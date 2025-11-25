using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Models;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests.Services;

public class RunExecutionServiceTests
{
    [Fact]
    public void ListLaunchProfiles_ParsesProfilesAndUrls()
    {
        // Arrange
        using var tempProject = new TempProject();
        tempProject.WriteLaunchSettings(new
        {
            profiles = new
            {
                Api = new { commandName = "Project", applicationUrl = "http://localhost:5055;https://localhost:7055" },
                Ui = new { commandName = "Project", applicationUrl = new[] { "http://localhost:6066", "https://localhost:8066" } }
            }
        });

        var service = CreateService();

        // Initial Assert
        File.Exists(tempProject.ProjectPath).Should().BeTrue("project file must exist for validation");

        // Act
        var profiles = service.ListLaunchProfiles(tempProject.ProjectPath);

        // Assert
        profiles.Message.Should().BeNull();
        profiles.Profiles.Should().HaveCount(2);
        profiles.Profiles[0].Name.Should().Be("Api");
        profiles.Profiles[0].ApplicationUrls.Should().Contain(new[] { "http://localhost:5055", "https://localhost:7055" });
        profiles.Profiles[1].ApplicationUrls.Should().Contain("http://localhost:6066");
        profiles.Profiles[1].ApplicationUrls.Should().Contain("https://localhost:8066");
    }

    [Fact]
    public async Task StopAsync_ReturnsFalseForUnknownToken()
    {
        var service = CreateService();

        var result = await service.StopAsync("missing", TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("token");
    }

    [Fact]
    public void GetOutput_ReturnsMessageWhenTokenMissing()
    {
        var service = CreateService();

        var output = service.GetOutput("missing", 10);

        output.Combined.Should().ContainSingle().Which.Should().Contain("No active session");
    }

    private static RunExecutionService CreateService()
    {
        var toolchain = new ToolchainInventory(Array.Empty<DotnetSdkInfo>(), Array.Empty<DotnetRuntimeInfo>(), Array.Empty<VisualStudioInstallationInfo>(), Array.Empty<string>());
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
        return new RunExecutionService(runnerSelector, validator, NullLogger<RunExecutionService>.Instance, processFactory: null);
    }

    private sealed class TempProject : IDisposable
    {
        public TempProject()
        {
            Root = Directory.CreateTempSubdirectory("run-exec-test").FullName;
            ProjectPath = Path.Combine(Root, "TestApp.csproj");
            File.WriteAllText(ProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        }

        public string Root { get; }
        public string ProjectPath { get; }

        public void WriteLaunchSettings(object payload)
        {
            var props = Path.Combine(Root, "Properties");
            Directory.CreateDirectory(props);
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(props, "launchSettings.json"), json);
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
}
