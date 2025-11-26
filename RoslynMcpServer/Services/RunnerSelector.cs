using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;
using RoslynMcpServer.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RoslynMcpServer.Services;

public sealed class RunnerSelector
{
    private readonly StartupEnvironmentInfo _environmentInfo;
    private readonly ILogger<RunnerSelector> _logger;
    private readonly bool _isWindowsHost = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public RunnerSelector(StartupEnvironmentInfo environmentInfo, ILogger<RunnerSelector> logger)
    {
        _environmentInfo = environmentInfo;
        _logger = logger;
    }

    public RunnerSelection SelectDotnetRunner(string? requestedVersion)
    {
        var sdks = _environmentInfo.Toolchain.DotnetSdks
            .Where(sdk => sdk.Exists)
            .ToList();

        if (sdks.Count == 0)
        {
            throw new InvalidOperationException("No dotnet SDKs were detected on this host. Run roslyn_env to verify installation.");
        }

        var candidate = requestedVersion == null
            ? sdks.OrderByDescending(sdk => ParseVersionOrDefault(sdk.Version)).First()
            : ResolveRequestedSdkVersion(sdks, requestedVersion);

        var executablePath = ResolveDotnetExecutable(candidate.BasePath);
        var environment = BuildDotnetEnvironmentOverrides(candidate.BasePath);

        var description = requestedVersion == null
            ? $"dotnet ({candidate.Version})"
            : $"dotnet ({candidate.Version}, requested {requestedVersion})";

        return new RunnerSelection("dotnet", executablePath, null, environment, description);
    }

    public RunnerSelection SelectMsbuildRunner(string? preferredInstance)
    {
        if (_environmentInfo.Toolchain.VisualStudioInstallations.Count == 0)
        {
            var fallback = FindDefaultMsbuildPath();
            if (fallback != null)
            {
                var translatedPath = TranslateExecutable(fallback);
                var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MSBUILDDISABLENODEREUSE"] = "1",
                };
                return new RunnerSelection("msbuild", translatedPath, null, env, "MSBuild (fallback)");
            }
        }

        var instance = ResolveVisualStudioInstance(preferredInstance, requireMsbuildPath: true, "MSBuild");
        var executable = instance.MsbuildPath!;
        var translated = TranslateExecutable(executable);

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };

        return new RunnerSelection("msbuild", translated, null, environment, $"MSBuild ({instance.Name} {instance.Version})");
    }

    public RunnerSelection SelectVsTestRunner(string? preferredInstance)
    {
        var instance = ResolveVisualStudioInstance(preferredInstance, requireMsbuildPath: false, "vstest.console");
        var vstestPath = Path.Combine(instance.InstallationPath, "Common7", "IDE", "CommonExtensions", "Microsoft", "TestWindow", "vstest.console.exe");

        if (!File.Exists(vstestPath))
        {
            throw new InvalidOperationException($"Could not find vstest.console.exe under '{vstestPath}'. Ensure Visual Studio Test tools are installed.");
        }

        var translated = TranslateExecutable(vstestPath);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };

        return new RunnerSelection("vstest", translated, null, environment, $"vstest.console ({instance.Name} {instance.Version})");
    }

    private string? FindDefaultMsbuildPath()
    {
        if (!_isWindowsHost)
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Professional", "MSBuild", "Current", "Bin", "MSBuild.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Enterprise", "MSBuild", "Current", "Bin", "MSBuild.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Community", "MSBuild", "Current", "Bin", "MSBuild.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "BuildTools", "MSBuild", "Current", "Bin", "MSBuild.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2019", "Professional", "MSBuild", "Current", "Bin", "MSBuild.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2019", "Community", "MSBuild", "Current", "Bin", "MSBuild.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2019", "BuildTools", "MSBuild", "Current", "Bin", "MSBuild.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static Version ParseVersionOrDefault(string versionText)
    {
        return Version.TryParse(versionText, out var parsed)
            ? parsed
            : new Version(0, 0);
    }

    private DotnetSdkInfo ResolveRequestedSdkVersion(IEnumerable<DotnetSdkInfo> sdks, string requestedVersion)
    {
        DotnetSdkInfo? exact = null;
        DotnetSdkInfo? prefix = null;

        foreach (var sdk in sdks)
        {
            if (string.Equals(sdk.Version, requestedVersion, StringComparison.OrdinalIgnoreCase))
            {
                exact = sdk;
                break;
            }

            if (sdk.Version.StartsWith(requestedVersion, StringComparison.OrdinalIgnoreCase))
            {
                prefix ??= sdk;
            }
        }

        if (exact != null)
        {
            return exact;
        }

        if (prefix != null)
        {
            return prefix;
        }

        var available = string.Join(", ", sdks.Select(s => s.Version));
        throw new InvalidOperationException($"Requested dotnet SDK version '{requestedVersion}' was not found. Available versions: {available}.");
    }

    private string ResolveDotnetExecutable(string sdkBasePath)
    {
        // sdkBasePath typically looks like C:\Program Files\dotnet\sdk
        var dotnetRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(sdkBasePath, ".."));
        var executable = System.IO.Path.Combine(dotnetRoot, "dotnet.exe");
        return TranslateExecutable(executable);
    }

    private IReadOnlyDictionary<string, string?> BuildDotnetEnvironmentOverrides(string sdkBasePath)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };

        var dotnetRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(sdkBasePath, ".."));
        overrides["DOTNET_ROOT"] = dotnetRoot;

        if (_isWindowsHost)
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                var x86Path = System.IO.Path.Combine(programFilesX86, "dotnet");
                overrides["DOTNET_ROOT(x86)"] = x86Path;
            }
        }

        return overrides;
    }

    private VisualStudioInstallationInfo ResolveVisualStudioInstance(string? preferredInstance, bool requireMsbuildPath, string runnerName)
    {
        var installations = _environmentInfo.Toolchain.VisualStudioInstallations;
        if (installations.Count == 0)
        {
            throw new InvalidOperationException("No Visual Studio installations detected. Install VS Build Tools or specify DOTNET SDK runner instead.");
        }

        VisualStudioInstallationInfo? selected = null;

        if (!string.IsNullOrWhiteSpace(preferredInstance))
        {
            selected = installations.FirstOrDefault(v => v.Name.Equals(preferredInstance, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                throw new InvalidOperationException($"Requested Visual Studio instance '{preferredInstance}' was not found.");
            }
        }
        else
        {
            selected = installations
                .OrderByDescending(v => Version.TryParse(v.Version, out var parsed) ? parsed : new Version(0, 0))
                .FirstOrDefault();
        }

        if (selected == null)
        {
            throw new InvalidOperationException("Unable to select a Visual Studio installation.");
        }

        if (requireMsbuildPath && string.IsNullOrWhiteSpace(selected.MsbuildPath))
        {
            throw new InvalidOperationException($"Selected Visual Studio instance '{selected.Name}' does not provide an MSBuild path.");
        }

        return selected;
    }

    private string TranslateExecutable(string windowsPath)
    {
        if (_isWindowsHost)
        {
            return windowsPath;
        }

        var translated = PathUtilities.TranslateWindowsPathToUnix(windowsPath);
        if (translated is null)
        {
            _logger.LogWarning("Failed to translate executable path '{Path}' to Unix form; returning original for runner.", windowsPath);
            return windowsPath;
        }

        return translated;
    }
}
