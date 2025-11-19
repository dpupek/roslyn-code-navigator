namespace RoslynMcpServer.Models;

public sealed record DotnetRuntimeInfo(
    string Name,
    string Version,
    string BasePath,
    string FullPath,
    bool Exists);
