using System.Collections.Generic;
using MyCompany.Services.Logging;

namespace MyCompany.Services.TextProcessing
{
    public static class ServiceRunner
    {
        public static IReadOnlyList<string> RunSamples()
        {
            var widget = new SerilogWidget();
            return new[]
            {
                widget.ProcessAndLog("alpha"),
                widget.ProcessAndLog("beta"),
                widget.ProcessAndLog("gamma")
            };
        }
    }
}
