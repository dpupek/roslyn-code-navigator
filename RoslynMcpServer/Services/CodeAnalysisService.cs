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
using System.Reflection;
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
            string? failureMessage = null;
            string? diagnosticMessage = null;

            workspace.WorkspaceFailed += (sender, args) =>
            {
                _logger.LogWarning(args.Diagnostic.ToString());
                if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    var friendlyMessage = BuildFriendlyLoadFailureMessage(solutionPath, args.Diagnostic.Message);
                    failureMessage ??= friendlyMessage;
                    diagnosticMessage ??= args.Diagnostic.Message;
                }
            };

            try
            {
                var solution = await workspace
                    .OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (failureMessage != null)
                {
                    throw new SolutionLoadException(failureMessage, diagnosticMessage);
                }

                _workspaces[solutionPath] = workspace;
                return solution;
            }
            catch (SolutionLoadException)
            {
                workspace.Dispose();
                _workspaces.TryRemove(solutionPath, out _);
                _solutionCache.TryRemove(solutionPath, out _);
                throw;
            }
            catch (FileNotFoundException ex)
            {
                workspace.Dispose();
                _workspaces.TryRemove(solutionPath, out _);
                _solutionCache.TryRemove(solutionPath, out _);
                var friendlyMessage = BuildAssemblyLoadFailureMessage(solutionPath, ex);
                throw new SolutionLoadException(friendlyMessage, ex.FileName, ex);
            }
            catch (ReflectionTypeLoadException ex)
            {
                workspace.Dispose();
                _workspaces.TryRemove(solutionPath, out _);
                _solutionCache.TryRemove(solutionPath, out _);
                var friendlyMessage = BuildReflectionLoadFailureMessage(solutionPath, ex);
                throw new SolutionLoadException(friendlyMessage, ex.LoaderExceptions?.FirstOrDefault()?.Message, ex);
            }
            catch (InvalidOperationException ex)
            {
                workspace.Dispose();
                _workspaces.TryRemove(solutionPath, out _);
                _solutionCache.TryRemove(solutionPath, out _);
                var friendlyMessage = BuildFriendlyLoadFailureMessage(solutionPath, ex.Message);
                throw new SolutionLoadException(friendlyMessage, ex.Message, ex);
            }
            catch
            {
                workspace.Dispose();
                _workspaces.TryRemove(solutionPath, out _);
                _solutionCache.TryRemove(solutionPath, out _);
                throw;
            }
        }

        private static string BuildAssemblyLoadFailureMessage(string solutionPath, FileNotFoundException exception)
        {
            var solutionName = Path.GetFileName(solutionPath);
            var baseMessage = $"MSBuild failed to load {(string.IsNullOrWhiteSpace(solutionName) ? solutionPath : solutionName)} because the .NET runtime assemblies were missing.";
            var fileName = exception.FileName ?? string.Empty;
            var lowerFile = fileName.ToLowerInvariant();

            if (lowerFile.Contains("system.runtime") && lowerFile.Contains("version=9.0.0.0"))
            {
                return baseMessage + " Install the .NET 9 SDK/targeting pack (e.g., `winget install --id Microsoft.DotNet.SDK.9 --exact --source winget`) on the Windows host, restart RoslynMcpServer, and try again.";
            }

            return baseMessage + $" Missing assembly: '{exception.FileName}'. Install the required .NET workload/targeting pack and retry.";
        }

        private static string BuildReflectionLoadFailureMessage(string solutionPath, ReflectionTypeLoadException exception)
        {
            var solutionName = Path.GetFileName(solutionPath);
            var baseMessage = $"MSBuild failed to load {(string.IsNullOrWhiteSpace(solutionName) ? solutionPath : solutionName)} because a referenced .NET assembly could not be loaded.";
            var loaderException = exception.LoaderExceptions?.FirstOrDefault();
            if (loaderException is FileNotFoundException fileNotFound)
            {
                return BuildAssemblyLoadFailureMessage(solutionPath, fileNotFound);
            }

            return baseMessage + " Check that the required .NET targeting packs/SDKs are installed and retry.";
        }

        private static string BuildFriendlyLoadFailureMessage(string solutionPath, string diagnosticMessage)
        {
            var solutionName = Path.GetFileName(solutionPath);
            var baseMessage = $"MSBuild failed to load {(string.IsNullOrWhiteSpace(solutionName) ? solutionPath : solutionName)}.";
            var lowerMessage = diagnosticMessage.ToLowerInvariant();

            if (lowerMessage.Contains("was not found") &&
                lowerMessage.Contains("depends on"))
            {
                return $"{baseMessage} A referenced NuGet package could not be restored ({diagnosticMessage}). " +
                       "Install the missing package or run 'dotnet restore' for the repository, then retry the tool.";
            }

            if (lowerMessage.Contains("was restored using") &&
                lowerMessage.Contains("instead of the project target framework"))
            {
                return $"{baseMessage} One of the NuGet packages only provides assemblies for an older framework ({diagnosticMessage}). " +
                       "Update that package to a version that targets your project's framework or retarget the project.";
            }

            if (lowerMessage.Contains("project.assets.json") ||
                lowerMessage.Contains("assets file") ||
                lowerMessage.Contains("nu110"))
            {
                return $"{baseMessage} Required restore assets are missing or out of date. Run 'dotnet restore \"{solutionPath}\"' (or from the repository root) and then rerun the tool. " +
                       $"MSBuild reported: {diagnosticMessage}";
            }

            if (lowerMessage.Contains("getsdktoolinginfo") ||
                lowerMessage.Contains("getframeworkpath") ||
                (lowerMessage.Contains("the framework") && lowerMessage.Contains("was not found")) ||
                lowerMessage.Contains("netsdk"))
            {
                return $"{baseMessage} The .NET SDK or targeting pack for this framework is not installed. Install the required workload (e.g., 'dotnet workload install windows' for net8.0-windows) or add the targeting pack via Visual Studio Installer. " +
                       $"MSBuild reported: {diagnosticMessage}";
            }

            return $"{baseMessage} MSBuild reported: {diagnosticMessage}";
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

        public async Task<IReadOnlyList<SolutionProjectInfo>> GetSolutionOverviewAsync(string solutionPath, CancellationToken cancellationToken = default)
        {
            var solution = await GetSolutionAsync(solutionPath, cancellationToken).ConfigureAwait(false);
            var overview = new List<SolutionProjectInfo>(solution.ProjectIds.Count);

            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameworks = ReadTargetFrameworks(project.FilePath);
                overview.Add(new SolutionProjectInfo(
                    project.Name,
                    project.FilePath ?? string.Empty,
                    frameworks,
                    project.AssemblyName,
                    project.Id.Id.ToString()));
            }

            return overview;
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

        private static IReadOnlyList<string> ReadTargetFrameworks(string? projectFilePath)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
            {
                return Array.Empty<string>();
            }

            try
            {
                var doc = XDocument.Load(projectFilePath);
                if (doc.Root == null)
                {
                    return Array.Empty<string>();
                }

                var multi = doc
                    .Descendants()
                    .FirstOrDefault(node => node.Name.LocalName.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrWhiteSpace(multi))
                {
                    return multi
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToArray();
                }

                var single = doc
                    .Descendants()
                    .FirstOrDefault(node => node.Name.LocalName.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrWhiteSpace(single))
                {
                    return new[] { single.Trim() };
                }
            }
            catch
            {
                // Ignore parse failures and fall back to empty list.
            }

            return Array.Empty<string>();
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
