using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, string> TrxIndex = new(StringComparer.OrdinalIgnoreCase);

    public sealed record TestRunResult(
        bool Succeeded,
        int ExitCode,
        string Runner,
        string StandardOutput,
        string StandardError,
        string? TrxPath,
        string? TrxToken);

    [McpServerTool, Description("Run 'dotnet build' for a solution or project using the detected Windows SDKs")]
    public static async Task<string> BuildSolution(
        [Description("Path to solution or project (.sln/.slnf/.csproj)")] string solutionPath,
        [Description("Build configuration (Debug/Release)")] string configuration = "Debug",
        [Description("Optional target framework (e.g., net10.0)")] string? framework = null,
        [Description("Optional runtime identifier (e.g., win-x64)")] string? runtimeIdentifier = null,
        [Description("Optional output directory override")] string? outputPath = null,
        [Description("Specific dotnet SDK version to use (defaults to latest installed)")] string? sdkVersion = null,
        [Description("Compile Razor/MVC views (adds RazorCompileOnBuild/MvcBuildViews)")] bool compileViews = false,
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
                AdditionalArguments: NormalizeArgs(ApplyCompileViews(additionalArguments, compileViews)));

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
    public static async Task<TestRunResult> TestSolution(
        [Description("Path to solution or project (.sln/.slnf/.csproj)")] string solutionPath,
        [Description("Test configuration (Debug/Release)")] string configuration = "Debug",
        [Description("Optional target framework (e.g., net10.0)")] string? framework = null,
        [Description("Optional runtime identifier (e.g., win-x64)")] string? runtimeIdentifier = null,
        [Description("Optional output directory override")] string? outputPath = null,
        [Description("Specific dotnet SDK version to use (defaults to latest installed)")] string? sdkVersion = null,
        [Description("Collect TRX logs (passes --logger:trx and returns token/path)")] bool collectTrx = false,
        [Description("Additional dotnet command arguments")]
        string[]? additionalArguments = null,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var service = serviceProvider?.GetService<BuildExecutionService>();
        if (service == null)
        {
            return new TestRunResult(false, -1, "dotnet", string.Empty, "Build execution service unavailable.", null, null);
        }

        var args = new List<string>();
        string? trxPath = null;
        string? trxToken = null;

        if (collectTrx)
        {
            var resultsDir = Path.Combine(Path.GetTempPath(), "roslyn-mcp-trx");
            Directory.CreateDirectory(resultsDir);
            var fileName = $"test-{Guid.NewGuid():N}.trx";
            trxPath = Path.Combine(resultsDir, fileName);
            trxToken = Guid.NewGuid().ToString("N");

            args.Add("--results-directory");
            args.Add(resultsDir);
            args.Add($"--logger:trx;LogFileName={fileName}");
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

            if (collectTrx && !string.IsNullOrEmpty(trxToken) && !string.IsNullOrEmpty(trxPath))
            {
                TrxIndex[trxToken] = trxPath;
            }

            return new TestRunResult(
                result.Succeeded,
                result.ExitCode,
                result.RunnerDescription,
                result.StandardOutput,
                result.StandardError,
                trxPath,
                trxToken);
        }
        catch (Exception ex)
        {
            LogError(serviceProvider, ex, "dotnet test failed");
            return new TestRunResult(
                false,
                -1,
                "dotnet",
                string.Empty,
                $"Error: {ex.Message} \nTry: increase tool_timeout_sec (e.g., 240s), stop running ASP.NET sessions that may lock binaries, add --no-restore, /p:RunAnalyzers=false, -m:1, or narrow tests with --filter.",
                trxPath,
                trxToken);
        }
    }

    [McpServerTool, Description("Return the TRX log content for a previous TestSolution run by token")]
    public static string GetTestTrx(
        [Description("Token returned by TestSolution when collectTrx=true")] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "Error: token is required.";
        }

        if (!TrxIndex.TryGetValue(token, out var path))
        {
            return $"No TRX found for token {token}.";
        }

        if (!File.Exists(path))
        {
            return $"TRX file not found at {path}. It may have been cleaned up.";
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return $"Error reading TRX: {ex.Message}";
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

    private static string[]? ApplyCompileViews(string[]? args, bool compileViews)
    {
        if (!compileViews)
        {
            return args;
        }

        var list = new List<string>();
        if (args != null)
        {
            list.AddRange(args.Where(a => !string.IsNullOrWhiteSpace(a)));
        }

        list.Add("/p:RazorCompileOnBuild=true");
        list.Add("/p:MvcBuildViews=true");
        return list.ToArray();
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
