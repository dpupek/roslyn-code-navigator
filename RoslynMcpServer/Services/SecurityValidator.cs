using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using RoslynMcpServer.Utilities;

namespace RoslynMcpServer.Services
{
    public class SecurityValidator
    {
        public readonly record struct SolutionValidationResult(bool IsValid, string? ErrorMessage, string? NormalizedPath)
        {
            public static SolutionValidationResult Success(string normalizedPath) => new(true, null, normalizedPath);
            public static SolutionValidationResult Failure(string message) => new(false, message, null);
        }

        private sealed record SdkInventory(IReadOnlyCollection<int> MajorVersions, IReadOnlyCollection<string> RawVersions)
        {
            public static SdkInventory Empty { get; } =
                new SdkInventory(Array.Empty<int>(), Array.Empty<string>());

            public bool HasData => MajorVersions.Count > 0;
            public string DisplayText => RawVersions.Count == 0
                ? "unknown"
                : string.Join(", ", RawVersions);
        }

        private readonly struct ProjectFrameworkInfo
        {
            public ProjectFrameworkInfo(string projectName, string projectPath, IReadOnlyCollection<string> targetFrameworks)
            {
                ProjectName = projectName;
                ProjectPath = projectPath;
                TargetFrameworks = targetFrameworks;
            }

            public string ProjectName { get; }
            public string ProjectPath { get; }
            public IReadOnlyCollection<string> TargetFrameworks { get; }
        }

        private readonly ILogger<SecurityValidator> _logger;
        private readonly bool _verboseLogging;
        private readonly HashSet<string> _allowedExtensions = new() { ".sln", ".csproj" };
        private readonly Regex _windowsPathPattern = new(@"^[a-zA-Z]:[\\/][^<>:|?*]+$", RegexOptions.Compiled);
        private readonly Regex _unixPathPattern = new(@"^/[^<>:|?*]+$", RegexOptions.Compiled);
        private static readonly Regex ProjectLineRegex = new(@"^\s*Project\("".+?""\)\s*=\s*""(?<name>[^""]+)"",\s*""(?<path>[^""]+)""",
            RegexOptions.Compiled);

        private readonly bool _isWindowsHost = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private readonly bool _isLinuxHost = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        private readonly bool _isWslHost = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                                           Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null;
        private readonly SdkInventory _sdkInventory;

        public SecurityValidator(ILogger<SecurityValidator> logger)
        {
            _logger = logger;
            _verboseLogging = IsVerboseSecurityLoggingEnabled();
            _sdkInventory = CaptureInstalledSdkInventory();
        }
        
        public SolutionValidationResult ValidateSolutionPath(string path) => ValidatePathInternal(path, requireSolutionExtension: true);

        public SolutionValidationResult ValidateFilePath(string path) => ValidatePathInternal(path, requireSolutionExtension: false);

        private SolutionValidationResult ValidatePathInternal(string path, bool requireSolutionExtension)
        {
            try
            {
                var workingPath = path;

                if (string.IsNullOrWhiteSpace(path))
                {
                    return Fail("Path was empty. Provide the absolute path to the target file.", path);
                }

                if (path.Contains("..") || path.Contains("~"))
                {
                    return Fail("Path contained unsupported traversal characters ('..' or '~'). Use an absolute path.", path);
                }

                var format = DeterminePathFormat(path);
                if (format == PathFormat.Unknown)
                {
                    return Fail("Path must be an absolute Windows (e.g., E:\\repo\\app.sln) or Unix (/mnt/e/repo/app.sln) path.", path);
                }

                if (!TryNormalizePathForHost(format, path, out workingPath, out var failureMessage, out var infoMessage))
                {
                    return Fail(failureMessage ?? "Path format is not supported on this host.", path);
                }

                if (!string.IsNullOrEmpty(infoMessage))
                {
                    _logger.LogInformation(infoMessage);
                }

                if (requireSolutionExtension)
                {
                    var extension = Path.GetExtension(workingPath);
                    if (!_allowedExtensions.Contains(extension))
                    {
                        return Fail($"Only .sln and .csproj files are supported. Received '{extension}'.", workingPath);
                    }
                }

                if (!File.Exists(workingPath))
                {
                    return Fail($"No file was found at '{workingPath}'. Double-check the path and try again.", workingPath);
                }

                if (requireSolutionExtension)
                {
                    var sdkResult = EnsureSdkCompatibility(workingPath);
                    if (!sdkResult.IsValid)
                    {
                        return sdkResult;
                    }
                }

                LogValidationSuccess(workingPath);
                return SolutionValidationResult.Success(workingPath);
            }
            catch (Exception ex)
            {
                return Fail("Unexpected error while validating the path. Check server logs for details.", path, ex);
            }
        }
        
