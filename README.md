# Roslyn MCP Server

A C# MCP (Model Context Protocol) server that integrates with Microsoft's Roslyn compiler platform to provide Claude Desktop with code analysis and navigation capabilities for C# codebases.

## Features

- **Wildcard Symbol Search** - Find classes, methods, and properties using pattern matching (`*Service`, `Get*User`, etc.)
- **Reference Tracking** - Locate all usages of symbols across entire solutions
- **Symbol Information** - Get detailed information about types, methods, properties, and more
- **Dependency Analysis** - Analyze project dependencies and namespace usage patterns
- **Code Complexity Analysis** - Identify high-complexity methods using cyclomatic complexity metrics
- **Performance Optimized** - Multi-level caching and incremental analysis for large codebases
- **Security** - Input validation and path sanitization

## Prerequisites

- .NET 8.0 SDK or later
- Visual Studio 2022 or VS Code (recommended for development)
- Claude Desktop application

## Installation

1. **Clone or download the project**
   ```bash
   git clone https://github.com/carquiza/RoslynMCP.git
   cd RoslynMCP/RoslynMcpServer
   ```

2. **Restore NuGet packages**
   ```bash
   dotnet restore
   ```

3. **Build the project**
   ```bash
   dotnet build
   ```

4. **Test the server** (optional)
   ```bash
   dotnet run
   ```

## Quick Setup

### Recommended: Publish a self-contained Windows EXE
This avoids rebuilds on each launch and is robust when multiple agents use the server simultaneously.

1) Publish
```powershell
dotnet publish RoslynMcpServer/RoslynMcpServer.csproj -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:PublishTrimmed=false
```

2) Configure Codex to launch the EXE
```toml
[mcp_servers.roslyn_code_navigator]
command = "E:\\Sandbox\\RoslynMCP\\RoslynMCP\\RoslynMcpServer\\bin\\Release\\net8.0\\win-x64\\publish\\RoslynMcpServer.exe"
args = []
env = {
  DOTNET_ENVIRONMENT = "Production",
  LOG_LEVEL = "Information",
  ROSLYN_LOG_LEVEL = "Debug",
  ROSLYN_VERBOSE_SECURITY_LOGS = "false",
  ROSLYN_MAX_PROJECT_CONCURRENCY = "4"
}
startup_timeout_sec = 30
tool_timeout_sec = 120
```

WSL users can reference the same EXE via the `/mnt` path (ensure interop is enabled):
```toml
[mcp_servers.roslyn_code_navigator]
command = "/mnt/e/Sandbox/RoslynMCP/RoslynMcpServer/bin/Release/net8.0/win-x64/publish/RoslynMcpServer.exe"
args = []
env = { DOTNET_ENVIRONMENT = "Production", LOG_LEVEL = "Information", ROSLYN_LOG_LEVEL = "Debug" }
startup_timeout_sec = 30
tool_timeout_sec = 120
```

### Windows
Run the PowerShell setup script:
```powershell
.\setup.ps1
```

### Linux/macOS
Run the installation test:
```bash
./test-installation.sh
```

## Claude Desktop Configuration

To connect this MCP server to Claude Desktop, you need to modify the Claude Desktop configuration file:

### Configuration File Location

- **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`
- **macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`

### Configuration Content

Add the following to your `claude_desktop_config.json` file:

```json
{
  "mcpServers": {
    "roslyn-code-navigator": {
      "command": "dotnet",
      "args": [
        "run", 
        "--project", 
        "/absolute/path/to/RoslynMCP/RoslynMcpServer"
      ],
      "env": {
        "DOTNET_ENVIRONMENT": "Production",
        "LOG_LEVEL": "Information",
        "ROSLYN_LOG_LEVEL": "Debug",
        "ROSLYN_VERBOSE_SECURITY_LOGS": "false"
      }
    }
  }
}
```

Important: Use an absolute path. If you are running on Windows Subsystem for Linux (WSL) but want to use Windows MSBuild, prefer the Codex CLI configuration below which launches the Windows `dotnet.exe`.

## Codex CLI Configuration

