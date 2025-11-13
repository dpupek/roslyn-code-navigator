using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tests.Fixtures;
using RoslynMcpServer.Tests.Utilities;
using Xunit;

namespace RoslynMcpServer.Tests.Services
{
    [Collection(TestAssetsCollection.Name)]
    public class SymbolSearchServiceTests
    {
        private readonly SymbolSearchService _symbolSearchService;
        private readonly string _solutionPath;

        public SymbolSearchServiceTests(TestAssetsFixture fixture)
        {
            _symbolSearchService = TestServiceFactory.CreateSymbolSearchService();
            _solutionPath = fixture.SampleSolutionPath;
        }

        [Fact]
        public async Task SearchSymbols_WildcardMatchesSerilogWidget()
        {
            var results = await _symbolSearchService.SearchSymbolsAsync(
                pattern: "Serilog*",
                solutionPath: _solutionPath,
                symbolTypes: "class",
                ignoreCase: true,
                cancellationToken: CancellationToken.None);

            results.Should().ContainSingle(result =>
                result.Name == "SerilogWidget" &&
                result.Namespace == "MyCompany.Services.Logging");
        }

        [Fact]
        public async Task SearchSymbols_NamespaceWildcardRestrictsResults()
        {
            var results = await _symbolSearchService.SearchSymbolsAsync(
                pattern: "MyCompany.Services.Text*.*",
                solutionPath: _solutionPath,
                symbolTypes: "class,method",
                ignoreCase: true,
                cancellationToken: CancellationToken.None);

            results.Should().NotBeEmpty();
            results.Should().OnlyContain(result =>
                result.Namespace.StartsWith("MyCompany.Services.Text"));
            results.Should().Contain(result => result.FullName.Contains("TextProcessor.Process"));
        }

        [Fact]
        public async Task FindReferences_FullyQualifiedPatternFindsCallSites()
        {
            var references = await _symbolSearchService.FindReferencesAsync(
                symbolName: "MyCompany.Services.Logging.SerilogWidget.Process*",
                solutionPath: _solutionPath,
                includeDefinition: false,
                cancellationToken: CancellationToken.None);

            references.Should().NotBeEmpty();
            references.Should().OnlyContain(r => r.DocumentPath.EndsWith("ServiceRunner.cs"));
            references.Should().AllSatisfy(r => r.IsDefinition.Should().BeFalse());
        }

        [Fact]
        public async Task FindReferences_ShortNameReturnsDefinitionsAndUsages()
        {
            var references = await _symbolSearchService.FindReferencesAsync(
                symbolName: "TextProcessor",
                solutionPath: _solutionPath,
                includeDefinition: true,
                cancellationToken: CancellationToken.None);

            references.Should().NotBeEmpty();
            references.Should().HaveCountGreaterThan(1);
            references.Should().Contain(r => r.DocumentPath.EndsWith("TextProcessor.cs"));
        }

        [Fact]
        public async Task SearchSymbols_FindsVisualBasicTypes()
        {
            var results = await _symbolSearchService.SearchSymbolsAsync(
                pattern: "LegacyCalculator",
                solutionPath: _solutionPath,
                symbolTypes: "class",
                ignoreCase: true,
                cancellationToken: CancellationToken.None);

            results.Should().ContainSingle(result =>
                result.Name == "LegacyCalculator" &&
                result.Namespace == "MyCompany.Services.Legacy");
        }

        [Fact]
        public async Task FindReferences_CanTraceUsageOfVisualBasicType()
        {
            var references = await _symbolSearchService.FindReferencesAsync(
                symbolName: "LegacyCalculator",
                solutionPath: _solutionPath,
                includeDefinition: true,
                cancellationToken: CancellationToken.None);

            references.Should().NotBeEmpty();
            references.Should().Contain(r => r.DocumentPath.EndsWith("LegacyCalculator.vb"));
            references.Should().Contain(r => r.DocumentPath.EndsWith("VbBridge.cs"));
        }

        [Fact]
        public async Task GetSymbolInfo_ReturnsMetadataForQualifiedMethod()
        {
            var info = await _symbolSearchService.GetSymbolInfoAsync(
                symbolName: "MyCompany.Services.Logging.SerilogWidget.ProcessAndLog",
                solutionPath: _solutionPath,
                cancellationToken: CancellationToken.None);

            info.Should().NotBeNull();
            info!.Name.Should().Be("ProcessAndLog");
            info.Namespace.Should().Be("MyCompany.Services.Logging");
            info.Parameters.Should().ContainSingle(p => p.Contains("String message", StringComparison.Ordinal));
            info.ReturnType.Should().Be("String");
        }
    }
}
