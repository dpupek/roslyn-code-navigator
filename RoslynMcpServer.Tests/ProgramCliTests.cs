using RoslynMcpServer;
using Xunit;

namespace RoslynMcpServer.Tests;

public class ProgramCliTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void GetCliResponse_WhenHelpRequested_ReturnsHelpText(string arg)
    {
        #region Arrange
        var args = new[] { arg };
        #endregion

        #region Initial Assert
        Assert.NotNull(args);
        #endregion

        #region Act
        var response = Program.GetCliResponse(args);
        #endregion

        #region Assert
        Assert.False(string.IsNullOrWhiteSpace(response));
        Assert.Contains("codenav-mcp", response!);
        Assert.Contains("Usage:", response!);
        #endregion
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void GetCliResponse_WhenVersionRequested_ReturnsVersionString(string arg)
    {
        #region Arrange
        var args = new[] { arg };
        #endregion

        #region Initial Assert
        Assert.NotNull(args);
        #endregion

        #region Act
        var response = Program.GetCliResponse(args);
        #endregion

        #region Assert
        Assert.False(string.IsNullOrWhiteSpace(response));
        Assert.DoesNotContain("Usage:", response!);
        #endregion
    }

    [Fact]
    public void GetCliResponse_WhenNoArguments_ReturnsNull()
    {
        #region Arrange
        var args = Array.Empty<string>();
        #endregion

        #region Initial Assert
        Assert.Empty(args);
        #endregion

        #region Act
        var response = Program.GetCliResponse(args);
        #endregion

        #region Assert
        Assert.Null(response);
        #endregion
    }

    [Fact]
    public void GetCliResponse_WhenArgumentsUnrecognized_ReturnsNull()
    {
        #region Arrange
        var args = new[] { "--unknown" };
        #endregion

        #region Initial Assert
        Assert.Single(args);
        #endregion

        #region Act
        var response = Program.GetCliResponse(args);
        #endregion

        #region Assert
        Assert.Null(response);
        #endregion
    }
}
