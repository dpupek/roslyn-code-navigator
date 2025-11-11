using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;
using RoslynMcpServer.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace RoslynMcpServer.Services
{
    public class CodeAnalysisService
    {
        private readonly DiagnosticLogger _diagnosticLogger;
        private readonly ILogger<CodeAnalysisService> _logger;
        private readonly ConcurrentDictionary<string, Solution> _solutionCache = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _solutionLocks = new();
        private readonly ConcurrentDictionary<string, MSBuildWorkspace> _workspaces = new();

        public CodeAnalysisService(DiagnosticLogger diagnosticLogger, ILogger<CodeAnalysisService> logger)
        {
            _diagnosticLogger = diagnosticLogger;
            _logger = logger;
        }

        public async Task<Solution> GetSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
        {
            if (_solutionCache.TryGetValue(solutionPath, out var cached))
            {
                return cached;
            }

            var loadLock = _solutionLocks.GetOrAdd(solutionPath, _ => new SemaphoreSlim(1, 1));
            await loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_solutionCache.TryGetValue(solutionPath, out cached))
                {
                    return cached;
                }

                var solution = await _diagnosticLogger.LoggedExecutionAsync(
                    "LoadSolution",
                    () => LoadSolutionAsync(solutionPath, cancellationToken),
                    new { SolutionPath = solutionPath }).ConfigureAwait(false);

                _solutionCache[solutionPath] = solution;
                return solution;
            }
            finally
            {
                loadLock.Release();
            }
        }

        private async Task<Solution> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken)
        {
            var workspace = MSBuildWorkspace.Create(CreateWorkspaceProperties());

            workspace.WorkspaceFailed += (sender, args) =>
            {
                _logger.LogWarning(args.Diagnostic.ToString());
                if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    throw new InvalidOperationException(args.Diagnostic.Message);
                }
            };

            var solution = await workspace
                .OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _workspaces[solutionPath] = workspace;
            return solution;
        }

        public async Task<DependencyAnalysis> AnalyzeDependenciesAsync(string solutionPath, int maxDepth = 3, CancellationToken cancellationToken = default)
        {
            var solution = await GetSolutionAsync(solutionPath, cancellationToken).ConfigureAwait(false);
            var analysis = new DependencyAnalysis
            {
                ProjectName = Path.GetFileNameWithoutExtension(solutionPath) ?? solutionPath
            };

            var dependencyMap = new Dictionary<string, ProjectDependency>(StringComparer.OrdinalIgnoreCase);
            var namespaceUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in solution.Projects.Where(p => p.SupportsCompilation))
            {
                cancellationToken.ThrowIfCancellationRequested();

                AddProjectDependencies(
                    project,
                    solution,
                    dependencyMap,
                    Math.Max(maxDepth, 1),
                    cancellationToken);

                await RecordNamespaceUsagesAsync(project, namespaceUsage, cancellationToken).ConfigureAwait(false);
                await CountSymbolsAsync(project, analysis, cancellationToken).ConfigureAwait(false);
            }

            analysis.Dependencies = dependencyMap.Values
                .OrderByDescending(d => d.UsageCount)
                .ThenBy(d => d.Name)
                .ToList();

            analysis.NamespaceUsages = namespaceUsage
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key)
                .Select(kvp => new NamespaceUsage
                {
                    Namespace = kvp.Key,
                    UsageCount = kvp.Value
                })
                .ToList();

            return analysis;
        }

        private void AddProjectDependencies(
            Project project,
            Solution solution,
            Dictionary<string, ProjectDependency> dependencyMap,
            int maxDepth,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pathVisited = new HashSet<ProjectId>();
            if (maxDepth > 0)
            {
                AddProjectDependenciesRecursive(project, solution, dependencyMap, pathVisited, 0, maxDepth, cancellationToken);
            }

            foreach (var packageRef in GetPackageReferences(project))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddOrUpdateDependency(dependencyMap, packageRef.Name, "PackageReference", packageRef.Version);
            }
        }

        private void AddProjectDependenciesRecursive(
            Project project,
            Solution solution,
            Dictionary<string, ProjectDependency> dependencyMap,
            HashSet<ProjectId> visited,
            int depth,
            int maxDepth,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (depth >= maxDepth || !visited.Add(project.Id))
            {
                return;
            }

            foreach (var reference in project.ProjectReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var referencedProject = solution.GetProject(reference.ProjectId);
                if (referencedProject == null)
                {
                    continue;
                }

                AddOrUpdateDependency(dependencyMap, referencedProject.Name, "ProjectReference", referencedProject.FilePath ?? string.Empty);
                AddProjectDependenciesRecursive(referencedProject, solution, dependencyMap, visited, depth + 1, maxDepth, cancellationToken);
            }
        }

        private void AddOrUpdateDependency(Dictionary<string, ProjectDependency> dependencyMap, string name, string type, string version)
        {
            var key = $"{type}:{name}";
            if (!dependencyMap.TryGetValue(key, out var dependency))
            {
                dependency = new ProjectDependency
                {
                    Name = name,
                    Type = type,
                    Version = version,
                    UsageCount = 0
                };

                dependencyMap[key] = dependency;
            }

            dependency.UsageCount++;
            if (!string.IsNullOrWhiteSpace(version))
            {
                dependency.Version = version;
            }
        }

        private IEnumerable<(string Name, string Version)> GetPackageReferences(Project project)
        {
            var references = new List<(string Name, string Version)>();
            if (string.IsNullOrWhiteSpace(project.FilePath) || !File.Exists(project.FilePath))
            {
                return references;
            }

            try
            {
                var doc = XDocument.Load(project.FilePath);
                foreach (var element in doc.Descendants()
                    .Where(element => element.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase)))
                {
                    var include = GetElementValue(element, "Include");
                    if (string.IsNullOrWhiteSpace(include))
                    {
                        continue;
                    }

                    var version = GetElementValue(element, "Version");
                    references.Add((include.Trim(), version.Trim()));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read package references from {ProjectFile}", project.FilePath);
            }

            return references;
        }

        private static string GetElementValue(XElement element, string localName)
        {
            var attribute = element.Attribute(localName);
            if (attribute != null)
            {
                return attribute.Value;
            }

            var child = element.Elements()
                .FirstOrDefault(child => child.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
            return child?.Value ?? string.Empty;
        }

        private async Task RecordNamespaceUsagesAsync(
            Project project,
            Dictionary<string, int> namespaceUsage,
            CancellationToken cancellationToken)
        {
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (document.SourceCodeKind != SourceCodeKind.Regular)
                {
                    continue;
                }

                if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not SyntaxNode root)
                {
                    continue;
                }

                var usingDirectives = root.DescendantNodes().OfType<UsingDirectiveSyntax>();
                foreach (var directive in usingDirectives)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ns = directive.Name?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(ns))
                    {
                        continue;
                    }

                    if (namespaceUsage.TryGetValue(ns, out var count))
                    {
                        namespaceUsage[ns] = count + 1;
                    }
                    else
                    {
                        namespaceUsage[ns] = 1;
                    }
                }
            }
        }

        private async Task CountSymbolsAsync(
            Project project,
            DependencyAnalysis analysis,
            CancellationToken cancellationToken)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation == null)
            {
                return;
            }

            CountSymbolsRecursive(compilation.Assembly.GlobalNamespace, analysis, cancellationToken);
        }

        private void CountSymbolsRecursive(ISymbol symbol, DependencyAnalysis analysis, CancellationToken cancellationToken)
        {
            if (symbol == null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            analysis.TotalSymbols++;
            switch (symbol.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    analysis.PublicSymbols++;
                    break;
                case Accessibility.Internal:
                case Accessibility.ProtectedAndInternal:
                case Accessibility.ProtectedOrInternal:
                    analysis.InternalSymbols++;
                    break;
            }

            IEnumerable<ISymbol> children = symbol switch
            {
                INamespaceSymbol namespaceSymbol => namespaceSymbol.GetMembers(),
                INamedTypeSymbol namedType => namedType.GetMembers(),
                _ => Array.Empty<ISymbol>()
            };

            foreach (var child in children)
            {
                CountSymbolsRecursive(child, analysis, cancellationToken);
            }
        }

        private Dictionary<string, string> CreateWorkspaceProperties()
        {
            var properties = new Dictionary<string, string>
            {
                ["UseSimpleAssemblyNames"] = "true"
            };

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                properties["DisableImplicitNuGetFallbackFolder"] = "true";

                var translatedFallback = PathUtilities.TranslateWindowsPathToUnix(@"C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages");
                if (!string.IsNullOrWhiteSpace(translatedFallback) && Directory.Exists(translatedFallback))
                {
                    properties["RestoreAdditionalProjectFallbackFolders"] = translatedFallback;
                }
            }

            var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (string.IsNullOrWhiteSpace(nugetRoot))
            {
                nugetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            }
            properties["NuGetPackageRoot"] = nugetRoot;

            return properties;
        }
    }
}
