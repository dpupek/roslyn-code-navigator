using System;
using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record RunSession(
    string Token,
    string ProjectPath,
    string? LaunchProfile,
    IReadOnlyList<string> Urls,
    int ProcessId,
    DateTimeOffset StartedAt,
    string RunnerDescription,
    string? RecentOutput,
    string? LogFilePath);

public sealed record RunOperationResult(
    bool Succeeded,
    string Message,
    int? ExitCode = null,
    string? Output = null,
    string?[]? Suggestions = null,
    RunSession? Session = null);

public sealed record RunOutputSnapshot(
    IReadOnlyList<string> StdOut,
    IReadOnlyList<string> StdErr,
    IReadOnlyList<string> Combined,
    bool Truncated);

public sealed record LaunchProfilesResult(
    IReadOnlyList<LaunchProfileInfo> Profiles,
    string? Message);
