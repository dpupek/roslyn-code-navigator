using System.Collections.Generic;

namespace RoslynMcpServer.Models;

public sealed record StartupEnvironmentInfo(
    string OSDescription,
    string ProcessArchitecture,
    string FrameworkDescription,
    string RuntimeVersion,
    string? MsbuildPath,
    string MsbuildSource,
    IReadOnlyDictionary<string, string?> EnvironmentVariables,
    ToolchainInventory Toolchain);
