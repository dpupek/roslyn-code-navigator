# Roslyn MCP Roadmap / Plan

## Tooling Enhancements
- [x] **Solution Overview Tool** (`ListProjects` / `RoslynSolutionInfo`)
  - Summarize all projects in a solution: name, path, target frameworks.
  - Surface MSBuild load diagnostics (missing TFMs, unloaded projects).
  - Expose counts via MCP so agents can confirm the right solution is loaded before deeper analysis.
- [x] **Implementation Finder Tool** (`FindImplementations` / `FindDerivedTypes`)
  - Enumerate classes implementing an interface or derived from a base type.
  - Provide file/line metadata similar to `FindReferences`.
  - Filter by language/project when needed to keep responses scoped.
- [ ] **Build/Test Runner Tools** (`BuildSolution`, `TestSolution`, `LegacyMsBuild`, `LegacyVsTest`)
  - Wrap Windows `dotnet.exe`, VS `MSBuild.exe`, and `vstest.console.exe` behind MCP so WSL agents can trigger builds/tests without manual shell hops.
  - Auto-select the runner based on target frameworks (SDK-style vs .NET Framework 4.x) with overrides for custom SDK versions.
  - Normalize paths between `/mnt/*` and Windows drives, enforce `MSBUILDDISABLENODEREUSE=1`, and stream structured logs + friendly errors (permission, SDK mismatch, missing packs).
  - Support optional `sdkVersion` parameter that pins to a specific installed SDK; default falls back to the runtime discovered by `roslyn_env`.

## Operational Tasks
- [x] Wire the new tools into `RoslynMcpServer.Tools` with proper descriptions and help docs.
- [ ] Add unit/integration tests covering the new tool behaviors using `TestAssets/SampleSolution`.
- [x] Document usage examples in `help.md` and update recipes where relevant (e.g., interface workflows).
- [ ] Extend docs with build/test wrapper guidance (agents.md, help.md, recipes) and permission troubleshooting.
- [ ] Add discovery endpoints (`ListBuildRunners`, `ListInstalledSdks`) so agents can inspect available toolchains before invoking builds/tests.

_Questions / Decisions_
- Should implementation finder support async streaming for very large hierarchies?
- Should solution overview include package references / TFMs per project or stay minimal?
- How should runner selection break ties when both dotnet SDK and VS MSBuild can build the target?
- Should build/test tools emit binary logs automatically or only on-demand?
