namespace RoslynMcpServer.Models;

public sealed record VisualStudioInstallationInfo(
    string Name,
    string Version,
    string DiscoveryType,
    string InstallationPath,
    string? MsbuildPath);
