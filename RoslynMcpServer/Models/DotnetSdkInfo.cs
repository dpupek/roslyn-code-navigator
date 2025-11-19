namespace RoslynMcpServer.Models;

public sealed record DotnetSdkInfo(
    string Version,
    string BasePath,
    string FullPath,
    bool Exists);
