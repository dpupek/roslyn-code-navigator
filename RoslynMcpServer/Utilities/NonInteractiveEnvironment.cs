using System.Collections.Generic;

namespace RoslynMcpServer.Utilities;

public static class NonInteractiveEnvironment
{
    public static void Apply(IDictionary<string, string?> environment)
    {
        environment["TERM"] = "dumb";
        environment["NO_COLOR"] = "1";
        environment["CLICOLOR"] = "0";
    }
}
