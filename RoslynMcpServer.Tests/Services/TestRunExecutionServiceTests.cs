using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Models;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests.Services;

public sealed class TestRunExecutionServiceTests
{
    [Fact]
    public async Task StartAsync_ReturnsRunIdAndCompletes()
    {
        #region Arrange
        using var dotnet = FakeDotnet.Create("echo \"starting\"\nsleep 0.2\necho \"done\"");
        using var tempSolution = TempSolution.Create();
        using var logDir = TempDirectory.Create();
        var service = CreateService(dotnet.SdkBasePath, dotnet.DotnetPath);
        var request = new DotnetCommandRequest(
            Command: "test",
            TargetPath: tempSolution.SolutionPath,
            WorkingDirectory: null,
            Configuration: "Debug",
            Framework: null,
            RuntimeIdentifier: null,
            OutputPath: null,
            SdkVersion: null,
            AdditionalArguments: Array.Empty<string>());
        #endregion

        #region Initial Assert
        File.Exists(dotnet.DotnetPath).Should().BeTrue();
        File.Exists(tempSolution.SolutionPath).Should().BeTrue();
        #endregion

        #region Act
        var start = service.StartAsync(request, collectTrx: true, logDir.Path, CancellationToken.None);
        var status = await WaitForStatusAsync(service, start.RunId, TimeSpan.FromSeconds(5));
        #endregion

        #region Assert
        start.Succeeded.Should().BeTrue();
        start.RunId.Should().NotBeNullOrWhiteSpace();
        start.TrxPath.Should().NotBeNullOrWhiteSpace();
        start.TrxToken.Should().NotBeNullOrWhiteSpace();
        start.LogFilePath.Should().NotBeNullOrWhiteSpace();

        status.Succeeded.Should().BeTrue();
        status.Status.Should().NotBeNull();
        status.Status!.State.Should().Be("Completed");
        status.Status.ExitCode.Should().Be(0);
        File.Exists(start.LogFilePath!).Should().BeTrue();
        File.ReadAllText(start.LogFilePath!).Should().Contain("starting");
        #endregion
    }

    [Fact]
    public async Task Cancel_ReturnsCancelledStatus()
    {
        #region Arrange
        using var dotnet = FakeDotnet.Create("echo \"starting\"\nsleep 3\necho \"done\"");
        using var tempSolution = TempSolution.Create();
        using var logDir = TempDirectory.Create();
        var service = CreateService(dotnet.SdkBasePath, dotnet.DotnetPath);
        var request = new DotnetCommandRequest(
            Command: "test",
            TargetPath: tempSolution.SolutionPath,
            WorkingDirectory: null,
            Configuration: "Debug",
            Framework: null,
            RuntimeIdentifier: null,
            OutputPath: null,
            SdkVersion: null,
            AdditionalArguments: Array.Empty<string>());
        #endregion

        #region Initial Assert
        File.Exists(dotnet.DotnetPath).Should().BeTrue();
        File.Exists(tempSolution.SolutionPath).Should().BeTrue();
        #endregion

        #region Act
        var start = service.StartAsync(request, collectTrx: false, logDir.Path, CancellationToken.None);
        var cancel = service.Cancel(start.RunId!);
        var status = await WaitForStatusAsync(service, start.RunId, TimeSpan.FromSeconds(5));
        #endregion

        #region Assert
        cancel.Succeeded.Should().BeTrue();
        status.Succeeded.Should().BeTrue();
        status.Status.Should().NotBeNull();
        status.Status!.State.Should().Be("Cancelled");
        #endregion
    }

    [Fact]
    public async Task Cancel_ReturnsCompletedStatusAfterRunEnds()
    {
        #region Arrange
        using var dotnet = FakeDotnet.Create("echo \"starting\"\nsleep 0.1\necho \"done\"");
        using var tempSolution = TempSolution.Create();
        using var logDir = TempDirectory.Create();
        var service = CreateService(dotnet.SdkBasePath, dotnet.DotnetPath);
        var request = new DotnetCommandRequest(
            Command: "test",
            TargetPath: tempSolution.SolutionPath,
            WorkingDirectory: null,
            Configuration: "Debug",
            Framework: null,
            RuntimeIdentifier: null,
            OutputPath: null,
            SdkVersion: null,
            AdditionalArguments: Array.Empty<string>());
        #endregion

        #region Initial Assert
        File.Exists(dotnet.DotnetPath).Should().BeTrue();
        #endregion

        #region Act
        var start = service.StartAsync(request, collectTrx: false, logDir.Path, CancellationToken.None);
        var status = await WaitForStatusAsync(service, start.RunId, TimeSpan.FromSeconds(5));
        var cancel = service.Cancel(start.RunId!);
        #endregion

        #region Assert
        status.Succeeded.Should().BeTrue();
        status.Status.Should().NotBeNull();
        status.Status!.State.Should().Be("Completed");
        cancel.Succeeded.Should().BeTrue();
        cancel.Status.Should().NotBeNull();
        cancel.Status!.State.Should().Be("Completed");
        #endregion
    }

