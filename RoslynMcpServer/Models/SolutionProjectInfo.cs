using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record SolutionProjectInfo(
    string Name,
    string FilePath,
    IReadOnlyList<string> TargetFrameworks,
    string? AssemblyName,
    string? ProjectId);