Codex expects MCP servers to be configured under the `[mcp_servers]` table in `~/.codex/config.toml` (see the [Codex config docs](https://github.com/openai/codex/blob/main/docs/config.md#mcp-integration) for details). Add an entry like the following, updating the absolute paths so they point at your local checkout:

```toml
[mcp_servers.roslyn_code_navigator]
# Option A: Launch Windows dotnet (recommended if your solution relies on VS/Windows NuGet fallbacks)
command = "/mnt/c/Program Files/dotnet/dotnet.exe"
args = ["run", "--project", "E:\\Sandbox\\RoslynMCP\\RoslynMCP\\RoslynMcpServer\\RoslynMcpServer.csproj"]

# Option B: Launch Linux dotnet inside WSL (make sure NuGet fallbacks exist on Linux)
# command = "dotnet"
# args = ["run", "--project", "/mnt/e/Sandbox/RoslynMCP/RoslynMcpServer/RoslynMcpServer.csproj"]

env = {
  DOTNET_ENVIRONMENT = "Production",
  LOG_LEVEL = "Information",
  ROSLYN_LOG_LEVEL = "Debug",
  ROSLYN_VERBOSE_SECURITY_LOGS = "false",
  # Tune how many projects compile concurrently (default heuristic: cores/2, max 8)
  ROSLYN_MAX_PROJECT_CONCURRENCY = "4"
}
# Optional overrides
startup_timeout_sec = 30
tool_timeout_sec = 120
```

The Codex CLI will launch the MCP server via `codex mcp start roslyn_code_navigator` (or automatically when a tool request requires it) and communicate over stdio.

### Codex CLI commands
- Start the server manually: `codex mcp start roslyn_code_navigator`
- List available tools: `codex mcp tools roslyn_code_navigator list`
- Validate server is reachable: `codex mcp ping roslyn_code_navigator`
- Tail server logs (if supported by your shell setup): check stderr output in your terminal or use your terminal multiplexer.

Note: Adding a server is done by editing `~/.codex/config.toml` as shown above. Some Codex builds may include an interactive helper, but it is not required; editing the config and running `codex mcp start <name>` is sufficient.

### Logging, NuGet & environment notes

- `ROSLYN_LOG_LEVEL` overrides the console log threshold used by the server (falls back to `LOG_LEVEL` if not set). Valid values match `Microsoft.Extensions.Logging.LogLevel` (`Trace`, `Debug`, `Information`, etc.).
- `ROSLYN_VERBOSE_SECURITY_LOGS` enables detailed reasoning when solution-path validation fails, which is useful when agents surface `Invalid solution path provided.`. Set it to `true` to emit warnings with the exact failure reason.
- NuGet fallbacks are validated at startup. If required fallback folders don’t exist, the server logs a clear error and exits immediately rather than hanging during MSBuild package resolution. You can override/define:
  - `NUGET_PACKAGES` (defaults to `~/.nuget/packages` if unset)
  - `NUGET_FALLBACK_PACKAGES` and/or `RestoreAdditionalProjectFallbackFolders` (on Windows defaults to `C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages`; on WSL we translate that path when present).
- If your Codex CLI runs under WSL but your repo depends on Windows-only NuGet fallback folders, point the MCP config at the Windows `dotnet.exe` and pass a Windows-style project path (as shown above). Codex will still proxy the stdio bridge across WSL, but MSBuild runs in Windows, so restore/build steps succeed.
- Concurrency: `ROSLYN_MAX_PROJECT_CONCURRENCY` controls the number of projects compiled in parallel during symbol searches to reduce memory pressure on large solutions.
- Timeouts and cancellation: MCP tool calls time out by default after `tool_timeout_sec` (e.g., 120s). All tools propagate cancellation tokens and will stop work promptly when the client cancels.

### Paths and solution visibility
- When launching with Windows `dotnet.exe`, prefer Windows-style paths in args and tool inputs (e.g., `E:\\...\\Solution.sln`).
- When launching with Linux `dotnet`, use WSL paths (e.g., `/mnt/e/.../Solution.sln`). Ensure the file paths are accessible from the chosen runtime.

## Usage

Once configured, restart Claude Desktop. You should see the Roslyn MCP Server appear in the available tools. Here are some example queries:

### Search for Symbols
```
Search for all classes ending with 'Service' in my solution at C:\MyProject\MyProject.sln
```

### Find References
```
Find all references to the UserRepository class in C:\MyProject\MyProject.sln
```

### Get Symbol Information
```
Get information about the CalculateTotal method in C:\MyProject\MyProject.sln
```

### Analyze Dependencies
```
Analyze dependencies for the solution at C:\MyProject\MyProject.sln
```

### Code Complexity Analysis

Note: Long-running operations include bounded parallelism and fail-fast cancellation. If an operation is canceled by the client (e.g., tool timeout), work will stop promptly and return an error message; there is no protocol-level heartbeat, so rely on logs for live progress.
```
Find methods with complexity higher than 7 in C:\MyProject\MyProject.sln
```

## Available Tools

1. **SearchSymbols** - Search for symbols using wildcard patterns
2. **FindReferences** - Find all references to a specific symbol
3. **GetSymbolInfo** - Get detailed information about a symbol
4. **AnalyzeDependencies** - Analyze project dependencies and usage patterns
5. **AnalyzeCodeComplexity** - Identify high-complexity methods

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