    [Fact]
    public void StartAsync_FailsWhenLogDirectoryIsFile()
    {
        #region Arrange
        using var dotnet = FakeDotnet.Create("echo \"starting\"");
        using var tempSolution = TempSolution.Create();
        using var tempFile = TempFile.Create();
        var service = CreateService(dotnet.SdkBasePath, dotnet.DotnetPath);
        var request = new DotnetCommandRequest(
            Command: "test",
            TargetPath: tempSolution.SolutionPath,
            WorkingDirectory: null,
            Configuration: "Debug",
            Framework: null,
            RuntimeIdentifier: null,
            OutputPath: null,
            SdkVersion: null,
            AdditionalArguments: Array.Empty<string>());
        #endregion

        #region Initial Assert
        File.Exists(tempFile.Path).Should().BeTrue();
        #endregion

        #region Act
        var result = service.StartAsync(request, collectTrx: false, tempFile.Path, CancellationToken.None);
        #endregion

        #region Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Log directory");
        #endregion
    }

    [Fact]
    public void GetStatus_ReturnsFailureForMissingRun()
    {
        #region Arrange
        using var dotnet = FakeDotnet.Create("echo \"noop\"");
        var service = CreateService(dotnet.SdkBasePath, dotnet.DotnetPath);
        #endregion

        #region Act
        var result = service.GetStatus("missing");
        #endregion

        #region Assert
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("No test run");
        #endregion
    }

    private static async Task<TestRunStatusResult> WaitForStatusAsync(TestRunExecutionService service, string? runId, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = service.GetStatus(runId ?? string.Empty);
            if (!status.Succeeded || status.Status is null)
            {
                return status;
            }

            if (!string.Equals(status.Status.State, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }

            await Task.Delay(50);
        }

        return service.GetStatus(runId ?? string.Empty);
    }

    private static TestRunExecutionService CreateService(string sdkBasePath, string? overrideExecutable = null)
    {
        var toolchain = new ToolchainInventory(
            new[]
            {
                new DotnetSdkInfo(
                    Version: "10.0.100",
                    BasePath: sdkBasePath,
                    FullPath: Path.Combine(sdkBasePath, "10.0.100"),
                    Exists: true)
            },
            Array.Empty<DotnetRuntimeInfo>(),
            Array.Empty<VisualStudioInstallationInfo>(),
            Array.Empty<string>());

        var environment = new StartupEnvironmentInfo(
            OSDescription: "Test",
            ProcessArchitecture: "x64",
            FrameworkDescription: ".NET",
            RuntimeVersion: "10.0",
            MsbuildPath: null,
            MsbuildSource: "Test",
            EnvironmentVariables: new Dictionary<string, string?>(),
            Toolchain: toolchain);

        var runnerSelector = new RunnerSelector(environment, NullLogger<RunnerSelector>.Instance);
        var validator = new SecurityValidator(NullLogger<SecurityValidator>.Instance);
        Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process>? factory = null;
        if (!string.IsNullOrWhiteSpace(overrideExecutable))
        {
            factory = startInfo =>
            {
                startInfo.FileName = overrideExecutable!;
                return new System.Diagnostics.Process { StartInfo = startInfo };
            };
        }

        return new TestRunExecutionService(runnerSelector, validator, NullLogger<TestRunExecutionService>.Instance, processFactory: factory);
    }

    private sealed class TempSolution : IDisposable
    {
        private TempSolution(string root)
        {
            Root = root;
            SolutionPath = Path.Combine(root, "Test.sln");
            File.WriteAllText(SolutionPath, string.Empty);
        }

        public string Root { get; }
        public string SolutionPath { get; }

        public static TempSolution Create()
        {
            var root = Directory.CreateTempSubdirectory("test-run-solution").FullName;
            return new TempSolution(root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var root = Directory.CreateTempSubdirectory("test-run-logs").FullName;
            return new TempDirectory(root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class TempFile : IDisposable
    {
        private TempFile(string path) => Path = path;

        public string Path { get; }

        public static TempFile Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test-run-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, "log file");
            return new TempFile(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeDotnet : IDisposable
    {
        private FakeDotnet(string root)
        {
            Root = root;
            SdkBasePath = Path.Combine(root, "sdk");
            Directory.CreateDirectory(SdkBasePath);

            var fileName = OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet.exe";
            DotnetPath = Path.Combine(root, fileName);
        }

        public string Root { get; }
        public string SdkBasePath { get; }
        public string DotnetPath { get; }

        public static FakeDotnet Create(string scriptBody)
        {
            var root = Directory.CreateTempSubdirectory("fake-dotnet").FullName;
            var fake = new FakeDotnet(root);
            if (OperatingSystem.IsWindows())
            {
                var script = "@echo off\r\n" + scriptBody.Replace("\n", "\r\n") + "\r\n";
                File.WriteAllText(fake.DotnetPath, script);
            }
            else
            {
                var script = "#!/bin/sh\n" + scriptBody + "\n";
                File.WriteAllText(fake.DotnetPath, script);
            }
            TryMakeExecutable(fake.DotnetPath);
            return fake;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }

        private static void TryMakeExecutable(string path)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch
            {
            }
        }
    }
}
