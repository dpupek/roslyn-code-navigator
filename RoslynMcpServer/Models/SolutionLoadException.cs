using System;

namespace RoslynMcpServer.Models;

public class SolutionLoadException : Exception
{
    public string? DiagnosticMessage { get; }

    public SolutionLoadException(string message, string? diagnosticMessage = null, Exception? innerException = null)
        : base(message, innerException)
    {
        DiagnosticMessage = diagnosticMessage;
    }
}
