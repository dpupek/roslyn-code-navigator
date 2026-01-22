using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcpServer.Utilities;

public sealed class WindowsProcessLauncher
{
    private readonly ILogger<WindowsProcessLauncher> _logger;

    public WindowsProcessLauncher(ILogger<WindowsProcessLauncher> logger)
    {
        _logger = logger;
    }

    public async Task<ProcessExecutionResult> RunAsync(WindowsProcessRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory!;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var kvp in request.EnvironmentOverrides)
        {
            if (kvp.Value is null)
            {
                continue;
            }

            startInfo.Environment[kvp.Key] = kvp.Value;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start runner {Runner} ({Path}).", request.RunnerDescription, request.ExecutablePath);
            return new ProcessExecutionResult(false, -1, TimeSpan.Zero, string.Empty, ex.Message, false, request.RunnerDescription);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var cancellationRequested = false;
        using var registration = cancellationToken.Register(() =>
        {
            cancellationRequested = true;
            try
            {
                if (TryGetHasExited(process, out var hasExited) && !hasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore
            }
        });

        try
        {
            await Task.Run(() => process.WaitForExit(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var cancelledOut = await stdoutTask.ConfigureAwait(false);
            var cancelledErr = await stderrTask.ConfigureAwait(false);
            return new ProcessExecutionResult(false, -1, stopwatch.Elapsed, cancelledOut, cancelledErr, true, request.RunnerDescription);
        }

        stopwatch.Stop();
        var output = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);

        var exitCode = TryGetExitCode(process, out var code) ? code : -1;
        var success = exitCode == 0;
        var wasCancelled = cancellationRequested || cancellationToken.IsCancellationRequested;

        if (!success)
        {
            _logger.LogWarning("Runner {Runner} exited with code {ExitCode}.", request.RunnerDescription, exitCode);
        }

        return new ProcessExecutionResult(success, exitCode, stopwatch.Elapsed, output, error, wasCancelled, request.RunnerDescription);
    }

    private static bool TryGetHasExited(Process process, out bool hasExited)
    {
        try
        {
            hasExited = process.HasExited;
            return true;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        hasExited = true;
        return false;
    }

    private static bool TryGetExitCode(Process process, out int exitCode)
    {
        try
        {
            exitCode = process.ExitCode;
            return true;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        exitCode = -1;
        return false;
    }
}
