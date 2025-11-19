using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using RoslynMcpServer.Models;

namespace RoslynMcpServer.Tools;

[McpServerToolType]
public static class EnvironmentTools
{
    [McpServerTool, Description("Show Roslyn MCP server runtime and MSBuild environment details")]
    public static string RoslynEnv(IServiceProvider? serviceProvider = null)
    {
        var info = serviceProvider?.GetService<StartupEnvironmentInfo>();
        if (info == null)
        {
            return "Environment information is not available.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"OS: {info.OSDescription} ({info.ProcessArchitecture})");
        builder.AppendLine($"Runtime: {info.RuntimeVersion} ({info.FrameworkDescription})");
        builder.AppendLine($"MSBuild Source: {info.MsbuildSource}");
        builder.AppendLine($"MSBuild Path: {info.MsbuildPath ?? "<auto-detected>"}");

        if (info.EnvironmentVariables.Any())
        {
            builder.AppendLine("Environment variables:");
            foreach (var kvp in info.EnvironmentVariables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"  {kvp.Key}={(string.IsNullOrEmpty(kvp.Value) ? "<not set>" : kvp.Value)}");
            }
        }

        var toolchain = info.Toolchain;

        builder.AppendLine();
        builder.AppendLine("dotnet SDKs:");
        if (toolchain.DotnetSdks.Count == 0)
        {
            builder.AppendLine("  <none detected>");
        }
        else
        {
            foreach (var sdk in toolchain.DotnetSdks)
            {
                var existsLabel = sdk.Exists ? string.Empty : " (missing)";
                builder.AppendLine($"  {sdk.Version} -> {sdk.FullPath}{existsLabel}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("dotnet runtimes:");
        if (toolchain.DotnetRuntimes.Count == 0)
        {
            builder.AppendLine("  <none detected>");
        }
        else
        {
            foreach (var runtime in toolchain.DotnetRuntimes)
            {
                var existsLabel = runtime.Exists ? string.Empty : " (missing)";
                builder.AppendLine($"  {runtime.Name} {runtime.Version} -> {runtime.FullPath}{existsLabel}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Visual Studio / MSBuild instances:");
        if (toolchain.VisualStudioInstallations.Count == 0)
        {
            builder.AppendLine("  <none detected>");
        }
        else
        {
            foreach (var vs in toolchain.VisualStudioInstallations)
            {
                builder.AppendLine($"  {vs.Name} {vs.Version} [{vs.DiscoveryType}] - MSBuild: {vs.MsbuildPath ?? "<n/a>"}");
            }
        }

        if (toolchain.SharedRuntimeProbePaths.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Shared runtime probe paths:");
            foreach (var path in toolchain.SharedRuntimeProbePaths)
            {
                builder.AppendLine($"  {path}");
            }
        }

        return builder.ToString();
    }

    [McpServerTool, Description("List the dotnet SDKs, runtimes, and Visual Studio toolchains detected at startup")]
    public static ToolchainInventory ListBuildRunners(IServiceProvider? serviceProvider = null)
    {
        var info = serviceProvider?.GetService<StartupEnvironmentInfo>();
        if (info == null)
        {
            return new ToolchainInventory(
                Array.Empty<DotnetSdkInfo>(),
                Array.Empty<DotnetRuntimeInfo>(),
                Array.Empty<VisualStudioInstallationInfo>(),
                Array.Empty<string>());
        }

        return info.Toolchain;
    }
}
