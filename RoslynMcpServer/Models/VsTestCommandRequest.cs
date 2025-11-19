using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record VsTestCommandRequest(
    IReadOnlyList<string> TestAssemblyPaths,
    string? Framework,
    string? Platform,
    bool InIsolation,
    IReadOnlyList<string> AdditionalArguments,
    string? PreferredInstance);
