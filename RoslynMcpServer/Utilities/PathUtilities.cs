using System;

namespace RoslynMcpServer.Utilities;

public static class PathUtilities
{
    public static string? TranslateWindowsPathToUnix(string? windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath))
            return null;

        var trimmed = windowsPath.Trim();
        if (trimmed.Length < 2 || trimmed[1] != ':')
            return trimmed.Replace('\\', '/');

        var driveLetter = char.ToLowerInvariant(trimmed[0]);
        var remainder = trimmed.Substring(2).Replace('\\', '/').TrimStart('/');
        return $"/mnt/{driveLetter}/{remainder}";
    }
}
