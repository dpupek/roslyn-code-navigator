using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools;

[McpServerToolType]
public static class SolutionTools
{
    [McpServerTool, Description("List projects in a solution with target frameworks")]
    public static async Task<string> ListProjects(
        [Description("Path to solution file (.sln/.slnf)")] string solutionPath,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var validator = serviceProvider?.GetService<SecurityValidator>();
        var validation = validator?.ValidateSolutionPath(solutionPath)
                        ?? SecurityValidator.SolutionValidationResult.Failure("Solution path validation failed.");
        if (!validation.IsValid)
        {
            return $"Error: {validation.ErrorMessage}";
        }

        var analysisService = serviceProvider?.GetService<CodeAnalysisService>();
        if (analysisService == null)
        {
            return "Error: Code analysis service not available.";
        }

        var normalizedPath = validation.NormalizedPath ?? solutionPath;
        var projects = await analysisService.GetSolutionOverviewAsync(normalizedPath, cancellationToken).ConfigureAwait(false);

        if (projects.Count == 0)
        {
            return "No projects were loaded for the specified solution.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Projects in {normalizedPath} ({projects.Count}):");

        foreach (var project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var frameworks = project.TargetFrameworks.Count > 0
                ? string.Join(", ", project.TargetFrameworks)
                : "<unknown>";
            builder.AppendLine($"- {project.Name}");
            builder.AppendLine($"    Path: {project.FilePath}");
            builder.AppendLine($"    Target Frameworks: {frameworks}");
        }

        return builder.ToString();
    }
}
