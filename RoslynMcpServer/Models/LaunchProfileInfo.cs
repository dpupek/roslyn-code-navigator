using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record LaunchProfileInfo(
    string Name,
    string? CommandName,
    IReadOnlyList<string> ApplicationUrls);
