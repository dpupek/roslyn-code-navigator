using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;
using RoslynMcpServer.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace RoslynMcpServer.Services;

public sealed class TestRunExecutionService
{
    private readonly RunnerSelector _runnerSelector;
    private readonly SecurityValidator _securityValidator;
    private readonly ILogger<TestRunExecutionService> _logger;
    private readonly bool _isWindowsHost = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private readonly Func<ProcessStartInfo, Process> _processFactory;
    private readonly ConcurrentDictionary<string, TestRunState> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<TestRunState> _recentlyExited = new();
    private readonly int _tailCapacity;
    private readonly string _defaultLogDirectory;

    public TestRunExecutionService(
        RunnerSelector runnerSelector,
        SecurityValidator securityValidator,
        ILogger<TestRunExecutionService> logger,
        Func<ProcessStartInfo, Process>? processFactory = null)
    {
        _runnerSelector = runnerSelector;
        _securityValidator = securityValidator;
        _logger = logger;
        _processFactory = processFactory ?? CreateProcess;
        _tailCapacity = ResolveTailCapacity();
        _defaultLogDirectory = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests");
    }

    public TestRunStartResult StartAsync(
        DotnetCommandRequest request,
        bool collectTrx,
        string? logDirectory,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new TestRunStartResult(false, "Start was cancelled.");
        }

