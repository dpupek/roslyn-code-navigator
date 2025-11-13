# Roslyn Code Navigator

**“Unparalleled code exploration and refactoring assistance.”** Point Codex at any Windows solution—C#, VB, hybrid—and instantly search symbols, trace references, inspect dependencies, and surface complexity hotspots with compiler-accurate results.

## Features

- **"Ctrl-T" for your AI agent**
- **Wildcard Symbol Search** - Find classes, methods, and properties using pattern matching (`*Service`, `Get*User`, etc.)
- **Reference Tracking** - Locate all usages of symbols across entire solutions
- **Symbol Information** - Get detailed information about types, methods, properties, and more
- **Dependency Analysis** - Analyze project dependencies and namespace usage patterns
- **Code Complexity Analysis** - Identify high-complexity methods using cyclomatic complexity metrics
- **Performance Optimized** - Multi-level caching and incremental analysis for large codebases
- **Security** - Input validation and path sanitization
- **Mixed-language support** - Handles solutions containing both C# and VB.NET projects

## Table of Contents
- [Roslyn Code Navigator](#roslyn-code-navigator)
  - [Features](#features)
  - [Table of Contents](#table-of-contents)
  - [Prerequisites](#prerequisites)
  - [Setup (Zero-to-Ready Guide)](#setup-zero-to-ready-guide)
    - [1. Download the sources](#1-download-the-sources)
    - [2. Restore \& build once](#2-restore--build-once)
    - [3. Optional: local smoke test](#3-optional-local-smoke-test)
    - [4. Publish a self-contained Windows EXE](#4-publish-a-self-contained-windows-exe)
    - [Configure Codex to launch the EXE](#configure-codex-to-launch-the-exe)
    - [Logging, NuGet \& environment notes](#logging-nuget--environment-notes)
    - [Paths and solution visibility](#paths-and-solution-visibility)
    - [Verify the connection](#verify-the-connection)
  - [Usage](#usage)
  - [Prompt Recipes \& Nudges](#prompt-recipes--nudges)
  - [Available Tools](#available-tools)
  - [Development and Testing](#development-and-testing)
    - [Using MCP Inspector](#using-mcp-inspector)
    - [Building while the server is running](#building-while-the-server-is-running)
  - [Architecture](#architecture)
  - [License](#license)
  - [Author](#author)
  - [Contributing](#contributing)
## Prerequisites

- .NET 8.0 SDK or later
- Visual Studio 2022 or VS Code (recommended for development)
- Claude Desktop application

## Setup (Zero-to-Ready Guide)

> All commands below assume you’re in a Windows PowerShell prompt (or the VS Developer PowerShell).

### 1. Download the sources
```powershell
git clone https://github.com/carquiza/RoslynMCP.git
cd RoslynMCP/RoslynMcpServer
```

### 2. Restore & build once
```powershell
dotnet restore
dotnet build
```

### 3. Optional: local smoke test
```powershell
dotnet run
```
Keep the window open until you see `Starting Roslyn MCP Server...`, then press Ctrl+C. This step just confirms MSBuild/SDKs are in good shape.

### 4. Publish a self-contained Windows EXE
Publishing once keeps startup instant for Codex and avoids restoring packages every time.
```powershell
# Example publish to E:\Apps\RoslynMcp
dotnet publish RoslynMcpServer/RoslynMcpServer.csproj `
  -c Release `
  -r win-x64 `
  -p:SelfContained=true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -o E:\Apps\RoslynMcp
```
Use `-o` (or `--output`) to pick a stable folder. Codex will later point to `E:\Apps\RoslynMcp\RoslynMcpServer.exe`.

### Configure Codex to launch the EXE

Codex expects MCP servers to be listed under `[mcp_servers]` in `~/.codex/config.toml` (see the [Codex config docs](https://github.com/openai/codex/blob/main/docs/config.md#mcp-integration)). Add or update an entry similar to the one below, substituting your local paths from the publish step:

```toml
[mcp_servers.roslyn_code_navigator]
# Option A: Launch Windows dotnet (recommended if your solution relies on VS/Windows NuGet fallbacks)
command = "/mnt/e/Apps/RoslynMcp/RoslynMcpServer.exe"
args = []

# Line Breaks are not allowed in the env property
env = { DOTNET_ENVIRONMENT = "Production", LOG_LEVEL = "Information", ROSLYN_LOG_LEVEL = "Debug", ROSLYN_VERBOSE_SECURITY_LOGS = "false", ROSLYN_MAX_PROJECT_CONCURRENCY = "4" }
# Optional overrides
startup_timeout_sec = 30
tool_timeout_sec = 120
```

The Codex CLI will launch the MCP server via `codex mcp start roslyn_code_navigator` (or automatically when a tool request requires it) and communicate over stdio.

### Logging, NuGet & environment notes

- `ROSLYN_LOG_LEVEL` overrides the console log threshold used by the server (falls back to `LOG_LEVEL` if not set). Valid values match `Microsoft.Extensions.Logging.LogLevel` (`Trace`, `Debug`, `Information`, etc.).
- `ROSLYN_VERBOSE_SECURITY_LOGS` enables detailed reasoning when solution-path validation fails, which is useful when agents surface `Invalid solution path provided.`. Set it to `true` to emit warnings with the exact failure reason.
- NuGet fallbacks are validated at startup. If required fallback folders don’t exist, the server logs a clear error and exits immediately rather than hanging during MSBuild package resolution. You can override/define:
  - `NUGET_PACKAGES` (defaults to `~/.nuget/packages` if unset)
  - `NUGET_FALLBACK_PACKAGES` and/or `RestoreAdditionalProjectFallbackFolders` (on Windows defaults to `C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages`; on WSL we translate that path when present).
- If your Codex CLI runs under WSL but your repo depends on Windows-only NuGet fallback folders, point the MCP config at the Windows `dotnet.exe` and pass a Windows-style project path (as shown above). Codex will still proxy the stdio bridge across WSL, but MSBuild runs in Windows, so restore/build steps succeed.
- Concurrency: `ROSLYN_MAX_PROJECT_CONCURRENCY` controls the number of projects compiled in parallel during symbol searches to reduce memory pressure on large solutions.
- Timeouts and cancellation: MCP tool calls time out by default after `tool_timeout_sec` (e.g., 120s). All tools propagate cancellation tokens and will stop work promptly when the client cancels.

| env key | What it does |
| --- | --- |
| `DOTNET_ENVIRONMENT` | Standard .NET hosting environment; keep at `Production` for the published exe. |
| `LOG_LEVEL` | Base log level for everything (Information is a good default). |
| `ROSLYN_LOG_LEVEL` | Lets you crank Roslyn-specific logs higher/lower than the base level. |
| `ROSLYN_VERBOSE_SECURITY_LOGS` | When `true`, prints the exact reason a solution path was rejected (missing SDK, invalid path, etc.). |
| `ROSLYN_MAX_PROJECT_CONCURRENCY` | Caps concurrent project compilations; lower values reduce memory usage on huge solutions. |

### Paths and solution visibility
- When launching with Windows `dotnet.exe`, prefer Windows-style paths in args and tool inputs (e.g., `E:\\...\\Solution.sln`).
- When launching with Linux `dotnet`, use WSL paths (e.g., `/mnt/e/.../Solution.sln`). Ensure the file paths are accessible from the chosen runtime.
- The server automatically translates between Windows drive-letter paths and `/mnt/<drive>/...` paths when possible, so you can paste whatever form you have handy; if it cannot safely convert, you still get a human-friendly hint instead of a generic failure.
- Before MSBuild loads the solution, the server inspects all projects, determines their target frameworks, and verifies that the corresponding SDKs/targeting packs are installed. Missing SDKs produce actionable error messages describing which frameworks require attention.

### Verify the connection
1. Start the server: `codex mcp start roslyn_code_navigator`
2. Run `codex mcp tools roslyn_code_navigator list` – the tools should appear.
3. Try `codex mcp call roslyn_code_navigator ShowHelp` to ensure responses flow correctly.
4. Point a tool (e.g., SearchSymbols) at a small solution to confirm MSBuild loads successfully.

## Usage

Once configured, restart Codex (or the CLI) so it picks up the new MCP entry. When the assistant needs code analysis, it will spin up the server automatically. Here are a few friendly prompts you can paste into chat:

1. **Search for symbols**
   ```
   Use the roslyn_code_navigator MCP server to search for all classes ending with 'Service' in C:\MyProject\MyProject.sln.
   ```

2. **Find references**
   ```
   Please run FindReferences via roslyn_code_navigator on C:\MyProject\MyProject.sln for symbol "UserRepository".
   ```
   _Tip: `symbolName` understands wildcards, so `My.Namespace.Text*.*` matches everything under that namespace tree._

3. **Get symbol info**
   ```
   Call GetSymbolInfo for MyCompany.Services.Logging.SerilogWidget.ProcessAndLog using roslyn_code_navigator.
   ```

4. **Analyze dependencies**
   ```
   Ask roslyn_code_navigator to analyze dependencies for C:\MyProject\MyProject.sln with depth 2.
   ```

5. **Code complexity**
   ```
   Use the AnalyzeCodeComplexity tool (threshold 7) on C:\MyProject\MyProject.sln.
   ```

Each of these examples mentions the `roslyn_code_navigator` server explicitly, which nudges Codex to use it instead of guessing or browsing. Feel free to adapt the wording to your scenario.

## Prompt Recipes & Nudges

Claude/Codex usually auto-detects when it needs the Roslyn MCP server, but here are some friendly prompts that consistently nudge it toward `roslyn_code_navigator`:

1. **Direct tool hint**
   ```
   Use the roslyn_code_navigator MCP server to run SearchSymbols on C:\Repo\App.sln for pattern "*Service".
   ```
   Mentioning the server name plus the tool you want is often enough.

2. **Explain why you need Roslyn**
   ```
   I need compiler-accurate references (not grep). Please run FindReferences via roslyn_code_navigator on E:\Solution\Foo.sln for symbol "OrderManager".
   ```
   Calling out “compiler-accurate” or “MSBuild solution” tells the assistant grep/web search won’t help.

3. **Ask for server help first**
   ```
   Before you start, call the ShowHelp tool from roslyn_code_navigator so you know what capabilities are available.
   ```
   Once the server is active, follow up with the specific request.

4. **Explicit multi-step instructions**
   ```
   Step 1: Start the roslyn_code_navigator server if it isn’t running.
   Step 2: Use its FindReferences tool on ... 
   ```
   Even though Codex auto-starts servers, the “Step 1/Step 2” pattern sets expectations.

5. **Fallback friendly reminder**
   If the assistant tries to install another tool or browse the web, reply with:
   ```
   Please don’t search online—use the local roslyn_code_navigator MCP server for this analysis.
   ```

There’s no extra wiring needed on the server; good prompts plus the `ShowHelp` tool usually keep the assistant on the right path.

## Available Tools

1. **SearchSymbols** - Search for symbols using wildcard patterns
2. **FindReferences** - Find all references to a specific symbol
3. **GetSymbolInfo** - Get detailed information about a symbol
4. **AnalyzeDependencies** - Analyze project dependencies and usage patterns
5. **AnalyzeCodeComplexity** - Identify high-complexity methods
6. **ShowHelp** - Return the built-in help/recipes document

## Development and Testing

### Using MCP Inspector

For development and testing, you can use the MCP Inspector:

```bash
# Install the inspector
npm install -g @modelcontextprotocol/inspector

# Test your server
npx @modelcontextprotocol/inspector dotnet run --project ./RoslynMcpServer
```

### Building while the server is running
On Windows, rebuilding while `RoslynMcpServer` is running can fail with file lock warnings. Stop the running MCP server (or build to a different output folder with `-o`) before rebuilding.

## Architecture

The server features a modular architecture with:

- **MCP Server Layer**: Handles communication with Claude Desktop
- **Roslyn Integration Layer**: Manages workspaces and compilations
- **Search Engine Layer**: Implements symbol search and analysis
- **Multi-level Caching**: Performance optimization for large codebases
- **Security Layer**: Input validation and sanitization

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Author

Christopher Arquiza

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