        public string SanitizeSearchPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return "*";
            
            // Remove potentially dangerous characters
            return Regex.Replace(pattern, @"[^\w*?.]", "");
        }

        private void LogValidationFailure(string reason, string? path, Exception? exception = null)
        {
            if (!_verboseLogging)
            {
                return;
            }

            _logger.LogWarning(exception,
                "Solution path validation failed: {Reason}. Path: {Path}",
                reason,
                path ?? "<null>");
        }

        private void LogValidationSuccess(string path)
        {
            if (_verboseLogging)
            {
                _logger.LogDebug("Solution path validated successfully: {Path}", path);
            }
        }

        private SolutionValidationResult Fail(string message, string? path = null, Exception? exception = null)
        {
            LogValidationFailure(message, path, exception);
            return SolutionValidationResult.Failure(message);
        }

        private PathFormat DeterminePathFormat(string path)
        {
            if (_windowsPathPattern.IsMatch(path))
            {
                return PathFormat.Windows;
            }

            if (_unixPathPattern.IsMatch(path))
            {
                return PathFormat.Unix;
            }

            return PathFormat.Unknown;
        }

        private bool TryNormalizePathForHost(
            PathFormat format,
            string originalPath,
            out string normalizedPath,
            out string? failureMessage,
            out string? infoMessage)
        {
            normalizedPath = originalPath;
            failureMessage = null;
            infoMessage = null;

            switch (format)
            {
                case PathFormat.Windows:
                    if (_isWindowsHost)
                    {
                        return true;
                    }

                    if (_isWslHost && TryTranslateWindowsToWsl(originalPath, out var translated))
                    {
                        normalizedPath = translated;
                        infoMessage = $"Translated Windows path '{originalPath}' to '{translated}' for WSL host.";
                        return true;
                    }

                    failureMessage = _isWslHost
                        ? "The server is running inside WSL, but the provided Windows path could not be translated. Use a /mnt/<drive>/... path."
                        : "The server is running on Linux and cannot access Windows drive paths. Provide a Unix-style path.";
                    return false;

                case PathFormat.Unix:
                    if (!_isWindowsHost)
                    {
                        return true;
                    }

                    if (TryTranslateUnixToWindows(originalPath, out var windowsPath))
                    {
                        normalizedPath = windowsPath;
                        infoMessage = $"Translated Unix path '{originalPath}' to '{windowsPath}' for Windows host.";
                        return true;
                    }

                    failureMessage = "The server is running on Windows and requires drive-qualified paths (e.g., E:\\Repo\\App.sln).";
                    return false;

                default:
                    failureMessage = "Unsupported path format.";
                    return false;
            }
        }

