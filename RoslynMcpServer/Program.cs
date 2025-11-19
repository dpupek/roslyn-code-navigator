using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Microsoft.Build.Locator;
using RoslynMcpServer.Models;
using RoslynMcpServer.Services;
using RoslynMcpServer.Utilities;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.IO;
using System.Linq;

namespace RoslynMcpServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Create a temporary logger for early initialization
            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Information));
            var tempLogger = loggerFactory.CreateLogger<Program>();

            var msbuildInfo = RegisterMsbuild(tempLogger);

            ConfigureEnvironment(tempLogger);

            EnsureDotnetRoot(msbuildInfo, tempLogger);
            var runtimeProbingPaths = DetectSharedRuntimeDirectories(tempLogger, out var runtimeInfos);
            SetupAssemblyResolving(runtimeProbingPaths, tempLogger);

            var toolchain = CollectToolchainInventory(tempLogger, runtimeProbingPaths, runtimeInfos);

            var startupInfo = CaptureStartupInfo(msbuildInfo, toolchain);

            tempLogger.LogInformation("Process runtime: {RuntimeVersion} ({FrameworkDescription})", startupInfo.RuntimeVersion, startupInfo.FrameworkDescription);
            tempLogger.LogInformation("OS: {OSDescription} ({Architecture})", startupInfo.OSDescription, startupInfo.ProcessArchitecture);
            tempLogger.LogInformation("MSBuild SDK Path: {SdkPath} (Source: {Source})", startupInfo.MsbuildPath ?? "<auto-detected>", startupInfo.MsbuildSource);

            var builder = Host.CreateApplicationBuilder(args);

            // Configure logging for MCP integration - ensure all logs go to stderr
            var configuredLogLevel = GetConfiguredLogLevel();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(configuredLogLevel);
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = configuredLogLevel;
            });

            // Register services
            builder.Services.AddSingleton<StartupEnvironmentInfo>(startupInfo);
            builder.Services.AddSingleton<CodeAnalysisService>();
            builder.Services.AddSingleton<SymbolSearchService>();
            builder.Services.AddSingleton<SecurityValidator>();
            builder.Services.AddSingleton<DiagnosticLogger>();
            builder.Services.AddSingleton<IncrementalAnalyzer>();
            builder.Services.AddSingleton<IPersistentCache, FilePersistentCache>();
            builder.Services.AddSingleton<MultiLevelCacheManager>();
            builder.Services.AddSingleton<RunnerSelector>();
            builder.Services.AddSingleton<Utilities.WindowsProcessLauncher>();
            builder.Services.AddSingleton<BuildExecutionService>();
            builder.Services.AddMemoryCache();

            // Configure MCP server
            try
            {
                builder.Services
                    .AddMcpServer()
                    .WithStdioServerTransport()
                    .WithResourcesFromAssembly()
                    .WithToolsFromAssembly();

                var host = builder.Build();

                var logger = host.Services.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("Starting Roslyn MCP Server...");

                await host.RunAsync();
            }
            catch (Exception ex)
            {
                tempLogger.LogError(ex, "Failed to start MCP server: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }

        private const string MsbuildPathOverrideVariable = "ROSLYN_MSBUILD_SDK_PATH";
        private const string MsbuildDisableNodeReuseVariable = "MSBUILDDISABLENODEREUSE";
        private static readonly string[] EnvironmentSnapshotKeys = new[]
        {
            MsbuildPathOverrideVariable,
            MsbuildDisableNodeReuseVariable,
            "NUGET_PACKAGES",
            "NUGET_FALLBACK_PACKAGES",
            "RestoreAdditionalProjectFallbackFolders",
            "DOTNET_ROOT",
            "DOTNET_ROOT(x86)"
        };
        private record MsbuildRegistrationInfo(string? SdkPath, string Source);
        private static IReadOnlyList<DotnetSdkInfo>? _cachedDotnetSdks;
        private static IReadOnlyList<VisualStudioInstallationInfo>? _cachedVisualStudioInstallations;

        private static MsbuildRegistrationInfo RegisterMsbuild(ILogger logger)
        {
            if (MSBuildLocator.IsRegistered)
            {
                return new MsbuildRegistrationInfo(null, "AlreadyRegistered");
            }

            var overridePath = Environment.GetEnvironmentVariable(MsbuildPathOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                try
                {
                    var normalizedPath = Path.GetFullPath(overridePath);
                    if (!Directory.Exists(normalizedPath))
                    {
                        logger.LogError("The path specified by {Variable} ('{Path}') does not exist.", MsbuildPathOverrideVariable, normalizedPath);
                        Environment.Exit(1);
                    }

                    MSBuildLocator.RegisterMSBuildPath(normalizedPath);
                    logger.LogInformation("MSBuild registered from override path {Path} (via {Variable}).", normalizedPath, MsbuildPathOverrideVariable);
                    return new MsbuildRegistrationInfo(normalizedPath, "Override");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to register MSBuild from override path '{Path}'.", overridePath);
                    Environment.Exit(1);
                }
            }

            if (TryRegisterLatestDotnetSdk(logger, out var detectedSdkPath))
            {
                return new MsbuildRegistrationInfo(detectedSdkPath, "DotNetSdk");
            }

            try
            {
                var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
                if (instances.Length == 0)
                {
                    MSBuildLocator.RegisterDefaults();
                    logger.LogInformation("MSBuild registered via defaults (no instances enumerated).");
                    return new MsbuildRegistrationInfo(null, "Defaults");
                }

                VisualStudioInstance preferred;

                var sdkInstances = instances
                    .Where(instance => instance.DiscoveryType == DiscoveryType.DotNetSdk)
                    .OrderByDescending(instance => instance.Version)
                    .ToArray();

                if (sdkInstances.Length > 0)
                {
                    preferred = sdkInstances.First();
                }
                else
                {
                    preferred = instances
                        .OrderByDescending(instance => instance.Version)
                    .ThenBy(instance => instance.DiscoveryType == DiscoveryType.DotNetSdk ? 0 : 1)
                    .First();
                }

                MSBuildLocator.RegisterInstance(preferred);
                logger.LogInformation(
                    "MSBuild registered from {DiscoveryType} @ {Path} (Version {Version})",
                    preferred.DiscoveryType,
                    preferred.MSBuildPath,
                    preferred.Version);
                return new MsbuildRegistrationInfo(preferred.MSBuildPath, "VisualStudio");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register MSBuild: {Message}", ex.Message);
                Environment.Exit(1);
            }

            return new MsbuildRegistrationInfo(null, "Unknown");
        }

        private static bool TryRegisterLatestDotnetSdk(ILogger logger, out string? sdkPath)
        {
            sdkPath = null;

            var sdks = EnumerateDotnetSdks(logger);
            Version? bestVersion = null;
            DotnetSdkInfo? bestSdk = null;

            foreach (var sdk in sdks)
            {
                if (!Version.TryParse(sdk.Version, out var parsedVersion))
                {
                    continue;
                }

                if (!sdk.Exists)
                {
                    continue;
                }

                if (bestSdk == null || (bestVersion != null && parsedVersion > bestVersion))
                {
                    bestVersion = parsedVersion;
                    bestSdk = sdk;
                }
            }

            if (bestSdk?.FullPath == null)
            {
                return false;
            }

            MSBuildLocator.RegisterMSBuildPath(bestSdk.FullPath);
            logger.LogInformation("MSBuild registered from dotnet --list-sdks path {Path} (Version {Version}).", bestSdk.FullPath, bestSdk.Version);
            sdkPath = bestSdk.FullPath;
            return true;
        }

        private static LogLevel GetConfiguredLogLevel()
        {
            var logLevelValue = Environment.GetEnvironmentVariable("ROSLYN_LOG_LEVEL")
                ?? Environment.GetEnvironmentVariable("LOG_LEVEL");

            if (!string.IsNullOrWhiteSpace(logLevelValue) &&
                Enum.TryParse<LogLevel>(logLevelValue, ignoreCase: true, out var parsedLevel))
            {
                return parsedLevel;
            }

            return LogLevel.Information;
        }

        private static void ConfigureEnvironment(ILogger logger)
        {
            EnsureNuGetPackageRoot();
            EnsureMsbuildNodeReuseSetting(logger);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Environment.SetEnvironmentVariable("DisableImplicitNuGetFallbackFolder", "true");

                var windowsFallback = @"C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages";
                var translated = PathUtilities.TranslateWindowsPathToUnix(windowsFallback);
                if (!string.IsNullOrWhiteSpace(translated) && Directory.Exists(translated))
                {
                    Environment.SetEnvironmentVariable("NUGET_FALLBACK_PACKAGES", translated);
                    Environment.SetEnvironmentVariable("RestoreAdditionalProjectFallbackFolders", translated);
                }

                ValidateFallbackPaths(logger);
                return;
            }

            // Windows hosts rely on Visual Studio's shared NuGet cache. Validate that it exists
            var configuredFallbacks = GetConfiguredFallbackPaths();
            if (configuredFallbacks.Count == 0)
            {
                var defaultFallback = @"C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages";
                Environment.SetEnvironmentVariable("NUGET_FALLBACK_PACKAGES", defaultFallback);
                Environment.SetEnvironmentVariable("RestoreAdditionalProjectFallbackFolders", defaultFallback);
                configuredFallbacks.Add(defaultFallback);
            }

            ValidateFallbackPaths(logger);
        }

        private static ToolchainInventory CollectToolchainInventory(ILogger logger, IReadOnlyList<string> runtimeProbePaths, IReadOnlyList<DotnetRuntimeInfo> runtimeInfos)
        {
            var sdks = EnumerateDotnetSdks(logger);
            var visualStudios = EnumerateVisualStudioInstallations(logger);

            return new ToolchainInventory(sdks, runtimeInfos, visualStudios, runtimeProbePaths);
        }

        private static IReadOnlyList<DotnetSdkInfo> EnumerateDotnetSdks(ILogger logger)
        {
            if (_cachedDotnetSdks != null)
            {
                return _cachedDotnetSdks;
            }

            var sdks = new List<DotnetSdkInfo>();

            try
            {
                var psi = new ProcessStartInfo("dotnet", "--list-sdks")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(2000);

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var line in lines)
                        {
                            var split = line.Split('[', 2, StringSplitOptions.TrimEntries);
                            if (split.Length != 2)
                            {
                                continue;
                            }

                            var versionText = split[0].Trim();
                            if (string.IsNullOrWhiteSpace(versionText))
                            {
                                continue;
                            }

                            var basePath = split[1].TrimEnd(']').Trim();
                            if (string.IsNullOrWhiteSpace(basePath))
                            {
                                continue;
                            }

                            var fullPath = Path.Combine(basePath, versionText);
                            var exists = Directory.Exists(fullPath);
                            sdks.Add(new DotnetSdkInfo(versionText, basePath, fullPath, exists));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enumerate dotnet SDKs via dotnet --list-sdks.");
            }

            _cachedDotnetSdks = sdks.ToArray();
            return _cachedDotnetSdks;
        }

        private static IReadOnlyList<DotnetRuntimeInfo> EnumerateDotnetRuntimes(ILogger logger)
        {
            var runtimes = new List<DotnetRuntimeInfo>();

            try
            {
                var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return Array.Empty<DotnetRuntimeInfo>();
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);

                if (string.IsNullOrWhiteSpace(output))
                {
                    return Array.Empty<DotnetRuntimeInfo>();
                }

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var line in lines)
                {
                    var bracketIndex = line.IndexOf('[');
                    if (bracketIndex < 0)
                    {
                        continue;
                    }

                    var left = line.Substring(0, bracketIndex).Trim();
                    var pathPart = line.Substring(bracketIndex + 1).TrimEnd(']').Trim();

                    var tokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2)
                    {
                        continue;
                    }

                    var versionText = tokens[^1];
                    var name = string.Join(' ', tokens.Take(tokens.Length - 1));
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(versionText) || string.IsNullOrWhiteSpace(pathPart))
                    {
                        continue;
                    }

                    var fullPath = Path.Combine(pathPart, versionText);
                    var exists = Directory.Exists(fullPath);
                    runtimes.Add(new DotnetRuntimeInfo(name, versionText, pathPart, fullPath, exists));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enumerate dotnet runtimes via dotnet --list-runtimes.");
            }

            return runtimes.ToArray();
        }

        private static IReadOnlyList<VisualStudioInstallationInfo> EnumerateVisualStudioInstallations(ILogger logger)
        {
            if (_cachedVisualStudioInstallations != null)
            {
                return _cachedVisualStudioInstallations;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _cachedVisualStudioInstallations = Array.Empty<VisualStudioInstallationInfo>();
                return _cachedVisualStudioInstallations;
            }

            try
            {
                var instances = MSBuildLocator
                    .QueryVisualStudioInstances()
                    .Select(instance => new VisualStudioInstallationInfo(
                        instance.Name,
                        instance.Version.ToString(),
                        instance.DiscoveryType.ToString(),
                        instance.VisualStudioRootPath,
                        instance.MSBuildPath))
                    .ToArray();

                _cachedVisualStudioInstallations = instances;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enumerate Visual Studio installations.");
                _cachedVisualStudioInstallations = Array.Empty<VisualStudioInstallationInfo>();
            }

            return _cachedVisualStudioInstallations;
        }

        private static void EnsureMsbuildNodeReuseSetting(ILogger logger)
        {
            var existing = Environment.GetEnvironmentVariable(MsbuildDisableNodeReuseVariable);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return;
            }

            Environment.SetEnvironmentVariable(MsbuildDisableNodeReuseVariable, "1");
            logger.LogInformation("{Variable} not set; defaulting to 1 to prevent cross-process MSBuild node reuse.", MsbuildDisableNodeReuseVariable);
        }

        private static void EnsureNuGetPackageRoot()
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
            {
                return;
            }

            var defaultPackages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");

            Environment.SetEnvironmentVariable("NUGET_PACKAGES", defaultPackages);
        }

        private static List<string> GetConfiguredFallbackPaths()
        {
            var values = new[]
            {
                Environment.GetEnvironmentVariable("NUGET_FALLBACK_PACKAGES"),
                Environment.GetEnvironmentVariable("RestoreAdditionalProjectFallbackFolders")
            };

            var paths = new List<string>();
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var split = value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var path in split)
                {
                    if (!string.IsNullOrWhiteSpace(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        paths.Add(path);
                    }
                }
            }

            return paths;
        }

        private static void ValidateFallbackPaths(ILogger logger)
        {
            var fallbackPaths = GetConfiguredFallbackPaths();
            if (fallbackPaths.Count == 0)
            {
                return;
            }

            var missing = fallbackPaths
                .Where(path => !Directory.Exists(path))
                .ToList();

            if (missing.Count == 0)
            {
                return;
            }

            foreach (var path in missing)
            {
                logger.LogError("Required NuGet fallback folder '{FallbackPath}' does not exist.", path);
            }

            logger.LogError("Set NUGET_FALLBACK_PACKAGES or RestoreAdditionalProjectFallbackFolders to a valid folder before starting the server.");
            Environment.Exit(1);
        }

        private static StartupEnvironmentInfo CaptureStartupInfo(MsbuildRegistrationInfo msbuildInfo, ToolchainInventory toolchain)
        {
            var env = new Dictionary<string, string?>();
            foreach (var key in EnvironmentSnapshotKeys)
            {
                env[key] = Environment.GetEnvironmentVariable(key);
            }

            return new StartupEnvironmentInfo(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.Version.ToString(),
                msbuildInfo.SdkPath,
                msbuildInfo.Source,
                env,
                toolchain);
        }

        private static void EnsureDotnetRoot(MsbuildRegistrationInfo msbuildInfo, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
            {
                var candidate = TryResolveDotnetRoot(msbuildInfo.SdkPath);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    Environment.SetEnvironmentVariable("DOTNET_ROOT", candidate);
                    logger.LogInformation("DOTNET_ROOT not set; defaulting to {Path}", candidate);
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)")))
            {
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrWhiteSpace(programFilesX86))
                {
                    var x86Path = Path.Combine(programFilesX86, "dotnet");
                    if (Directory.Exists(x86Path))
                    {
                        Environment.SetEnvironmentVariable("DOTNET_ROOT(x86)", x86Path);
                        logger.LogInformation("DOTNET_ROOT(x86) not set; defaulting to {Path}", x86Path);
                    }
                }
            }
        }

        private static string? TryResolveDotnetRoot(string? sdkPath)
        {
            if (!string.IsNullOrWhiteSpace(sdkPath))
            {
                var sdkDirectory = Path.GetDirectoryName(sdkPath);
                if (!string.IsNullOrWhiteSpace(sdkDirectory))
                {
                    var rootCandidate = Path.GetFullPath(Path.Combine(sdkDirectory, ".."));
                    if (Directory.Exists(rootCandidate))
                    {
                        return rootCandidate;
                    }
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrWhiteSpace(programFiles))
                {
                    var defaultPath = Path.Combine(programFiles, "dotnet");
                    if (Directory.Exists(defaultPath))
                    {
                        return defaultPath;
                    }
                }
            }

            return null;
        }

        private static IReadOnlyList<string> DetectSharedRuntimeDirectories(ILogger logger, out IReadOnlyList<DotnetRuntimeInfo> runtimeInfos)
        {
            runtimeInfos = EnumerateDotnetRuntimes(logger);

            return runtimeInfos
                .Where(r => r.Exists)
                .Select(r => r.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void SetupAssemblyResolving(IReadOnlyList<string> probingDirectories, ILogger logger)
        {
            if (probingDirectories.Count == 0)
            {
                logger.LogWarning("No shared runtime directories detected for assembly probing.");
                return;
            }

            AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
            {
                foreach (var directory in probingDirectories)
                {
                    var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            return context.LoadFromAssemblyPath(candidate);
                        }
                        catch
                        {
                            // ignore and continue searching
                        }
                    }
                }

                return null;
            };

            logger.LogInformation("Configured assembly probing across {Count} shared runtime directories.", probingDirectories.Count);
        }
    }
}
