using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using RoslynMcpServer.Models;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

[McpServerToolType]
public static class AspNetRunTools
{
    [McpServerTool, Description("Start an ASP.NET project via dotnet run and return a stop token + URLs")]
    public static async Task<RunOperationResult> StartAspNet(
        [Description("Path to ASP.NET project (.csproj)")] string projectPath,
        [Description("Launch profile name (optional)")] string? launchProfile = null,
        [Description("Build configuration")] string configuration = "Debug",
        [Description("Target framework (optional)")] string? framework = null,
        [Description("Override URLs (e.g., http://localhost:5005;https://localhost:7005)")] string? urls = null,
        [Description("Pass --no-build (default true)")] bool noBuild = true,
        [Description("Log stdout/stderr to a temp file; returns path in session")] bool logToFile = false,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var service = serviceProvider?.GetService<RunExecutionService>();
        if (service is null)
        {
            throw new InvalidOperationException("Run execution service unavailable.");
        }

        return await service.StartAsync(projectPath, launchProfile, configuration, framework, urls, noBuild, logToFile, cancellationToken)
            .ConfigureAwait(false);
    }

    [McpServerTool, Description("Stop a running ASP.NET project started via StartAspNet")]
    public static async Task<RunOperationResult> StopAspNet(
        [Description("Token returned by StartAspNet")] string token,
        [Description("Timeout in seconds before forcing kill (default 60)")] int timeoutSeconds = 60,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var service = serviceProvider?.GetService<RunExecutionService>();
        if (service is null)
        {
            return new RunOperationResult(false, "Run execution service unavailable.");
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 60 : timeoutSeconds);
        return await service.StopAsync(token, timeout, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool, Description("List launch profiles from Properties/launchSettings.json")]
    public static LaunchProfilesResult ListLaunchProfiles(
        [Description("Path to ASP.NET project (.csproj)")] string projectPath,
        IServiceProvider? serviceProvider = null)
    {
        var service = serviceProvider?.GetService<RunExecutionService>();
        if (service is null)
        {
            return new LaunchProfilesResult(Array.Empty<LaunchProfileInfo>(), "Run execution service unavailable.");
        }

        return service.ListLaunchProfiles(projectPath);
    }

    [McpServerTool, Description("List active ASP.NET run sessions started via StartAspNet")]
    public static IReadOnlyCollection<RunSession> ListAspNetSessions(IServiceProvider? serviceProvider = null)
    {
        var service = serviceProvider?.GetService<RunExecutionService>();
        if (service is null)
        {
            return Array.Empty<RunSession>();
        }

        return service.ListSessions();
    }

    [McpServerTool, Description("List recently exited ASP.NET run sessions")] 
    public static IReadOnlyCollection<RunSession> ListAspNetRecentSessions(
        [Description("Maximum sessions to return (default 10)")] int maxCount = 10,
        IServiceProvider? serviceProvider = null)
    {
        var service = serviceProvider?.GetService<RunExecutionService>();
        if (service is null)
        {
            return Array.Empty<RunSession>();
        }

        return service.ListRecentlyExited(maxCount);
    }

    [McpServerTool, Description("Return recent console output for a running ASP.NET session")]
    public static RunOutputSnapshot GetAspNetOutput(
        [Description("Token returned by StartAspNet")] string token,
        [Description("Maximum lines to return (defaults to 200)")] int maxLines = 200,
        IServiceProvider? serviceProvider = null)
    {
        var service = serviceProvider?.GetService<RunExecutionService>();
        if (service is null)
        {
            return new RunOutputSnapshot(Array.Empty<string>(), Array.Empty<string>(), new[] { "Run execution service unavailable." }, false);
        }

        return service.GetOutput(token, maxLines <= 0 ? 200 : maxLines);
    }
}
