# Roslyn Code Navigator Help

Welcome! This server exposes C# code navigation and analysis tooling through the Model Context Protocol (MCP). Use this help resource when you need a quick reminder of the available capabilities.

## Available Tools

- **SearchSymbols** – Wildcard search across symbol names (`*Service`, `Get*User`, etc.).
- **FindReferences** – Locate every reference to a specific symbol.
- **GetSymbolInfo** – Retrieve metadata (signature, accessibility, attributes) for a symbol.
- **AnalyzeDependencies** - Summarize project/package relationships and namespace usage.
- **AnalyzeCodeComplexity** - Report methods whose cyclomatic complexity exceeds a threshold.
- **ShowHelp** - Emit this help document directly via an MCP tool call.
- Mixed-language solutions containing both C# and VB.NET projects are supported; VB symbols and references appear alongside C# ones.

## Usage Tips

1. Always provide an absolute path to the `.sln` you want to inspect.
2. You can narrow symbol searches by comma-separated kinds (e.g., `class,method`).
3. Dependency analysis may take longer on large solutions-expect a few seconds.
4. Complexity analysis defaults to a threshold of 5; raise it if you only want the worst offenders.
5. `FindReferences` and `GetSymbolInfo` accept fully qualified names (with `.` separators) and wildcards, so patterns like `My.Namespace.Text*.*` can target groups of types/members; omit the namespace to stick with the legacy exact-name lookup.
6. When something fails, check the MCP server logs (stderr) for detailed diagnostics.
7. If the MCP server is running from WSL, pass the `/mnt/<drive>/...` form of the solution path; Windows-style `E:\...` paths will be rejected. Conversely, when running on Windows use the drive-qualified form.

## Recipes

### Rename a Method (or Type) Safely
1. Use **SearchSymbols** with `symbolTypes="method"` (or `class`, etc.) to locate the declaration and confirm the namespace/assembly.
2. Call **FindReferences** with `symbolName` set to the method name (or qualified wildcard such as `My.Namespace.Service*.Process*`) to enumerate every usage and the definition site.
3. Rename the symbol in its source file, then update each reference reported in step 2. Because `FindReferences` returns exact file/line info, you can work through the list methodically.
4. Re-run **FindReferences** with the *old* name; the result should come back empty. Optionally run it again with the new name to verify the symbol is discoverable in its new form.
5. Finish by rebuilding or running targeted tests to confirm behavior.

### Map the Workspace Quickly
1. Call **SearchSymbols** with a broad pattern (e.g., `*Controller`, `symbolTypes=class`) to see what modules exist.
2. Follow up with **AnalyzeDependencies** (`maxDepth=2`) on the same solution to learn which projects depend on each other.
3. Capture both outputs for your session notes before diving into a feature.

### Track Interface Implementations
1. Run **FindReferences** on the interface name with `includeDefinition=true` to list the definition plus all usage sites.
2. Filter the results to definition entries—each will point to a class implementing the interface.
3. For a large interface, layer in **SearchSymbols** with `pattern="*YourInterface*"` to double-check no implementations are missed.

### Pre-refactor Impact Check
1. Use **FindReferences** on the symbol you intend to rename/move.
2. Paste the reference list into your PR plan or notebook.
3. After the change, re-run **FindReferences** (old name) to ensure nothing references the stale identifier; run it again with the new name to confirm it’s discoverable.

### Dependency Impact Scan
1. Before touching a shared project, run **AnalyzeDependencies** on the solution with a low depth (1–2).
2. Look for projects/packages that list the target project as a dependency; note them in your plan.
3. If any dependencies look unexpected, inspect their project files directly before proceeding.

### Cross-language Sanity Check
1. When a solution mixes C# and VB, run **SearchSymbols** twice: once for a C# type, once for a VB type (e.g., `LegacyCalculator`).
2. Use **FindReferences** on the VB type to confirm cross-language call sites appear.
3. If anything fails to load, check logs for `"language 'Visual Basic' is not supported"`—it usually means VB targeting packs are missing.

### Hotspot / Complexity Sweep
1. Run **AnalyzeCodeComplexity** with a moderate threshold (e.g., 7).
2. Sort the output by complexity and cross-reference with `git blame` to see which high-complexity methods churn frequently.
3. Use the list to propose refactors or add guardrails before touching risky code.

## Safety & Validation

- Solution paths are validated to avoid directory traversal and must exist on disk.
- Path style is auto-corrected when possible (e.g., `/mnt/c/...` to `C:\...` or vice versa); if conversion is impossible, the server returns a friendly hint about which style to use.
- Before loading a solution the server checks the target frameworks and ensures the matching .NET SDK / targeting packs are installed, returning actionable errors when they are missing.
- Search patterns are sanitized to alphanumerics plus `*` and `?`.
- File analysis results are cached so repeated requests are faster.

## Need More?

Open this resource again anytime with a `read_resource` call for `roslyn-help`. Feel free to extend this file with project-specific guidance.
