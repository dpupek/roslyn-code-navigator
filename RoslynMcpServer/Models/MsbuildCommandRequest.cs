using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record MsbuildCommandRequest(
    string ProjectOrSolutionPath,
    IReadOnlyList<string> Targets,
    IReadOnlyDictionary<string, string?> Properties,
    IReadOnlyList<string> AdditionalArguments,
    string? PreferredInstance);
