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

    public static bool TryTranslateUnixPathToWindows(string? unixPath, out string windowsPath)
    {
        windowsPath = string.Empty;
        if (string.IsNullOrWhiteSpace(unixPath))
        {
            return false;
        }

        if (!unixPath.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) || unixPath.Length < 7)
        {
            return false;
        }

        var driveLetter = unixPath[5];
        if (!char.IsLetter(driveLetter))
        {
            return false;
        }

        var remainder = unixPath.Substring(6).TrimStart('/');
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return false;
        }

        var converted = remainder.Replace('/', '\\');
        windowsPath = $"{char.ToUpperInvariant(driveLetter)}:\\{converted}";
        return true;
    }
}
