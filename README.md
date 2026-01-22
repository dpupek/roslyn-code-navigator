# Roslyn Code Navigator - MCP Server

**"Unparalleled code exploration and refactoring assistance."** Point Codex at any Windows solution-C#, VB, hybrid-and instantly search symbols, trace references, inspect dependencies, and surface complexity hotspots with compiler-accurate results.

Use this server when you need compiler-accurate, MSBuild-aware analysis of medium-to-large .NET solutions (including mixed C#/VB and legacy .NET Framework) directly from your MCP-compatible client.

## Features

- **"Ctrl-T" for your AI agent**
- **Wildcard Symbol Search** - Find classes, methods, and properties using pattern matching (`*Service`, `Get*User`, etc.)
- **Reference & Implementation Tracking** - Locate all usages of symbols and map implementations/derived types
- **Symbol Information** - Get detailed information about types, methods, properties, and more
- **Dependency & Complexity Analysis** - Analyze project/package dependencies and flag high-complexity methods
- **Project & Environment Discovery** - List projects/TFMs and capture runtime/MSBuild inventories
- **Build & Test Runners** - Run `dotnet build/test` or Visual Studio `MSBuild.exe`/`vstest.console.exe` for legacy solutions
- **Performance Optimized** - Bounded concurrency, safe caching, and incremental analysis for large codebases
- **Security & Validation** - Input validation, path sanitization, and proactive NuGet fallback checks
- **Mixed-language support** - Handles solutions containing both C# and VB.NET projects

**Full tool details** live in `RoslynMcpServer/help.md`; the `ShowHelp` tool returns that file verbatim.

## Table of Contents
- [Roslyn Code Navigator - MCP Server](#roslyn-code-navigator---mcp-server)
  - [Features](#features)
  - [Table of Contents](#table-of-contents)
  - [Prerequisites](#prerequisites)
  - [Quickstart (Codex CLI)](#quickstart-codex-cli)
  - [Setup (Zero-to-Ready Guide)](#setup-zero-to-ready-guide)
    - [1. Download the sources](#1-download-the-sources)
    - [2. Restore \& build once](#2-restore--build-once)
    - [3. Optional: local smoke test](#3-optional-local-smoke-test)
    - [4. Publish a self-contained Windows EXE](#4-publish-a-self-contained-windows-exe)
    - [Configure Codex CLI](#configure-codex-cli)
    - [Logging, NuGet \& environment notes](#logging-nuget--environment-notes)
    - [Paths and solution visibility](#paths-and-solution-visibility)
    - [Verify the connection](#verify-the-connection)
  - [Usage](#usage)
  - [Prompt Recipes \& Nudges](#prompt-recipes--nudges)
  - [Available Tools](#available-tools)
  - [Docs & Help](#docs--help)
  - [Development and Testing](#development-and-testing)
    - [Using MCP Inspector](#using-mcp-inspector)
    - [Building while the server is running](#building-while-the-server-is-running)
  - [Architecture](#architecture)
  - [License](#license)
  - [Author](#author)
  - [Contributing](#contributing)
## Prerequisites

- .NET 10.0 SDK (host runtime for `RoslynMcpServer`)
- Visual Studio 2022 or VS Code (recommended for development)
- An MCP-compatible client (e.g., Codex CLI, Claude Desktop)

## Quickstart (Codex CLI)

1. Clone and publish the server:
   ```powershell
   git clone https://github.com/dpupek/roslyn-code-navigator.git
   cd roslyn-code-navigator
   pwsh scripts/build-and-publish.ps1
   ```
2. Copy the printed `[mcp_servers.roslyn_code_navigator]` TOML block into your `~/.codex/config.toml`.
3. Restart Codex CLI so it picks up the new server.
4. Verify the connection:
   ```powershell
   codex mcp tools roslyn_code_navigator list
   codex mcp call roslyn_code_navigator ShowHelp
   ```
5. Start chatting and use the prompt recipes below to drive symbol search, dependency analysis, and build/test runs via `roslyn_code_navigator`.

## Setup (Zero-to-Ready Guide)

> All commands below assume you’re in a Windows PowerShell prompt (or the VS Developer PowerShell).

### 1. Download the sources
```powershell
git clone https://github.com/dpupek/roslyn-code-navigator.git
cd roslyn-code-navigator
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

### 4. Publish via the installer script (Windows PowerShell)
Run the helper script to publish and emit a WSL-friendly TOML snippet:
```powershell
pwsh scripts/build-and-publish.ps1
```
Defaults:
- Configuration: `Release`
- Runtime: `win-x64`
- Output: `%USERPROFILE%\.ros-code-nav` (overwrites if present)

The script prints a TOML block like:
```toml
[mcp_servers.roslyn_code_navigator]
name = "Roslyn Code Navigator"
command = "/mnt/c/Users/<you>/.ros-code-nav/RoslynMcpServer.exe"
# OPTIONAL LOGGING
# env = { ROSLYN_LOG_LEVEL = "Debug", ROSLYN_VERBOSE_SECURITY_LOGS = "true" }
# Optional overrides
startup_timeout_sec = 30
tool_timeout_sec = 120

# If your solution relies on COM references or legacy project types, you can point
# LegacyMsBuild at a specific MSBuild.exe via `preferredInstance`, e.g.
# C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe.
# The server will also auto-probe common VS 2019/2022 locations.
```
Copy that into your Codex MCP config (or adapt it for Claude Desktop). If you prefer manual publishing, you can still run `dotnet publish` yourself; just point the TOML `command` at the resulting exe.

### Configure Codex CLI

Codex expects MCP servers to be listed under `[mcp_servers]` in `~/.codex/config.toml` (see the [Codex config docs](https://github.com/openai/codex/blob/main/docs/config.md#mcp-integration)). Add or update an entry similar to the one below, substituting your local paths from the publish step:

```toml
[mcp_servers.roslyn_code_navigator]
command = "/mnt/e/Apps/RoslynMcp/RoslynMcpServer.exe"
```

The Codex CLI will launch the MCP server automatically when a tool request requires it and communicate over stdio. You can further customize behavior with environment variables and timeouts; see the logging and environment notes below.

### Logging, NuGet & environment notes

- `ROSLYN_LOG_LEVEL` overrides the console log threshold used by the server (falls back to `LOG_LEVEL` if not set). Valid values match `Microsoft.Extensions.Logging.LogLevel` (`Trace`, `Debug`, `Information`, etc.).
- `ROSLYN_VERBOSE_SECURITY_LOGS` enables detailed reasoning when solution-path validation fails, which is useful when agents surface `Invalid solution path provided.`. Set it to `true` to emit warnings with the exact failure reason.
- NuGet fallbacks are validated at startup. If required fallback folders don't exist, the server logs a clear error and exits immediately rather than hanging during MSBuild package resolution. You can override/define:
  - `NUGET_PACKAGES` (defaults to `~/.nuget/packages` if unset)
  - `NUGET_FALLBACK_PACKAGES` and/or `RestoreAdditionalProjectFallbackFolders` (on Windows defaults to `C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages`; on WSL we translate that path when present).
- If your Codex CLI runs under WSL but your repo depends on Windows-only NuGet fallback folders, point the MCP config at the Windows `dotnet.exe` and pass a Windows-style project path (as shown above). Codex will still proxy the stdio bridge across WSL, but MSBuild runs in Windows, so restore/build steps succeed.
- Concurrency: `ROSLYN_MAX_PROJECT_CONCURRENCY` controls the number of projects compiled in parallel during symbol searches to reduce memory pressure on large solutions.
- Timeouts and cancellation: MCP tool calls time out by default after `tool_timeout_sec` (e.g., 120s). All tools propagate cancellation tokens and will stop work promptly when the client cancels.
- WSL/Windows bridging: if your repo lives under `/mnt/*` in WSL but you want to force Windows `dotnet.exe`, set `NEXPORT_WINDOTNET` to the Windows .NET SDK root (for example, `"/mnt/c/Program Files/dotnet/"`); the publish script and server will honor it.
- Background runs: set `TERM=dumb`, `NO_COLOR=1`, and `CLICOLOR=0` in the server environment to reduce terminal/pty quirks when invoking Windows toolchains from WSL.
- MSBuild node reuse: the server disables node reuse (`MSBUILDDISABLENODEREUSE=1`) so server-run builds shut down cleanly. Avoid overriding this unless you fully control the machine.

| env key | What it does |
| --- | --- |
| `DOTNET_ENVIRONMENT` | Standard .NET hosting environment; keep at `Production` for the published exe. |
| `LOG_LEVEL` | Base log level for everything (Information is a good default). |
| `ROSLYN_LOG_LEVEL` | Lets you crank Roslyn-specific logs higher/lower than the base level. |
| `ROSLYN_VERBOSE_SECURITY_LOGS` | When `true`, prints the exact reason a solution path was rejected (missing SDK, invalid path, etc.). |
| `ROSLYN_MAX_PROJECT_CONCURRENCY` | Caps concurrent project compilations; lower values reduce memory usage on huge solutions. |
| `TERM`, `NO_COLOR`, `CLICOLOR` | Force non-interactive output to avoid terminal/PTY issues during background runs. |

### Paths and solution visibility
- When launching with Windows `dotnet.exe`, prefer Windows-style paths in args and tool inputs (e.g., `E:\\...\\Solution.sln`).
- When launching with Linux `dotnet`, use WSL paths (e.g., `/mnt/e/.../Solution.sln`). Ensure the file paths are accessible from the chosen runtime.
- The server automatically translates between Windows drive-letter paths and `/mnt/<drive>/...` paths when possible, so you can paste whatever form you have handy; if it cannot safely convert, you still get a human-friendly hint instead of a generic failure.
- Before MSBuild loads the solution, the server inspects all projects, determines their target frameworks, and verifies that the corresponding SDKs/targeting packs are installed. Missing SDKs produce actionable error messages describing which frameworks require attention.

### Verify the connection
1. Trigger any Roslyn tool (e.g., `codex mcp tools roslyn_code_navigator list`) to let Codex spin up the server.
2. Run `codex mcp tools roslyn_code_navigator list` – the tools should appear.
3. Try `codex mcp call roslyn_code_navigator ShowHelp` to ensure responses flow correctly.
4. Point a tool (e.g., SearchSymbols) at a small solution to confirm MSBuild loads successfully.

## Usage

Once configured, restart Codex (or the CLI) so it picks up the new MCP entry. When the assistant needs code analysis, it will spin up the server automatically.

> Recommended first step: call the `ShowHelp` tool from `roslyn_code_navigator` (or read `RoslynMcpServer/help.md`) so the assistant knows what tools and recipes are available.

Here are a few friendly prompts you can paste into chat:

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

Example end-to-end workflow (investigate a slow API controller):

1. Use `SearchSymbols` via `roslyn_code_navigator` to find `*Controller` classes in your solution.
2. Run `AnalyzeCodeComplexity` on the same solution (with a threshold like `7`) to flag hotspots.
3. For the most complex controller actions, call `FindReferences` to see where they are used and how they are composed across your codebase.

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
3. **FindImplementations** - List implementers of an interface or subclasses of a base type
4. **GetSymbolInfo** - Get detailed information about a symbol
5. **AnalyzeDependencies** - Analyze project/package dependencies and namespace usage patterns
6. **AnalyzeCodeComplexity** - Identify high-complexity methods
7. **ListProjects** - Show projects and their target frameworks in a solution
8. **RoslynEnv** - Display runtime/MSBuild environment and inventories for troubleshooting
9. **ListBuildRunners** - Enumerate available dotnet SDKs/runtimes and Visual Studio instances
10. **BuildSolution** - Run `dotnet build` using detected SDKs/targeting packs
11. **TestSolution** - Run `dotnet test` with optional TRX logging
12. **StartTest** - Start `dotnet test` asynchronously (returns run id + log paths)
13. **GetTestStatus** - Poll async test run status (state/exit code/log tails)
14. **CancelTestRun** - Cancel a running async test run
15. **ListTestRuns** - List active (and optionally recent) async test runs
16. **LegacyMsBuild** - Invoke Visual Studio `MSBuild.exe` for .NET Framework solutions/projects
17. **LegacyVsTest** - Invoke `vstest.console.exe` for .NET Framework test assemblies
18. **ShowHelp** - Return the built-in help/recipes document

## Docs & Help

- Quick reference: run `ShowHelp` or read `RoslynMcpServer/help.md` for the latest tool list, recipes, and environment notes.
- Full recipe coverage lives in `RoslynMcpServer/help.md`.

## Development and Testing

### Automated NuGet publishing
This repo publishes the .NET tool to nuget.org on tags matching `vX.Y.Z` via GitHub Actions. Create a tag and push it, and the workflow will pack and push:

```bash
git tag v0.1.9
git push origin v0.1.9
```

Make sure the repo has a `NUGET_API_KEY` secret configured with push permissions.

### Running tests from WSL (Windows dotnet)
Use the Windows `dotnet.exe` when the repo lives under `/mnt/*`:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test RoslynMCP.sln -v minimal --no-restore --logger "trx;LogFileName=artifacts/logs/tests.trx"
```

The TRX output ends up under `RoslynMcpServer.Tests/TestResults/artifacts/logs/tests.trx` (VSTest relocates it).

If the test run exits non-zero without visible errors, add diagnostics:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test RoslynMcpServer.Tests/RoslynMcpServer.Tests.csproj --no-restore -v minimal --logger "trx;LogFileName=artifacts/logs/tests.trx" --diag artifacts/logs/vstest.diag.log
```

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
