using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;
using RoslynMcpServer.Utilities;
using System.Runtime.InteropServices;

namespace RoslynMcpServer.Services;

public sealed class RunExecutionService
{
    private readonly RunnerSelector _runnerSelector;
    private readonly SecurityValidator _securityValidator;
    private readonly ILogger<RunExecutionService> _logger;
    private readonly bool _isWindowsHost = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private readonly Func<ProcessStartInfo, Process> _processFactory;
    private readonly ConcurrentDictionary<string, RunSessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<RunSessionState> _recentlyExited = new();
    private readonly int _tailCapacity;
    private readonly JobObjectManager? _jobManager;
    private readonly string _markerBaseDirectory;

    public RunExecutionService(
        RunnerSelector runnerSelector,
        SecurityValidator securityValidator,
        ILogger<RunExecutionService> logger,
        Func<ProcessStartInfo, Process>? processFactory = null)
    {
        _runnerSelector = runnerSelector;
        _securityValidator = securityValidator;
        _logger = logger;
        _processFactory = processFactory ?? CreateProcess;
        _tailCapacity = ResolveTailCapacity();
        _jobManager = _isWindowsHost ? new JobObjectManager() : null;
        _markerBaseDirectory = Path.Combine(Path.GetTempPath(), "roslyn-mcp-runs");
        Directory.CreateDirectory(_markerBaseDirectory);
    }

    public async Task<RunOperationResult> StartAsync(
        string projectPath,
        string? launchProfile,
        string configuration,
        string? framework,
        string? urlsOverride,
        bool noBuild,
        bool logToFile,
        CancellationToken cancellationToken)
    {
        var validation = _securityValidator.ValidateSolutionPath(projectPath);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            return Fail(validation.ErrorMessage ?? "Project path validation failed.");
        }

        var hostPath = validation.NormalizedPath;
        var windowsPath = EnsureWindowsPath(hostPath);
        var markerDir = GetMarkerDirectory(hostPath);
        CleanupOrphans(markerDir);

        var runner = _runnerSelector.SelectDotnetRunner(requestedVersion: null);
        var workingDirectory = DetermineWorkingDirectory(windowsPath);

