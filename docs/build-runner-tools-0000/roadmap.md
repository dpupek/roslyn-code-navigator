# Build/Test Runner Tools – Roadmap

> _Placeholder case number; update filename when FogBugz epic ID is assigned._

## Phase 1 – Discovery & Foundations
- [ ] Inventory existing Windows toolchains (dotnet SDKs, VS editions) via `roslyn_env` and prototype `ListBuildRunners` output.
- [ ] Define MCP tool schemas (`BuildSolution`, `TestSolution`, `LegacyMsBuild`, `LegacyVsTest`, `ListInstalledSdks`).
- [ ] Update `help.md`/recipes with provisional usage examples and troubleshooting guidance for permissions + SDK detection.
- [ ] Validate cross-solution requirements (net10 + net462) using `TestAssets/SampleSolution` and a new legacy fixture.

## Phase 2 – Runner Implementations
- [ ] Implement `RunnerSelector`, `DotnetSdkRunner`, `VsMsbuildRunner`, and `VsTestRunner` wired through a new `BuildExecutionService`.
- [ ] Expose `BuildSolution`/`TestSolution` MCP tools that wrap Windows `dotnet.exe` with logging + cancellation support.
- [ ] Add legacy `LegacyMsBuild`/`LegacyVsTest` tools invoking VS binaries, including platform/config parameters and TRX/binlog handling.
- [ ] Extend `roslyn_env` output to include resolved runner paths and environment variables (DOTNET_ROOT, MSBUILDDISABLENODEREUSE).

## Phase 3 – Validation & Docs
- [ ] Add unit/integration tests (see test plan) covering SDK selection, legacy fallback, and friendly errors.
- [ ] Update `agents.md` + `help.md` with final instructions, approvals guidance, and recipes mapping old shell commands to MCP tool invocations.
- [ ] Capture telemetry/logging for runner selection decisions and pipe into `DiagnosticLogger`.
- [ ] Review concurrency/resource policies; document recommendations for multi-agent environments.

## Phase 4 – Stretch Goals
- [ ] Optional binlog collection flag that uploads artifacts for download via MCP resource endpoints.
- [ ] Support scripted pipelines (Jenkins/AzDO) by exposing CLI snippets generated from MCP tool inputs.
- [ ] Investigate queueing/back-pressure mechanisms if simultaneous builds exceed host capacity.
