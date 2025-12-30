# Roslyn Code Navigator MCP (codenav-mcp)

This package installs the Roslyn MCP server as a .NET tool. It exposes compiler-accurate C#/VB symbol search, reference finding, dependency analysis, and build/test runner tools via the Model Context Protocol (MCP).

## Install

```bash
dotnet tool install --global Dpupek.Code.Nav.Mcp
```

## Run

```bash
codenav-mcp
```

## Notes

- Configure your MCP client to launch the tool with the `codenav-mcp` command.
- For full usage and recipes, see the repo README and `RoslynMcpServer/help.md`.