        private SolutionValidationResult EnsureSdkCompatibility(string solutionPath)
        {
            if (!_sdkInventory.HasData)
            {
                return SolutionValidationResult.Success(solutionPath);
            }

            var projects = EnumerateProjects(solutionPath);
            if (projects.Count == 0)
            {
                return SolutionValidationResult.Success(solutionPath);
            }

            var missingSdks = new Dictionary<int, List<string>>();

            foreach (var project in projects)
            {
                foreach (var tfm in project.TargetFrameworks)
                {
                    if (TryGetNetCoreMajor(tfm, out var major))
                    {
                        if (!_sdkInventory.MajorVersions.Contains(major))
                        {
                            if (!missingSdks.TryGetValue(major, out var list))
                            {
                                list = new List<string>();
                                missingSdks[major] = list;
                            }

                            list.Add(project.ProjectName);
                        }
                        continue;
                    }

                    if (IsNetFrameworkTfm(tfm))
                    {
                        if (!_isWindowsHost)
                        {
                            return Fail($"The solution targets the .NET Framework ({tfm}), but the Roslyn MCP server is running on Linux/WSL. Please run the server on Windows to load this solution.", solutionPath);
                        }

                        if (!HasReferenceAssemblies(tfm))
                        {
                            return Fail($"The solution targets the .NET Framework ({tfm}), but the matching reference assemblies were not found under 'C:\\Program Files (x86)\\Reference Assemblies\\Microsoft\\Framework\\.NETFramework'. Install the appropriate targeting pack and restart the server.", solutionPath);
                        }
                    }
                }
            }

            if (missingSdks.Count > 0)
            {
                var builder = new StringBuilder();
                foreach (var kvp in missingSdks.OrderBy(k => k.Key))
                {
                    builder.Append($"net{kvp.Key}.x (");
                    builder.Append(string.Join(", ", kvp.Value.Distinct(StringComparer.OrdinalIgnoreCase)));
                    builder.Append("), ");
                }

                var missingDescription = builder.ToString().TrimEnd(',', ' ');
                var message = $"Solution targets {missingDescription}, but the installed SDKs are {_sdkInventory.DisplayText}. Install the missing .NET SDK(s) and restart the server.";
                return Fail(message, solutionPath);
            }

            return SolutionValidationResult.Success(solutionPath);
        }

