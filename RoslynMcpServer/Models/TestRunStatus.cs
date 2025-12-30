using System;
using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record TestRunStartResult(
    bool Succeeded,
    string Message,
    string? RunId = null,
    string? Runner = null,
    DateTimeOffset? StartedAt = null,
    string? LogFilePath = null,
    string? TrxPath = null,
    string? TrxToken = null);

public sealed record TestRunStatus(
    string RunId,
    string State,
    string TargetPath,
    string Runner,
    DateTimeOffset StartedAt,
    TimeSpan? Duration,
    int? ExitCode,
    string? StdOutTail,
    string? StdErrTail,
    string? LogFilePath,
    string? TrxPath,
    string? TrxToken);

public sealed record TestRunStatusResult(
    bool Succeeded,
    string Message,
    TestRunStatus? Status = null);

public sealed record TestRunListResult(
    IReadOnlyList<TestRunStatus> Runs);
