using System;

namespace RoslynMcpServer.Models;

public sealed record ProcessExecutionResult(
    bool Succeeded,
    int ExitCode,
    TimeSpan Duration,
    string StandardOutput,
    string StandardError,
    bool WasCancelled,
    string RunnerDescription);