        private List<ProjectFrameworkInfo> EnumerateProjects(string solutionPath)
        {
            var projects = new List<ProjectFrameworkInfo>();
            try
            {
                var solutionDirectory = Path.GetDirectoryName(solutionPath);
                if (string.IsNullOrEmpty(solutionDirectory))
                {
                    return projects;
                }

                foreach (var line in File.ReadLines(solutionPath))
                {
                    var match = ProjectLineRegex.Match(line);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var relativePath = match.Groups["path"].Value;
                    if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                        !relativePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var projectName = match.Groups["name"].Value;
                    var normalizedRelative = relativePath.Replace('\\', Path.DirectorySeparatorChar)
                                                          .Replace('/', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(solutionDirectory, normalizedRelative));

                    if (!File.Exists(fullPath))
                    {
                        continue;
                    }

                    var frameworks = ReadTargetFrameworks(fullPath);
                    projects.Add(new ProjectFrameworkInfo(projectName, fullPath, frameworks));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect solution '{SolutionPath}' for target frameworks. SDK validation skipped.", solutionPath);
                return new List<ProjectFrameworkInfo>();
            }

            return projects;
        }

        private IReadOnlyCollection<string> ReadTargetFrameworks(string projectPath)
        {
            try
            {
                using var stream = File.OpenRead(projectPath);
                var document = XDocument.Load(stream);

                var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var element in document.Descendants().Where(e => IsFrameworkElement(e.Name.LocalName)))
                {
                    var tfmValue = element.Value;
                    AddFrameworkValues(tfmValue, frameworks);
                }

                if (frameworks.Count == 0)
                {
                    var versionElement = document.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "TargetFrameworkVersion");
                    if (versionElement != null)
                    {
                        frameworks.Add(versionElement.Value.Trim());
                    }
                }

                return frameworks.Count == 0
                    ? Array.Empty<string>()
                    : frameworks;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to parse project '{ProjectPath}' for target frameworks.", projectPath);
                return Array.Empty<string>();
            }
        }

        private static bool IsFrameworkElement(string localName)
        {
            return localName.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase) ||
                   localName.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddFrameworkValues(string value, HashSet<string> bucket)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bucket.Add(part);
            }
        }

        private static bool TryGetNetCoreMajor(string tfm, out int major)
        {
            major = 0;
            if (string.IsNullOrWhiteSpace(tfm))
            {
                return false;
            }

            if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var remainder = tfm.Substring(3);
            if (remainder.StartsWith("standard", StringComparison.OrdinalIgnoreCase) ||
                remainder.StartsWith("core", StringComparison.OrdinalIgnoreCase) ||
                remainder.StartsWith("framework", StringComparison.OrdinalIgnoreCase) ||
                remainder.StartsWith("4", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var numericBuilder = new StringBuilder();
            foreach (var ch in remainder)
            {
                if (char.IsDigit(ch) || ch == '.')
                {
                    numericBuilder.Append(ch);
                }
                else
                {
                    break;
                }
            }

            if (numericBuilder.Length == 0)
            {
                return false;
            }

            if (Version.TryParse(numericBuilder.ToString(), out var version) && version.Major >= 5)
            {
                major = version.Major;
                return true;
            }

            return false;
        }

        private static bool IsNetFrameworkTfm(string tfm)
        {
            if (string.IsNullOrWhiteSpace(tfm))
            {
                return false;
            }

            if (tfm.StartsWith("net4", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return tfm.StartsWith("v4", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasReferenceAssemblies(string tfm)
        {
            var suffix = NormalizeFrameworkVersionToken(tfm);
            var referenceRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Reference Assemblies",
                "Microsoft",
                "Framework",
                ".NETFramework",
                suffix);

            return Directory.Exists(referenceRoot);
        }

        private static string NormalizeFrameworkVersionToken(string tfm)
        {
            if (string.IsNullOrWhiteSpace(tfm))
            {
                return tfm;
            }

            if (tfm.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                return tfm;
            }

            if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            {
                return tfm;
            }

            var digits = new string(tfm.Substring(3).Where(char.IsDigit).ToArray());
            if (digits.Length == 0)
            {
                return tfm;
            }

            var builder = new StringBuilder("v");
            builder.Append(digits[0]);
            for (var i = 1; i < digits.Length; i++)
            {
                builder.Append('.');
                builder.Append(digits[i]);
            }

            return builder.ToString();
        }

        private bool TryTranslateUnixToWindows(string path, out string windowsPath)
        {
            return PathUtilities.TryTranslateUnixPathToWindows(path, out windowsPath);
        }

        private bool TryTranslateWindowsToWsl(string path, out string wslPath)
        {
            wslPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || path[1] != ':' || !char.IsLetter(path[0]))
            {
                return false;
            }

            var drive = char.ToLowerInvariant(path[0]);
            var remainder = path.Substring(2).TrimStart('\\', '/');
            if (remainder.Length == 0)
            {
                return false;
            }

            var unix = remainder.Replace('\\', '/');
            wslPath = $"/mnt/{drive}/{unix}";
            return true;
        }

        private SdkInventory CaptureInstalledSdkInventory()
        {
            try
            {
                var startInfo = new ProcessStartInfo("dotnet", "--list-sdks")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return SdkInventory.Empty;
                }

                var readTask = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                    _logger.LogWarning("dotnet --list-sdks timed out; SDK validation will be skipped.");
                    return SdkInventory.Empty;
                }

                var output = readTask.GetAwaiter().GetResult();

                var versions = new List<string>();
                var majors = new HashSet<int>();

                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('[', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                    {
                        continue;
                    }

                    var versionText = parts[0].Trim();
                    if (string.IsNullOrWhiteSpace(versionText))
                    {
                        continue;
                    }

                    var sanitized = versionText.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
                    if (Version.TryParse(sanitized, out var parsed))
                    {
                        majors.Add(parsed.Major);
                        versions.Add(versionText);
                    }
                }

                return versions.Count == 0
                    ? SdkInventory.Empty
                    : new SdkInventory(majors, versions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate installed .NET SDKs; SDK validation disabled.");
                return SdkInventory.Empty;
            }
        }

        private enum PathFormat
        {
            Unknown = 0,
            Windows = 1,
            Unix = 2
        }

        private static bool IsVerboseSecurityLoggingEnabled()
        {
            var value = Environment.GetEnvironmentVariable("ROSLYN_VERBOSE_SECURITY_LOGS");
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("t", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
