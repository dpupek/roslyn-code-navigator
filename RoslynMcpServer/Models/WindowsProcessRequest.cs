using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record WindowsProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> EnvironmentOverrides,
    string RunnerDescription);
