using System.IO;
using FluentAssertions;
using RoslynMcpServer.Services;
using Xunit;

namespace RoslynMcpServer.Tests.Services;

public sealed class TrxFailureSummaryTests
{
    [Fact]
    public void ParseTrxFailureSummary_ReturnsFailedTestsAndMessage()
    {
        #region Arrange
        using var tempDir = TempDirectory.Create();
        var trxPath = Path.Combine(tempDir.Path, "results.trx");
        File.WriteAllText(trxPath, """
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Foo.Bar" outcome="Failed">
      <Output>
        <ErrorInfo>
          <Message>boom</Message>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
    <UnitTestResult testName="Baz.Qux" outcome="Passed" />
  </Results>
</TestRun>
""");
        #endregion

        #region Act
        var summary = TestRunExecutionService.ParseTrxFailureSummary(trxPath);
        #endregion

        #region Assert
        summary.Should().NotBeNull();
        summary!.FailedTests.Should().ContainSingle().Which.Should().Be("Foo.Bar");
        summary.FirstFailureMessage.Should().Be("boom");
        #endregion
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"roslyn-trx-{System.Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
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
}
