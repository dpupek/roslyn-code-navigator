using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record RunnerSelection(
    string RunnerKind,
    string ExecutablePath,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> EnvironmentOverrides,
    string DisplayName);
