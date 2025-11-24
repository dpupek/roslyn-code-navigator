using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcpServer.Models;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

[McpServerToolType]
public static class BuildTools
{
    [McpServerTool, Description("Run 'dotnet build' for a solution or project using the detected Windows SDKs")]
    public static async Task<string> BuildSolution(
        [Description("Path to solution or project (.sln/.slnf/.csproj)")] string solutionPath,
        [Description("Build configuration (Debug/Release)")] string configuration = "Debug",
        [Description("Optional target framework (e.g., net10.0)")] string? framework = null,
        [Description("Optional runtime identifier (e.g., win-x64)")] string? runtimeIdentifier = null,
        [Description("Optional output directory override")] string? outputPath = null,
        [Description("Specific dotnet SDK version to use (defaults to latest installed)")] string? sdkVersion = null,
        [Description("Additional dotnet command arguments")]
        string[]? additionalArguments = null,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var service = serviceProvider?.GetService<BuildExecutionService>();
        if (service == null)
        {
            return "Error: Build execution service unavailable.";
        }

        try
        {
            var request = new DotnetCommandRequest(
                Command: "build",
                TargetPath: solutionPath,
                WorkingDirectory: null,
                Configuration: configuration,
                Framework: framework,
                RuntimeIdentifier: runtimeIdentifier,
                OutputPath: outputPath,
                SdkVersion: sdkVersion,
                AdditionalArguments: NormalizeArgs(additionalArguments));

            var result = await service.RunDotnetBuildAsync(request, cancellationToken).ConfigureAwait(false);
            return FormatResult(result);
        }
        catch (Exception ex)
        {
            LogError(serviceProvider, ex, "dotnet build failed");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Run 'dotnet test' for a solution or project using the detected Windows SDKs")]
    public static async Task<string> TestSolution(
        [Description("Path to solution or project (.sln/.slnf/.csproj)")] string solutionPath,
        [Description("Test configuration (Debug/Release)")] string configuration = "Debug",
        [Description("Optional target framework (e.g., net10.0)")] string? framework = null,
        [Description("Optional runtime identifier (e.g., win-x64)")] string? runtimeIdentifier = null,
        [Description("Optional output directory override")] string? outputPath = null,
        [Description("Specific dotnet SDK version to use (defaults to latest installed)")] string? sdkVersion = null,
        [Description("Collect TRX logs (passes --logger:trx)")] bool collectTrx = false,
        [Description("Additional dotnet command arguments")]
        string[]? additionalArguments = null,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var service = serviceProvider?.GetService<BuildExecutionService>();
        if (service == null)
        {
            return "Error: Build execution service unavailable.";
        }

        var args = new List<string>();
        if (collectTrx)
        {
            args.Add("--logger:trx");
        }
        if (additionalArguments != null)
        {
            args.AddRange(additionalArguments);
        }

        try
        {
            var request = new DotnetCommandRequest(
                Command: "test",
                TargetPath: solutionPath,
                WorkingDirectory: null,
                Configuration: configuration,
                Framework: framework,
                RuntimeIdentifier: runtimeIdentifier,
                OutputPath: outputPath,
                SdkVersion: sdkVersion,
                AdditionalArguments: args);

            var result = await service.RunDotnetTestAsync(request, cancellationToken).ConfigureAwait(false);
            return FormatResult(result);
        }
        catch (Exception ex)
        {
            LogError(serviceProvider, ex, "dotnet test failed");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Run Visual Studio MSBuild for legacy .NET Framework solutions/projects")]
    public static async Task<string> LegacyMsBuild(
        [Description("Path to solution/project (.sln/.slnf/.csproj)")] string projectOrSolutionPath,
        [Description("MSBuild targets (semicolon separated)")] string targets = "Rebuild",
        [Description("MSBuild properties in key=value;key=value form")] string properties = "Configuration=Debug;Platform=AnyCPU",
        [Description("Preferred Visual Studio instance name (optional)")] string? preferredInstance = null,
        [Description("Additional MSBuild arguments")]
        string[]? additionalArguments = null,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var service = serviceProvider?.GetService<BuildExecutionService>();
        if (service == null)
        {
            return "Error: Build execution service unavailable.";
        }

        try
        {
            var request = new MsbuildCommandRequest(
                ProjectOrSolutionPath: projectOrSolutionPath,
                Targets: ParseList(targets),
                Properties: ParseProperties(properties),
                AdditionalArguments: NormalizeArgs(additionalArguments),
                PreferredInstance: preferredInstance);

            var result = await service.RunMsbuildAsync(request, cancellationToken).ConfigureAwait(false);
            return FormatResult(result);
        }
        catch (Exception ex)
        {
            LogError(serviceProvider, ex, "Legacy MSBuild failed");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Run vstest.console.exe for .NET Framework test assemblies")]
    public static async Task<string> LegacyVsTest(
        [Description("List of test assembly paths (.dll)")] string[] testAssemblies,
        [Description("Preferred Visual Studio instance name (optional)")] string? preferredInstance = null,
        [Description("Target .NET Framework moniker (e.g., .NETFramework,Version=v4.6.2)")] string? framework = null,
        [Description("Target platform (x86/x64/AnyCPU)")] string? platform = null,
        [Description("Run tests in isolation (adds /InIsolation)")] bool inIsolation = false,
        [Description("Additional vstest arguments")]
        string[]? additionalArguments = null,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var service = serviceProvider?.GetService<BuildExecutionService>();
        if (service == null)
        {
            return "Error: Build execution service unavailable.";
        }

        if (testAssemblies == null || testAssemblies.Length == 0)
        {
            return "Error: Provide at least one test assembly path.";
        }

        try
        {
            var request = new VsTestCommandRequest(
                TestAssemblyPaths: testAssemblies,
                Framework: framework,
                Platform: platform,
                InIsolation: inIsolation,
                AdditionalArguments: NormalizeArgs(additionalArguments),
                PreferredInstance: preferredInstance);

            var result = await service.RunVsTestAsync(request, cancellationToken).ConfigureAwait(false);
            return FormatResult(result);
        }
        catch (Exception ex)
        {
            LogError(serviceProvider, ex, "Legacy vstest failed");
            return $"Error: {ex.Message}";
        }
    }

    private static IReadOnlyList<string> NormalizeArgs(string[]? args)
    {
        return args is null || args.Length == 0
            ? Array.Empty<string>()
            : args.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
    }

    private static IReadOnlyList<string> ParseList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string?> ParseProperties(string value)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return dict;
        }

        var parts = value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var kvp = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kvp.Length == 2)
            {
                dict[kvp[0]] = kvp[1];
            }
            else if (kvp.Length == 1)
            {
                dict[kvp[0]] = string.Empty;
            }
        }

        return dict;
    }

    private static string FormatResult(ProcessExecutionResult result)
    {
        var status = result.WasCancelled
            ? "Cancelled"
            : result.Succeeded ? "Succeeded" : "Failed";

        var builder = new StringBuilder();
        builder.AppendLine($"Status: {status}");
        builder.AppendLine($"Runner: {result.RunnerDescription}");
        builder.AppendLine($"Exit Code: {result.ExitCode}");
        builder.AppendLine($"Duration: {result.Duration}");

        AppendSection(builder, "StdOut", result.StandardOutput);
        AppendSection(builder, "StdErr", result.StandardError);

        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string heading, string content, int maxLength = 2000)
    {
        builder.AppendLine($"{heading}:");
        if (string.IsNullOrWhiteSpace(content))
        {
            builder.AppendLine("  <empty>");
            return;
        }

        var trimmed = content.Length > maxLength ? content.Substring(0, maxLength) + "..." : content;
        foreach (var line in trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append("  ");
            builder.AppendLine(line);
        }
    }

    private static void LogError(IServiceProvider? serviceProvider, Exception exception, string message)
    {
        var factory = serviceProvider?.GetService<ILoggerFactory>();
        factory?.CreateLogger("BuildTools").LogError(exception, message);
    }
}
