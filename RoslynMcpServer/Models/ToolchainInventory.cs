using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record ToolchainInventory(
    IReadOnlyList<DotnetSdkInfo> DotnetSdks,
    IReadOnlyList<DotnetRuntimeInfo> DotnetRuntimes,
    IReadOnlyList<VisualStudioInstallationInfo> VisualStudioInstallations,
    IReadOnlyList<string> SharedRuntimeProbePaths);
