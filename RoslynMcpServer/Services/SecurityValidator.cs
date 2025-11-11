using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace RoslynMcpServer.Services
{
    public class SecurityValidator
    {
        private readonly ILogger<SecurityValidator> _logger;
        private readonly bool _verboseLogging;
        private readonly HashSet<string> _allowedExtensions = new() { ".sln", ".csproj" };
        private readonly Regex _windowsPathPattern = new(@"^[a-zA-Z]:[\\/][^<>:|?*]+$");
        private readonly Regex _unixPathPattern = new(@"^/[^<>:|?*]+$");

        public SecurityValidator(ILogger<SecurityValidator> logger)
        {
            _logger = logger;
            _verboseLogging = IsVerboseSecurityLoggingEnabled();
        }
        
        public bool ValidateSolutionPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    LogValidationFailure("Path was empty or whitespace.", path);
                    return false;
                }

                if (path.Contains("..") || path.Contains("~"))
                {
                    LogValidationFailure("Path traversal characters detected.", path);
                    return false;
                }

                if (!IsValidPathFormat(path))
                {
                    LogValidationFailure("Path did not match an absolute Windows or Unix-style format.", path);
                    return false;
                }

                var extension = Path.GetExtension(path);
                if (!_allowedExtensions.Contains(extension))
                {
                    LogValidationFailure($"Extension '{extension}' is not allowed.", path);
                    return false;
                }

                if (!File.Exists(path))
                {
                    LogValidationFailure("File does not exist at supplied path.", path);
                    return false;
                }

                LogValidationSuccess(path);
                return true;
            }
            catch (Exception ex)
            {
                LogValidationFailure("Unexpected exception while checking path.", path, ex);
                return false;
            }
        }
        
        public string SanitizeSearchPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return "*";
            
            // Remove potentially dangerous characters
            return Regex.Replace(pattern, @"[^\w*?.]", "");
        }

        private bool IsValidPathFormat(string path)
        {
            return _windowsPathPattern.IsMatch(path) || _unixPathPattern.IsMatch(path);
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
