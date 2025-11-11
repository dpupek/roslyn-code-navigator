using ModelContextProtocol.Server;
using System.ComponentModel;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcpServer.Resources;

[McpServerResourceType]
public static class HelpResources
{
    private static readonly Lazy<string> HelpMarkdown = new(LoadHelpFile, isThreadSafe: true);

    [McpServerResource(
        Name = "roslyn-help",
        Title = "Roslyn MCP Help",
        UriTemplate = "resource://roslyn/help",
        MimeType = "text/markdown")]
    [Description("Streams the built-in help.md file so clients can read usage docs via MCP resources.")]
    public static Task<string> ReadHelpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(HelpMarkdown.Value);
    }

    private static string LoadHelpFile()
    {
        var helpPath = Path.Combine(AppContext.BaseDirectory, "help.md");
        if (!File.Exists(helpPath))
        {
            return "# Roslyn MCP Server Help\nThe help.md file could not be found at runtime.";
        }

        return File.ReadAllText(helpPath);
    }
}