        var selectedProfile = launchProfile;
        var profileResult = ListLaunchProfiles(hostPath);
        var profiles = profileResult.Profiles;
        if (profiles.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(selectedProfile))
            {
                selectedProfile = profiles[0].Name;
            }
            else if (!profiles.Any(p => string.Equals(p.Name, selectedProfile, StringComparison.OrdinalIgnoreCase)))
            {
                var available = string.Join(", ", profiles.Select(p => p.Name));
                return Fail($"Launch profile '{selectedProfile}' was not found.", suggestions: new[] { $"Available profiles: {available}" });
            }
        }
        else if (!string.IsNullOrWhiteSpace(selectedProfile))
        {
            return Fail($"Launch profile '{selectedProfile}' was requested but no launchSettings.json profiles were found.", suggestions: new[] { "Create Properties/launchSettings.json or omit launchProfile." });
        }

        if (!string.IsNullOrWhiteSpace(profileResult.Message) && profiles.Count == 0)
        {
            return Fail(profileResult.Message);
        }

        var resolvedUrls = ResolveUrls(urlsOverride, selectedProfile, profiles);

        var arguments = BuildDotnetArguments(windowsPath, selectedProfile, configuration, framework, resolvedUrls, noBuild);
        var startInfo = new ProcessStartInfo
        {
            FileName = runner.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var kvp in runner.EnvironmentOverrides)
        {
            if (kvp.Value is null)
            {
                continue;
            }

            startInfo.Environment[kvp.Key] = kvp.Value;
        }

        if (!string.IsNullOrWhiteSpace(urlsOverride))
        {
            startInfo.Environment["ASPNETCORE_URLS"] = urlsOverride!;
        }

        var process = _processFactory(startInfo);
        process.EnableRaisingEvents = true;
        _jobManager?.Add(process);

        var token = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;

        var stdoutTail = new LimitedConcurrentQueue<string>(_tailCapacity);
        var stderrTail = new LimitedConcurrentQueue<string>(_tailCapacity);
        var combinedTail = new LimitedConcurrentQueue<string>(_tailCapacity);

        StreamWriter? logWriter = null;
        string? logPath = null;
        if (logToFile)
        {
            logPath = Path.Combine(Path.GetTempPath(), $"aspnet-run-{token}.log");
            logWriter = new StreamWriter(File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }

        try
        {
            if (!process.Start())
            {
                return Fail("Failed to start dotnet run process.");
            }
        }
        catch
        {
            logWriter?.Dispose();
            process.Dispose();
            return Fail("Failed to start dotnet run process (process launch error)");
        }

        var stdoutTask = Task.Run(() => ReadStream(process.StandardOutput, stdoutTail, combinedTail, logWriter, cancellationToken));
        var stderrTask = Task.Run(() => ReadStream(process.StandardError, stderrTail, combinedTail, logWriter, cancellationToken));

        // Detect immediate failure (process exits within 1 second)
        if (process.WaitForExit(1000))
        {
            var output = await stdoutTask.ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);
            logWriter?.Dispose();
            process.Dispose();
            var combined = string.Join(Environment.NewLine, new[] { output, error }.Where(s => !string.IsNullOrEmpty(s)));
            var friendly = FriendlyBindMessage(combined);
            return Fail(friendly.message, process.ExitCode, combined, friendly.suggestions);
        }

        var session = new RunSession(token, hostPath, selectedProfile, resolvedUrls, process.Id, startedAt, runner.DisplayName, FormatTail(combinedTail), logPath);
        var state = new RunSessionState(session, process, stdoutTail, stderrTail, combinedTail, stdoutTask, stderrTask, logWriter, markerDir);
        _sessions[token] = state;
        CreateMarker(process.Id, token, hostPath, markerDir);

        process.Exited += async (_, _) =>
        {
            if (_sessions.TryRemove(token, out var removed))
            {
                EnqueueRecentExit(removed);
            }
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
            }
            RemoveMarker(process.Id, markerDir);
            logWriter?.Dispose();
            process.Dispose();
        };

        return new RunOperationResult(true, "Started", null, null, null, session);
    }

    public Task<RunOperationResult> StopAsync(string token, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(token, out var state))
        {
            return Task.FromResult(Fail($"No active session found for token {token}. It may have already exited.", suggestions: new[] { "Call ListAspNetSessions to see active tokens." }));
        }

        var pid = state.Session.ProcessId;

        try
        {
            if (IsProcessDisposed(state.Process) || state.Process.HasExited)
            {
                return Task.FromResult(new RunOperationResult(true, $"Session {token} already exited.", ExitCode: state.Process.ExitCode, Session: state.Session));
            }

            if (!state.Process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                state.Process.Kill(entireProcessTree: true);
                state.Process.WaitForExit();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop run session {Token} (PID {Pid}).", token, pid);
            return Task.FromResult(Fail($"Failed to stop process: {ex.Message}", suggestions: new[] { "Process may have already exited or permission denied.", "Check ListAspNetSessions and OS process list." }));
        }
        finally
        {
            EnqueueRecentExit(state);
            RemoveMarker(pid, state.MarkerDirectory);
            state.LogWriter?.Dispose();
            state.Process.Dispose();
        }

        return Task.FromResult(new RunOperationResult(true, $"Stopped session {token}.", ExitCode: state.Process.ExitCode, Session: state.Session));
    }

    public IReadOnlyCollection<RunSession> ListSessions()
    {
        var list = new List<RunSession>();
        foreach (var state in _sessions.Values)
        {
            if (!IsProcessDisposed(state.Process) && !state.Process.HasExited)
            {
                var updated = state.Session with { RecentOutput = FormatTail(state.CombinedTail) };
                list.Add(updated);
            }
        }

        return list;
    }

    public IReadOnlyCollection<RunSession> ListRecentlyExited(int maxCount)
    {
        return _recentlyExited.Take(maxCount <= 0 ? 10 : maxCount).Select(s => s.Session with { RecentOutput = FormatTail(s.CombinedTail) }).ToArray();
    }

    public RunOutputSnapshot GetOutput(string token, int maxLines)
    {
        if (!_sessions.TryGetValue(token, out var state))
        {
            return new RunOutputSnapshot(Array.Empty<string>(), Array.Empty<string>(), new[] { $"No active session for token {token}." }, false);
        }

        var stdout = state.StdOutTail.ToArray();
        var stderr = state.StdErrTail.ToArray();
        var combined = state.CombinedTail.ToArray();

        bool truncated = false;
        if (maxLines > 0)
        {
            if (stdout.Length > maxLines) { stdout = stdout[^maxLines..]; truncated = true; }
            if (stderr.Length > maxLines) { stderr = stderr[^maxLines..]; truncated = true; }
            if (combined.Length > maxLines) { combined = combined[^maxLines..]; truncated = true; }
        }

        if (stdout.Length == 0 && stderr.Length == 0 && combined.Length == 0)
        {
            combined = new[] { "<no output yet (app starting?)>" };
        }

        return new RunOutputSnapshot(stdout, stderr, combined, truncated);
    }

    private RunOperationResult Fail(string message, int? exitCode = null, string? output = null, string?[]? suggestions = null)
    {
        return new RunOperationResult(false, message, exitCode, output, suggestions);
    }

    public LaunchProfilesResult ListLaunchProfiles(string projectPath)
    {
        var validation = _securityValidator.ValidateFilePath(projectPath);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            return new LaunchProfilesResult(Array.Empty<LaunchProfileInfo>(), validation.ErrorMessage ?? "Path validation failed.");
        }

        var directory = Path.GetDirectoryName(validation.NormalizedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new LaunchProfilesResult(Array.Empty<LaunchProfileInfo>(), "Could not resolve project directory.");
        }

        var launchSettings = Path.Combine(directory, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettings))
        {
            _logger.LogInformation("No launchSettings.json found at {Path}.", launchSettings);
            return new LaunchProfilesResult(Array.Empty<LaunchProfileInfo>(), "No launchSettings.json found.");
        }

        try
        {
            using var stream = File.OpenRead(launchSettings);
            using var json = JsonDocument.Parse(stream);
            if (!json.RootElement.TryGetProperty("profiles", out var profilesElement) || profilesElement.ValueKind != JsonValueKind.Object)
            {
                return new LaunchProfilesResult(Array.Empty<LaunchProfileInfo>(), "launchSettings.json did not contain a 'profiles' object.");
            }

            var profiles = new List<LaunchProfileInfo>();
            foreach (var profileProp in profilesElement.EnumerateObject())
            {
                var profileName = profileProp.Name;
                var profileObj = profileProp.Value;
                profileObj.TryGetProperty("commandName", out var commandNameElement);
                profileObj.TryGetProperty("applicationUrl", out var appUrlElement);

                var urls = ExtractUrls(appUrlElement);
                profiles.Add(new LaunchProfileInfo(profileName, commandNameElement.GetString(), urls));
            }

            return new LaunchProfilesResult(profiles, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read launch settings at {Path}.", launchSettings);
            return new LaunchProfilesResult(Array.Empty<LaunchProfileInfo>(), "Failed to parse launchSettings.json; see server logs.");
        }
    }

    private static IReadOnlyList<string> ExtractUrls(JsonElement appUrlElement)
    {
        if (appUrlElement.ValueKind == JsonValueKind.String)
        {
            return SplitUrls(appUrlElement.GetString());
        }

        if (appUrlElement.ValueKind == JsonValueKind.Array)
        {
            var urls = new List<string>();
            foreach (var item in appUrlElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    urls.AddRange(SplitUrls(item.GetString()));
                }
            }
            return urls;
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> SplitUrls(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private IReadOnlyList<string> ResolveUrls(string? urlsOverride, string? selectedProfile, IReadOnlyList<LaunchProfileInfo> profiles)
    {
        if (!string.IsNullOrWhiteSpace(urlsOverride))
        {
            return SplitUrls(urlsOverride);
        }

        if (string.IsNullOrWhiteSpace(selectedProfile))
        {
            return Array.Empty<string>();
        }

        var match = profiles.FirstOrDefault(p => string.Equals(p.Name, selectedProfile, StringComparison.OrdinalIgnoreCase));
        return match?.ApplicationUrls ?? Array.Empty<string>();
    }

    private IReadOnlyList<string> BuildDotnetArguments(string projectPath, string? profile, string configuration, string? framework, IReadOnlyList<string> urls, bool noBuild)
    {
        var args = new List<string> { "run", "--project", projectPath };

        if (!string.IsNullOrWhiteSpace(configuration))
        {
            args.Add("-c");
            args.Add(configuration);
        }

        if (!string.IsNullOrWhiteSpace(framework))
        {
            args.Add("-f");
            args.Add(framework);
        }

        if (!string.IsNullOrWhiteSpace(profile))
        {
            args.Add("--launch-profile");
            args.Add(profile!);
        }

        if (urls.Count > 0)
        {
            args.Add("--urls");
            args.Add(string.Join(';', urls));
        }

        if (noBuild)
        {
            args.Add("--no-build");
        }

        return args;
    }

    private string DetermineWorkingDirectory(string windowsPath)
    {
        var directory = Path.GetDirectoryName(windowsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Environment.CurrentDirectory;
        }

        if (_isWindowsHost)
        {
            return directory;
        }

        var translated = PathUtilities.TranslateWindowsPathToUnix(directory);
        return translated ?? directory;
    }

    private string EnsureWindowsPath(string hostPath)
    {
        if (_isWindowsHost)
        {
            return hostPath;
        }

        if (PathUtilities.TryTranslateUnixPathToWindows(hostPath, out var windowsPath))
        {
            return windowsPath;
        }

        _logger.LogWarning("Could not translate '{Path}' to a Windows path. Falling back to original path.", hostPath);
        return hostPath;
    }

    private static Process CreateProcess(ProcessStartInfo startInfo)
    {
        return new Process { StartInfo = startInfo };
    }

    private static async Task<string> ReadStream(StreamReader reader, LimitedConcurrentQueue<string> tail, LimitedConcurrentQueue<string> combinedTail, StreamWriter? logWriter, CancellationToken token)
    {
        var all = new List<string>();
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            all.Add(line);
            tail.Enqueue(line);
            combinedTail.Enqueue(line);
            if (logWriter != null)
            {
                await logWriter.WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        return string.Join(Environment.NewLine, all);
    }

    private static (string message, string?[]? suggestions) FriendlyBindMessage(string combined)
    {
        if (string.IsNullOrWhiteSpace(combined))
        {
            return ("dotnet run exited immediately. Check logs.", null);
        }

        var lower = combined.ToLowerInvariant();
        if (lower.Contains("address already in use") || lower.Contains("eaddrinuse") || lower.Contains("failed to bind"))
        {
            var endpoint = ExtractEndpoint(combined);
            return (
                endpoint is null
                    ? "dotnet run failed: address/port already in use."
                    : $"dotnet run failed: {endpoint} is already in use.",
                new[]
                {
                    "Pick a different --urls value (e.g., http://localhost:0).",
                    "Stop the conflicting process (another Kestrel/IISExpress instance)."
                });
        }

        if (lower.Contains("access is denied"))
        {
            return (
                "dotnet run failed: access denied binding to the requested port.",
                new[] { "Try a higher port (above 1024) or run with sufficient permissions." });
        }

        return ($"dotnet run exited immediately. Output follows:\n{combined}", null);
    }

    private static string? ExtractEndpoint(string text)
    {
        var idx = text.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var span = text.AsSpan(idx);
        var end = span.Length;
        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];
            if (ch == ' ' || ch == '\\' || ch == '"' || ch == '\'' || ch == '\n' || ch == '\r')
            {
                end = i;
                break;
            }
        }

        var url = span[..end].ToString();
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    private static bool IsProcessDisposed(Process process)
    {
        try
        {
            _ = process.Id;
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string? FormatTail(LimitedConcurrentQueue<string> tail)
    {
        var lines = tail.ToArray();
        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private void EnqueueRecentExit(RunSessionState state)
    {
        _recentlyExited.Enqueue(state);
        while (_recentlyExited.Count > 10 && _recentlyExited.TryDequeue(out _))
        {
        }
    }

    private int ResolveTailCapacity()
    {
        var text = Environment.GetEnvironmentVariable("ROSLYN_ASPNET_TAIL_LINES");
        if (int.TryParse(text, out var value) && value > 0)
        {
            return value;
        }

        return 50;
    }

    private void CleanupOrphans(string markerDirectory)
    {
        if (!Directory.Exists(markerDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(markerDirectory, "*.run"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!int.TryParse(name, out var pid))
            {
                File.Delete(file);
                continue;
            }

            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
            }
            catch
            {
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private void CreateMarker(int pid, string token, string projectPath, string markerDirectory)
    {
        try
        {
            Directory.CreateDirectory(markerDirectory);
            var path = Path.Combine(markerDirectory, $"{pid}.run");
            File.WriteAllText(path, token + "\n" + DateTimeOffset.UtcNow.ToString("O") + "\n" + projectPath);
        }
        catch
        {
        }
    }

    private void RemoveMarker(int pid, string markerDirectory)
    {
        try
        {
            var path = Path.Combine(markerDirectory, $"{pid}.run");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private string ComputeAgentId()
    {
        var baseDir = _markerBaseDirectory;
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(baseDir);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private string GetMarkerDirectory(string projectPath)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(projectPath);
        var hash = sha.ComputeHash(bytes);
        var suffix = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Path.Combine(_markerBaseDirectory, suffix);
    }

    private sealed record RunSessionState(
        RunSession Session,
        Process Process,
        LimitedConcurrentQueue<string> StdOutTail,
        LimitedConcurrentQueue<string> StdErrTail,
        LimitedConcurrentQueue<string> CombinedTail,
        Task<string> StdOutTask,
        Task<string> StdErrTask,
        StreamWriter? LogWriter,
        string MarkerDirectory);
}
