# Roslyn Code Navigator MCP (codenav-mcp)

Roslyn Code Navigator is a production-grade MCP server that turns large .NET codebases into a queryable knowledge surface for AI agents. It provides compiler-accurate symbol search, reference tracing, dependency/complexity analysis, and safe build/test execution across mixed C#/VB and legacy .NET Framework solutions—bridging WSL and Windows toolchains so agents can operate reliably on real enterprise repositories.

## Links

- Repository: https://github.com/dpupek/roslyn-code-navigator
- Documentation: https://github.com/dpupek/roslyn-code-navigator/blob/main/RoslynMcpServer/help.md
- Issue tracker: https://github.com/dpupek/roslyn-code-navigator/issues
- Releases: https://github.com/dpupek/roslyn-code-navigator/releases

## Install / Update

```bash
dotnet tool install --global Dpupek.Code.Nav.Mcp
```

```bash
dotnet tool update --global Dpupek.Code.Nav.Mcp
```

## Run

```bash
codenav-mcp
```

## Quickstart (MCP clients)

1. Install the tool (above).
2. Add an MCP server entry to your client config:
   ```toml
   [mcp_servers.roslyn_code_navigator]
   command = "codenav-mcp"
   ```
3. Trigger any tool to start the server (example: `tools/list`).

## Capabilities (high level)

- Wildcard symbol search and symbol metadata
- Reference and implementation tracking
- Dependency and complexity analysis
- Solution/project inventory + environment diagnostics
- `dotnet build/test`, MSBuild, and vstest execution

## Notes

- Configure your MCP client to launch the tool with the `codenav-mcp` command.
- For full usage, recipes, and detailed tool docs, see the repository README and help file.
