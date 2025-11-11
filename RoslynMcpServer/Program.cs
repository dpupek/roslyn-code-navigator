using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Microsoft.Build.Locator;
using RoslynMcpServer.Services;
using RoslynMcpServer.Utilities;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

            // Register MSBuild before any workspace operations
            // This is required for Roslyn to find MSBuild
            if (!MSBuildLocator.IsRegistered)
            {
                try
                {
                    MSBuildLocator.RegisterDefaults();
                    tempLogger.LogInformation("MSBuild registered successfully");
                }
                catch (Exception ex)
                {
                    tempLogger.LogError(ex, "Failed to register MSBuild: {Message}", ex.Message);
                    Environment.Exit(1);
                }
            }

            ConfigureEnvironment(tempLogger);

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
            builder.Services.AddSingleton<CodeAnalysisService>();
            builder.Services.AddSingleton<SymbolSearchService>();
            builder.Services.AddSingleton<SecurityValidator>();
            builder.Services.AddSingleton<DiagnosticLogger>();
            builder.Services.AddSingleton<IncrementalAnalyzer>();
            builder.Services.AddSingleton<IPersistentCache, FilePersistentCache>();
            builder.Services.AddSingleton<MultiLevelCacheManager>();
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
    }
}
