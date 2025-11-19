using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Models;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests.Services;

public class RunnerSelectorTests
{
    private static StartupEnvironmentInfo CreateEnvironment(ToolchainInventory toolchain)
    {
        return new StartupEnvironmentInfo(
            OSDescription: "Test OS",
            ProcessArchitecture: "X64",
            FrameworkDescription: ".NET Test",
            RuntimeVersion: "10.0",
            MsbuildPath: null,
            MsbuildSource: "Test",
            EnvironmentVariables: new Dictionary<string, string?>(),
            Toolchain: toolchain);
    }

    [Fact]
    public void SelectDotnetRunner_PicksLatestWhenNoVersionRequested()
    {
        var toolchain = new ToolchainInventory(
            new[]
            {
                new DotnetSdkInfo("9.0.101", "C\\Program Files\\dotnet\\sdk", "C\\Program Files\\dotnet\\sdk\\9.0.101", true),
                new DotnetSdkInfo("10.0.100", "C\\Program Files\\dotnet\\sdk", "C\\Program Files\\dotnet\\sdk\\10.0.100", true)
            },
            Array.Empty<DotnetRuntimeInfo>(),
            Array.Empty<VisualStudioInstallationInfo>(),
            Array.Empty<string>());

        var selector = new RunnerSelector(CreateEnvironment(toolchain), NullLogger<RunnerSelector>.Instance);

        var selection = selector.SelectDotnetRunner(requestedVersion: null);

        selection.RunnerKind.Should().Be("dotnet");
        selection.DisplayName.Should().Contain("10.0.100");
        selection.EnvironmentOverrides.Should().ContainKey("DOTNET_ROOT");
        selection.EnvironmentOverrides.Should().ContainKey("MSBUILDDISABLENODEREUSE");
    }

    [Fact]
    public void SelectDotnetRunner_ThrowsIfRequestedVersionMissing()
    {
        var toolchain = new ToolchainInventory(
            new[] { new DotnetSdkInfo("9.0.101", "C\\dotnet\\sdk", "C\\dotnet\\sdk\\9.0.101", true) },
            Array.Empty<DotnetRuntimeInfo>(),
            Array.Empty<VisualStudioInstallationInfo>(),
            Array.Empty<string>());

        var selector = new RunnerSelector(CreateEnvironment(toolchain), NullLogger<RunnerSelector>.Instance);

        Action act = () => selector.SelectDotnetRunner("10.0.100");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*10.0.100*");
    }

    [Fact]
    public void SelectMsbuildRunner_UsesPreferredInstance()
    {
        var vsInstallations = new[]
        {
            new VisualStudioInstallationInfo("VS2019", "16.0.0", "VisualStudio", "C:\\VS2019", "C:\\VS2019\\MSBuild\\Current\\Bin\\MSBuild.exe"),
            new VisualStudioInstallationInfo("VS2022", "17.0.0", "VisualStudio", "C:\\VS2022", "C:\\VS2022\\MSBuild\\Current\\Bin\\MSBuild.exe")
        };

        var toolchain = new ToolchainInventory(
            Array.Empty<DotnetSdkInfo>(),
            Array.Empty<DotnetRuntimeInfo>(),
            vsInstallations,
            Array.Empty<string>());

        var selector = new RunnerSelector(CreateEnvironment(toolchain), NullLogger<RunnerSelector>.Instance);

        var selection = selector.SelectMsbuildRunner("VS2019");

        selection.RunnerKind.Should().Be("msbuild");
        selection.ExecutablePath.Should().Contain("VS2019");
        selection.DisplayName.Should().Contain("VS2019");
    }

    [Fact]
    public void SelectVsTestRunner_RequiresExecutableToExist()
    {
        using var temp = new TempVsInstallation();

        var vsInstallations = new[]
        {
            new VisualStudioInstallationInfo("VS2022", "17.0.0", "VisualStudio", temp.RootPath, Path.Combine(temp.RootPath, "MSBuild", "Current", "Bin", "MSBuild.exe"))
        };

        var toolchain = new ToolchainInventory(
            Array.Empty<DotnetSdkInfo>(),
            Array.Empty<DotnetRuntimeInfo>(),
            vsInstallations,
            Array.Empty<string>());

        var selector = new RunnerSelector(CreateEnvironment(toolchain), NullLogger<RunnerSelector>.Instance);

        var selection = selector.SelectVsTestRunner(null);

        selection.RunnerKind.Should().Be("vstest");
        selection.ExecutablePath.Should().EndWith("vstest.console.exe");
        selection.DisplayName.Should().Contain("VS2022");
    }

    private sealed class TempVsInstallation : IDisposable
    {
        public TempVsInstallation()
        {
            RootPath = Directory.CreateTempSubdirectory("vs-install").FullName;
            var vstestPath = Path.Combine(RootPath, "Common7", "IDE", "CommonExtensions", "Microsoft", "TestWindow");
            Directory.CreateDirectory(vstestPath);
            File.WriteAllText(Path.Combine(vstestPath, "vstest.console.exe"), string.Empty);

            var msbuildPath = Path.Combine(RootPath, "MSBuild", "Current", "Bin");
            Directory.CreateDirectory(msbuildPath);
            File.WriteAllText(Path.Combine(msbuildPath, "MSBuild.exe"), string.Empty);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}
