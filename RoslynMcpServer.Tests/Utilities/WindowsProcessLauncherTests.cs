using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Models;
using RoslynMcpServer.Utilities;
using Xunit;

namespace RoslynMcpServer.Tests.Utilities;

public sealed class WindowsProcessLauncherTests
{
    [Fact]
    public async Task RunAsync_ReturnsSuccessForExitZero()
    {
        #region Arrange
        var launcher = new WindowsProcessLauncher(NullLogger<WindowsProcessLauncher>.Instance);
        var request = CreateRequest(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows()
                ? new[] { "/C", "exit 0" }
                : new[] { "-c", "exit 0" });
        #endregion

        #region Initial Assert
        request.Arguments.Count.Should().BeGreaterThan(0);
        #endregion

        #region Act
        var result = await launcher.RunAsync(request, CancellationToken.None);
        #endregion

        #region Assert
        result.Succeeded.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.WasCancelled.Should().BeFalse();
        #endregion
    }

    [Fact]
    public async Task RunAsync_ReturnsCancelledWhenTokenStopsProcess()
    {
        #region Arrange
        var launcher = new WindowsProcessLauncher(NullLogger<WindowsProcessLauncher>.Instance);
        var request = CreateRequest(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows()
                ? new[] { "/C", "ping -n 30 127.0.0.1 >NUL" }
                : new[] { "-c", "sleep 5" });
        using var cts = new CancellationTokenSource();
        #endregion

        #region Initial Assert
        request.Arguments.Count.Should().BeGreaterThan(0);
        #endregion

        #region Act
        var runTask = launcher.RunAsync(request, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var result = await runTask;
        #endregion

        #region Assert
        result.Succeeded.Should().BeFalse();
        result.WasCancelled.Should().BeTrue();
        #endregion
    }

    private static WindowsProcessRequest CreateRequest(string executable, IEnumerable<string> arguments)
    {
        return new WindowsProcessRequest(
            executable,
            arguments as IReadOnlyList<string> ?? new List<string>(arguments),
            null,
            new Dictionary<string, string?>(),
            "test");
    }
}
