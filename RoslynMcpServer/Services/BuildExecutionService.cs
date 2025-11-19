using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;
using RoslynMcpServer.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcpServer.Services;

public sealed class BuildExecutionService
{
    private readonly RunnerSelector _runnerSelector;
    private readonly WindowsProcessLauncher _processLauncher;
    private readonly SecurityValidator _securityValidator;
    private readonly ILogger<BuildExecutionService> _logger;
    private readonly bool _isWindowsHost = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public BuildExecutionService(
        RunnerSelector runnerSelector,
        WindowsProcessLauncher processLauncher,
        SecurityValidator securityValidator,
        ILogger<BuildExecutionService> logger)
    {
        _runnerSelector = runnerSelector;
        _processLauncher = processLauncher;
        _securityValidator = securityValidator;
        _logger = logger;
    }

    public Task<ProcessExecutionResult> RunDotnetBuildAsync(DotnetCommandRequest request, CancellationToken cancellationToken)
    {
        return RunDotnetCommandInternalAsync(request with { Command = "build" }, cancellationToken);
    }

    public Task<ProcessExecutionResult> RunDotnetTestAsync(DotnetCommandRequest request, CancellationToken cancellationToken)
    {
        return RunDotnetCommandInternalAsync(request with { Command = "test" }, cancellationToken);
    }

    public async Task<ProcessExecutionResult> RunDotnetCommandInternalAsync(DotnetCommandRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TargetPath))
        {
            throw new ArgumentException("Target path must be provided.", nameof(request));
        }

        var validation = _securityValidator.ValidateSolutionPath(request.TargetPath);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            throw new InvalidOperationException(validation.ErrorMessage ?? "Solution path validation failed.");
        }

        var hostPath = validation.NormalizedPath;
        var windowsPath = EnsureWindowsPath(hostPath);

        var runner = _runnerSelector.SelectDotnetRunner(request.SdkVersion);
        var workingDirectory = DetermineWorkingDirectory(windowsPath);

        var arguments = BuildDotnetArguments(request.Command, windowsPath, request);
        var processRequest = new WindowsProcessRequest(
            runner.ExecutablePath,
            arguments,
            workingDirectory,
            runner.EnvironmentOverrides,
            runner.DisplayName);

        return await _processLauncher.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessExecutionResult> RunMsbuildAsync(MsbuildCommandRequest request, CancellationToken cancellationToken)
    {
        var validation = _securityValidator.ValidateSolutionPath(request.ProjectOrSolutionPath);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            throw new InvalidOperationException(validation.ErrorMessage ?? "Path validation failed.");
        }

        var windowsPath = EnsureWindowsPath(validation.NormalizedPath);
        var runner = _runnerSelector.SelectMsbuildRunner(request.PreferredInstance);
        var workingDirectory = DetermineWorkingDirectory(windowsPath);

        var arguments = new List<string> { windowsPath };

        if (request.Targets.Count > 0)
        {
            arguments.Add($"/t:{string.Join(";", request.Targets)}");
        }

        foreach (var property in request.Properties)
        {
            if (string.IsNullOrWhiteSpace(property.Key))
            {
                continue;
            }

            var value = property.Value ?? string.Empty;
            arguments.Add($"/p:{property.Key}={value}");
        }

        if (request.AdditionalArguments.Count > 0)
        {
            arguments.AddRange(request.AdditionalArguments);
        }

        var processRequest = new WindowsProcessRequest(
            runner.ExecutablePath,
            arguments,
            workingDirectory,
            runner.EnvironmentOverrides,
            runner.DisplayName);

        return await _processLauncher.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessExecutionResult> RunVsTestAsync(VsTestCommandRequest request, CancellationToken cancellationToken)
    {
        if (request.TestAssemblyPaths.Count == 0)
        {
            throw new ArgumentException("At least one test assembly path must be provided.", nameof(request));
        }

        var normalizedAssemblies = new List<string>();
        foreach (var assemblyPath in request.TestAssemblyPaths)
        {
            var validation = _securityValidator.ValidateFilePath(assemblyPath);
            if (!validation.IsValid || validation.NormalizedPath is null)
            {
                throw new InvalidOperationException(validation.ErrorMessage ?? $"Path validation failed for '{assemblyPath}'.");
            }

            normalizedAssemblies.Add(validation.NormalizedPath);
        }

        var windowsAssemblies = normalizedAssemblies.Select(EnsureWindowsPath).ToList();
        var runner = _runnerSelector.SelectVsTestRunner(request.PreferredInstance);
        var workingDirectory = DetermineWorkingDirectory(windowsAssemblies[0]);

        var arguments = new List<string>();
        foreach (var assembly in windowsAssemblies)
        {
            arguments.Add(assembly);
        }

        if (!string.IsNullOrWhiteSpace(request.Framework))
        {
            arguments.Add($"/Framework:{request.Framework}");
        }

        if (!string.IsNullOrWhiteSpace(request.Platform))
        {
            arguments.Add($"/Platform:{request.Platform}");
        }

        if (request.InIsolation)
        {
            arguments.Add("/InIsolation");
        }

        if (request.AdditionalArguments.Count > 0)
        {
            arguments.AddRange(request.AdditionalArguments);
        }

        var processRequest = new WindowsProcessRequest(
            runner.ExecutablePath,
            arguments,
            workingDirectory,
            runner.EnvironmentOverrides,
            runner.DisplayName);

        return await _processLauncher.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> BuildDotnetArguments(string command, string windowsPath, DotnetCommandRequest request)
    {
        var args = new List<string> { command, windowsPath };

        if (!string.IsNullOrWhiteSpace(request.Configuration))
        {
            args.Add("-c");
            args.Add(request.Configuration!);
        }

        if (!string.IsNullOrWhiteSpace(request.Framework))
        {
            args.Add("-f");
            args.Add(request.Framework!);
        }

        if (!string.IsNullOrWhiteSpace(request.RuntimeIdentifier))
        {
            args.Add("-r");
            args.Add(request.RuntimeIdentifier!);
        }

        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            args.Add("-o");
            args.Add(request.OutputPath!);
        }

        if (request.AdditionalArguments.Count > 0)
        {
            args.AddRange(request.AdditionalArguments);
        }

        return args;
    }

    private string DetermineWorkingDirectory(string windowsPath)
    {
        var directory = Path.GetDirectoryName(windowsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Environment.CurrentDirectory;
        }

        if (_isWindowsHost)
        {
            return directory;
        }

        var translated = PathUtilities.TranslateWindowsPathToUnix(directory);
        return translated ?? directory;
    }

    private string EnsureWindowsPath(string hostPath)
    {
        if (_isWindowsHost)
        {
            return hostPath;
        }

        if (PathUtilities.TryTranslateUnixPathToWindows(hostPath, out var windowsPath))
        {
            return windowsPath;
        }

        _logger.LogWarning("Could not translate '{Path}' to a Windows path. Falling back to original path.", hostPath);
        return hostPath;
    }
}
