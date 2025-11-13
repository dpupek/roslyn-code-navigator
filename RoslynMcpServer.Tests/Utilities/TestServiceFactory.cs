using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tests.Utilities
{
    internal static class TestServiceFactory
    {
        public static SymbolSearchService CreateSymbolSearchService()
        {
            var diagnosticLogger = new DiagnosticLogger(NullLogger<DiagnosticLogger>.Instance);
            var codeAnalysis = new CodeAnalysisService(diagnosticLogger, NullLogger<CodeAnalysisService>.Instance);
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            return new SymbolSearchService(codeAnalysis, NullLogger<SymbolSearchService>.Instance, memoryCache);
        }
    }
}