        var validation = _securityValidator.ValidateSolutionPath(request.TargetPath);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            return new TestRunStartResult(false, validation.ErrorMessage ?? "Solution path validation failed.");
        }

        var logDir = ResolveLogDirectory(logDirectory);
        if (logDir is null)
        {
            return new TestRunStartResult(false, "Log directory validation failed.");
        }
        var windowsLogDir = EnsureWindowsPath(logDir);

        var hostPath = validation.NormalizedPath;
        var windowsPath = EnsureWindowsPath(hostPath);
        var runner = _runnerSelector.SelectDotnetRunner(request.SdkVersion);
        var workingDirectory = !string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? request.WorkingDirectory!
            : DetermineWorkingDirectory(windowsPath);

        var trxPath = default(string);
        var trxToken = default(string);
        var arguments = BuildDotnetArguments(
            request.Command,
            windowsPath,
            request,
            collectTrx,
            logDir,
            windowsLogDir,
            out trxPath,
            out trxToken);
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

        NonInteractiveEnvironment.Apply(startInfo.Environment);

        _logger.LogInformation(
            "Preparing test run for {TargetPath}. Runner={Runner} ({RunnerPath}). WorkingDir={WorkingDirectory} CollectTrx={CollectTrx} LogDir={LogDirectory}.",
            hostPath,
            runner.DisplayName,
            runner.ExecutablePath,
            workingDirectory,
            collectTrx,
            logDir);
        _logger.LogInformation(
            "dotnet args: {Arguments}",
            string.Join(" ", startInfo.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        if (collectTrx)
        {
            _logger.LogInformation("TRX output path: {TrxPath} (token {TrxToken})", trxPath ?? "<unknown>", trxToken ?? "<none>");
        }

        var process = _processFactory(startInfo);
        process.EnableRaisingEvents = true;

        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var stdoutTail = new LimitedConcurrentQueue<string>(_tailCapacity);
        var stderrTail = new LimitedConcurrentQueue<string>(_tailCapacity);

        var logFilePath = Path.Combine(logDir, $"test-run-{runId}.log");
        StreamWriter? logWriter = null;
        try
        {
            logWriter = new StreamWriter(File.Open(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open test run log at {Path}", logFilePath);
            logWriter = null;
            logFilePath = string.Empty;
        }

        try
        {
            if (!process.Start())
            {
                logWriter?.Dispose();
                process.Dispose();
                return new TestRunStartResult(false, "Failed to start dotnet test process.");
            }
        }
        catch (Exception ex)
        {
            logWriter?.Dispose();
            process.Dispose();
            return new TestRunStartResult(false, $"Failed to start dotnet test process: {ex.Message}");
        }

        _logger.LogInformation(
            "Started test run {RunId} (pid {ProcessId}) for {TargetPath} using {Runner}. Log: {LogFile}.",
            runId,
            process.Id,
            hostPath,
            runner.DisplayName,
            string.IsNullOrWhiteSpace(logFilePath) ? "<none>" : logFilePath);

        var runCts = new CancellationTokenSource();
        var stdoutTask = Task.Run(() => ReadStream(process.StandardOutput, stdoutTail, logWriter, runCts.Token));
        var stderrTask = Task.Run(() => ReadStream(process.StandardError, stderrTail, logWriter, runCts.Token));

        var state = new TestRunState(
            runId,
            hostPath,
            runner.DisplayName,
            startedAt,
            process,
            stdoutTail,
            stderrTail,
            stdoutTask,
            stderrTask,
            logWriter,
            logFilePath,
            trxPath,
            trxToken,
            runCts);

        _runs[runId] = state;

        process.Exited += async (_, _) =>
        {
            await FinalizeRunAsync(state, stdoutTask, stderrTask).ConfigureAwait(false);
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                _logger.LogWarning("Test run {RunId} wait-for-exit faulted.", runId);
            }

            await FinalizeRunAsync(state, stdoutTask, stderrTask).ConfigureAwait(false);
        });

        return new TestRunStartResult(
            true,
            "Started dotnet test.",
            runId,
            runner.DisplayName,
            startedAt,
            string.IsNullOrWhiteSpace(logFilePath) ? null : logFilePath,
            trxPath,
            trxToken);
    }

    public TestRunStatusResult GetStatus(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return new TestRunStatusResult(false, "RunId is required.");
        }

        if (_runs.TryGetValue(runId, out var active))
        {
            if (active.Process.HasExited)
            {
                _ = FinalizeRunAsync(active, active.StdOutTask, active.StdErrTask);
            }

            return new TestRunStatusResult(true, "OK", BuildStatus(active));
        }

        var completed = FindRecent(runId);
        if (completed is null)
        {
            return new TestRunStatusResult(false, $"No test run found for id {runId}.");
        }

        return new TestRunStatusResult(true, "OK", BuildStatus(completed));
    }

    public TestRunStatusResult Cancel(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return new TestRunStatusResult(false, "RunId is required.");
        }

        if (!_runs.TryGetValue(runId, out var state))
        {
            var completed = FindRecent(runId);
            if (completed is not null)
            {
                return new TestRunStatusResult(true, "Run already completed.", BuildStatus(completed));
            }

            return new TestRunStatusResult(false, $"No active test run found for id {runId}.");
        }

        state.MarkCancelRequested();
        try
        {
            if (!state.Process.HasExited)
            {
                state.Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        return new TestRunStatusResult(true, "Cancellation requested.", BuildStatus(state));
    }

    public TestRunListResult ListRuns(bool includeCompleted, int maxResults)
    {
        var results = new List<TestRunStatus>();

        foreach (var run in _runs.Values.OrderByDescending(r => r.StartedAt))
        {
            results.Add(BuildStatus(run));
            if (maxResults > 0 && results.Count >= maxResults)
            {
                return new TestRunListResult(results);
            }
        }

        if (!includeCompleted)
        {
            return new TestRunListResult(results);
        }

        foreach (var run in _recentlyExited.ToArray().OrderByDescending(r => r.StartedAt))
        {
            if (results.Any(r => string.Equals(r.RunId, run.RunId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(BuildStatus(run));
            if (maxResults > 0 && results.Count >= maxResults)
            {
                break;
            }
        }

        return new TestRunListResult(results);
    }

    private string? ResolveLogDirectory(string? logDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(logDirectory) ? _defaultLogDirectory : logDirectory!;
        var validation = _securityValidator.ValidateDirectoryPath(directory);
        return validation.IsValid ? validation.NormalizedPath : null;
    }

    private static IReadOnlyList<string> BuildDotnetArguments(
        string command,
        string windowsPath,
        DotnetCommandRequest request,
        bool collectTrx,
        string logDirectory,
        string windowsLogDirectory,
        out string? trxPath,
        out string? trxToken)
    {
        var args = new List<string> { command, windowsPath };
        trxPath = null;
        trxToken = null;

        if (!string.IsNullOrWhiteSpace(request.Configuration))
        {
            args.Add("-c");
            args.Add(request.Configuration!);
        }

        if (!string.IsNullOrWhiteSpace(request.Framework))
        {
            args.Add("-f");
            args.Add(request.Framework!);
        }

        if (!string.IsNullOrWhiteSpace(request.RuntimeIdentifier))
        {
            args.Add("-r");
            args.Add(request.RuntimeIdentifier!);
        }

        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            args.Add("-o");
            args.Add(request.OutputPath!);
        }

        if (collectTrx)
        {
            var resultsDir = Path.Combine(logDirectory, "trx");
            Directory.CreateDirectory(resultsDir);
            var fileName = $"test-{Guid.NewGuid():N}.trx";
            trxPath = Path.Combine(resultsDir, fileName);
            trxToken = Guid.NewGuid().ToString("N");

            var windowsResultsDir = CombineWindowsPath(windowsLogDirectory, "trx");
            args.Add("--results-directory");
            args.Add(windowsResultsDir);
            args.Add($"--logger:trx;LogFileName={fileName}");
        }

        if (request.AdditionalArguments.Count > 0)
        {
            args.AddRange(request.AdditionalArguments);
        }

        return args;
    }

    private static string CombineWindowsPath(string basePath, string child)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return child;
        }

        if (basePath.Contains(':') || basePath.Contains('\\'))
        {
            if (basePath.EndsWith("\\", StringComparison.Ordinal))
            {
                return basePath + child;
            }

            return basePath + "\\" + child;
        }

        return Path.Combine(basePath, child);
    }

    private async Task FinalizeRunAsync(TestRunState state, Task stdoutTask, Task stderrTask)
    {
        lock (state.Sync)
        {
            if (state.CleanupStarted)
            {
                return;
            }

            state.CleanupStarted = true;
            state.CompletedAt = DateTimeOffset.UtcNow;
            if (state.Process.HasExited)
            {
                state.ExitCode = state.Process.ExitCode;
            }
        }

        try
        {
            state.HasExitedSnapshot = state.Process.HasExited;
        }
        catch (Exception ex)
        {
            state.DiagnosticsFaulted = true;
            _logger.LogWarning(ex, "Failed to snapshot process state for test run {RunId}.", state.RunId);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(state.LogFilePath) && File.Exists(state.LogFilePath))
            {
                state.LastLogTimestampSnapshot = File.GetLastWriteTimeUtc(state.LogFilePath);
            }
        }
        catch (Exception ex)
        {
            state.DiagnosticsFaulted = true;
            _logger.LogWarning(ex, "Failed to snapshot log timestamp for test run {RunId}.", state.RunId);
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch
        {
            _logger.LogWarning("Test run {RunId} log readers faulted.", state.RunId);
        }

        var completedAt = state.CompletedAt ?? DateTimeOffset.UtcNow;
        var duration = completedAt - state.StartedAt;
        _logger.LogInformation(
            "Finalized test run {RunId} (pid {ProcessId}) state={State} exit={ExitCode} duration={Duration}.",
            state.RunId,
            state.Process.Id,
            state.CancelRequested ? "Cancelled" : state.ExitCode == 0 ? "Completed" : "Failed",
            state.ExitCode,
            duration);

        state.LogWriter?.Dispose();
        state.Process.Dispose();
        state.ProcessDisposed = true;

        _runs.TryRemove(state.RunId, out _);
        EnqueueRecentExit(state);
    }

    private TestRunState? FindRecent(string runId)
    {
        foreach (var run in _recentlyExited.ToArray())
        {
            if (string.Equals(run.RunId, runId, StringComparison.OrdinalIgnoreCase))
            {
                return run;
            }
        }

        return null;
    }

    private TestRunStatus BuildStatus(TestRunState state)
    {
        DateTimeOffset? completedAt;
        int? exitCode;
        bool cancelRequested;
        bool? hasExited = null;
        DateTimeOffset? lastLogTimestamp = null;
        IReadOnlyList<string>? failedTests = null;
        string? firstFailureMessage = null;
        var diagnosticsOk = true;

        lock (state.Sync)
        {
            completedAt = state.CompletedAt;
            exitCode = state.ExitCode;
            cancelRequested = state.CancelRequested;
            if (state.DiagnosticsFaulted)
            {
                diagnosticsOk = false;
            }
        }

        var duration = completedAt.HasValue ? completedAt.Value - state.StartedAt : (TimeSpan?)null;
        var status = completedAt is null
            ? "Running"
            : cancelRequested ? "Cancelled"
            : exitCode == 0 ? "Completed" : "Failed";

        if (state.HasExitedSnapshot.HasValue)
        {
            hasExited = state.HasExitedSnapshot;
        }
        else
        {
            try
            {
                hasExited = state.Process.HasExited;
            }
            catch (Exception ex)
            {
                diagnosticsOk = false;
                _logger.LogWarning(ex, "Failed to read process state for test run {RunId}.", state.RunId);
            }
        }

        if (state.LastLogTimestampSnapshot.HasValue)
        {
            lastLogTimestamp = state.LastLogTimestampSnapshot;
        }
        else
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(state.LogFilePath) && File.Exists(state.LogFilePath))
                {
                    lastLogTimestamp = File.GetLastWriteTimeUtc(state.LogFilePath);
                }
            }
            catch (Exception ex)
            {
                diagnosticsOk = false;
                _logger.LogWarning(ex, "Failed to read log timestamp for test run {RunId}.", state.RunId);
            }
        }

        if (!diagnosticsOk)
        {
            status = "Inconclusive";
        }

        if (!string.IsNullOrWhiteSpace(state.TrxPath) && File.Exists(state.TrxPath))
        {
            try
            {
                var summary = ParseTrxFailureSummary(state.TrxPath);
                if (summary is not null)
                {
                    failedTests = summary.FailedTests;
                    firstFailureMessage = summary.FirstFailureMessage;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse TRX summary for test run {RunId}.", state.RunId);
            }
        }

        return new TestRunStatus(
            state.RunId,
            status,
            state.TargetPath,
            state.RunnerDescription,
            state.StartedAt,
            duration,
            exitCode,
            hasExited,
            lastLogTimestamp,
            failedTests,
            firstFailureMessage,
            FormatTail(state.StdOutTail),
            FormatTail(state.StdErrTail),
            string.IsNullOrWhiteSpace(state.LogFilePath) ? null : state.LogFilePath,
            state.TrxPath,
            state.TrxToken);
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

    private static async Task<string> ReadStream(StreamReader reader, LimitedConcurrentQueue<string> tail, StreamWriter? logWriter, CancellationToken token)
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
            if (logWriter != null)
            {
                await logWriter.WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        return string.Join(Environment.NewLine, all);
    }

    private static string? FormatTail(LimitedConcurrentQueue<string> tail)
    {
        var lines = tail.ToArray();
        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private void EnqueueRecentExit(TestRunState state)
    {
        _recentlyExited.Enqueue(state);
        while (_recentlyExited.Count > 20 && _recentlyExited.TryDequeue(out _))
        {
        }
    }

    private int ResolveTailCapacity()
    {
        var text = Environment.GetEnvironmentVariable("ROSLYN_TEST_TAIL_LINES");
        if (int.TryParse(text, out var value) && value > 0)
        {
            return value;
        }

        return 50;
    }

    internal static TrxFailureSummary? ParseTrxFailureSummary(string trxPath)
    {
        var document = XDocument.Load(trxPath);
        if (document.Root is null)
        {
            return null;
        }

        var ns = document.Root.Name.Namespace;
        var failed = document.Descendants(ns + "UnitTestResult")
            .Where(element => string.Equals((string?)element.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase))
            .Select(element => (string?)element.Attribute("testName"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();

        if (failed.Count == 0)
        {
            return null;
        }

        var firstFailureMessage = document.Descendants(ns + "UnitTestResult")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase))?
            .Element(ns + "Output")?
            .Element(ns + "ErrorInfo")?
            .Element(ns + "Message")?
            .Value;

        return new TrxFailureSummary(failed, string.IsNullOrWhiteSpace(firstFailureMessage) ? null : firstFailureMessage);
    }

    private sealed class TestRunState
    {
        public TestRunState(
            string runId,
            string targetPath,
            string runnerDescription,
            DateTimeOffset startedAt,
            Process process,
            LimitedConcurrentQueue<string> stdOutTail,
            LimitedConcurrentQueue<string> stdErrTail,
            Task<string> stdOutTask,
            Task<string> stdErrTask,
            StreamWriter? logWriter,
            string logFilePath,
            string? trxPath,
            string? trxToken,
            CancellationTokenSource runCts)
        {
            RunId = runId;
            TargetPath = targetPath;
            RunnerDescription = runnerDescription;
            StartedAt = startedAt;
            Process = process;
            StdOutTail = stdOutTail;
            StdErrTail = stdErrTail;
            StdOutTask = stdOutTask;
            StdErrTask = stdErrTask;
            LogWriter = logWriter;
            LogFilePath = logFilePath;
            TrxPath = trxPath;
            TrxToken = trxToken;
            RunCts = runCts;
        }

        public string RunId { get; }
        public string TargetPath { get; }
        public string RunnerDescription { get; }
        public DateTimeOffset StartedAt { get; }
        public Process Process { get; }
        public LimitedConcurrentQueue<string> StdOutTail { get; }
        public LimitedConcurrentQueue<string> StdErrTail { get; }
        public Task<string> StdOutTask { get; }
        public Task<string> StdErrTask { get; }
        public StreamWriter? LogWriter { get; }
        public string LogFilePath { get; }
        public string? TrxPath { get; }
        public string? TrxToken { get; }
        public CancellationTokenSource RunCts { get; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int? ExitCode { get; set; }
        public bool CancelRequested { get; private set; }
        public bool CleanupStarted { get; set; }
        public bool? HasExitedSnapshot { get; set; }
        public DateTimeOffset? LastLogTimestampSnapshot { get; set; }
        public bool DiagnosticsFaulted { get; set; }
        public bool ProcessDisposed { get; set; }
        public object Sync { get; } = new();

        public void MarkCancelRequested()
        {
            lock (Sync)
            {
                CancelRequested = true;
                RunCts.Cancel();
            }
        }
    }

    internal sealed record TrxFailureSummary(
        IReadOnlyList<string> FailedTests,
        string? FirstFailureMessage);
}
