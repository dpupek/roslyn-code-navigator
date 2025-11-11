# Roslyn MCP Server Help

Welcome! This server exposes C# code navigation and analysis tooling through the Model Context Protocol (MCP). Use this help resource when you need a quick reminder of the available capabilities.

## Available Tools

- **SearchSymbols** – Wildcard search across symbol names (`*Service`, `Get*User`, etc.).
- **FindReferences** – Locate every reference to a specific symbol.
- **GetSymbolInfo** – Retrieve metadata (signature, accessibility, attributes) for a symbol.
- **AnalyzeDependencies** – Summarize project/package relationships and namespace usage.
- **AnalyzeCodeComplexity** – Report methods whose cyclomatic complexity exceeds a threshold.

## Usage Tips

1. Always provide an absolute path to the `.sln` you want to inspect.
2. You can narrow symbol searches by comma-separated kinds (e.g., `class,method`).
3. Dependency analysis may take longer on large solutions—expect a few seconds.
4. Complexity analysis defaults to a threshold of 5; raise it if you only want the worst offenders.
5. When something fails, check the MCP server logs (stderr) for detailed diagnostics.
6. If the MCP server is running from WSL, pass the `/mnt/<drive>/...` form of the solution path; Windows-style `E:\...` paths will be rejected. Conversely, when running on Windows use the drive-qualified form.

## Safety & Validation

- Solution paths are validated to avoid directory traversal and must exist on disk.
- Search patterns are sanitized to alphanumerics plus `*` and `?`.
- File analysis results are cached so repeated requests are faster.

## Need More?

Open this resource again anytime with a `read_resource` call for `roslyn-help`. Feel free to extend this file with project-specific guidance.
