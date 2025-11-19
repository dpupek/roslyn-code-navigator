using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record DotnetCommandRequest(
    string Command,
    string TargetPath,
    string? WorkingDirectory,
    string? Configuration,
    string? Framework,
    string? RuntimeIdentifier,
    string? OutputPath,
    string? SdkVersion,
    IReadOnlyList<string> AdditionalArguments);
