# Build/Test Runner Tools – Workflows

> _Placeholder case number – update folder name when the FogBugz epic ID is available._

## Personas & Motivations
- **WSL Agent Developer (primary)** – Runs Codex agents inside WSL but needs Windows-only toolchains (dotnet, VS MSBuild, vstest). Wants a single MCP command to compile/test any solution without path gymnastics or sudo prompts.
- **Windows Maintainer** – Maintains Jenkins/Azure DevOps jobs on native Windows. Wants parity with WSL experience plus visibility into which runner/SDK each job used.
- **Release Engineer** – Triages customer solutions targeting mixed TFMs (net10 + net462). Needs quick confirmation that the right SDK/reference packs were used to reproduce bugs.

## Workflow 1 – SDK Build from WSL
1. Agent runs `BuildSolution` MCP tool with `/mnt/.../Solution.sln`.
2. Tool validates path ownership via `SecurityValidator`, translates to `E:\...`.
3. `RoslynEnv` data provides installed dotnet SDK list; tool either uses default (latest) or the requested `sdkVersion`.
4. Build service spawns `"$DOTNET_ROOT/dotnet.exe" build <solution> -c <config> -p:<props>` with `MSBUILDDISABLENODEREUSE=1` and streaming logs.
5. Structured output is returned (success/failure, elapsed time, captured warnings). On permission failure the response includes the “ask user to re-approve Windows binary” guidance from `agents.md`.

### Edge considerations
- Missing SDK: respond with targeted message listing detected SDKs and suggesting `roslyn_env`/`ListInstalledSdks`.
- Cancellation: ensure dotnet process is killed if the MCP token cancels (>120 s limit).

## Workflow 2 – Legacy VS MSBuild (net462)
1. Agent issues `LegacyMsBuild` specifying `.sln`, `/p:Platform=x86`, `/p:Configuration=Debug`.
2. Tool inspects solution TFMs via `SolutionTools` or `dotnet sln list` to confirm .NET Framework requirement.
3. Runner locates VS installation (e.g., `C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe`). If multiple editions exist, prefer the one configured in `agents.md` or via optional parameter.
4. Spawn `MSBuild.exe` with provided targets (`/t:Rebuild`) and same logging strategy.
5. Return success metadata plus pointer to diagnostic log on failure (e.g., binary log path).

## Workflow 3 – vstest.console for net462 Tests
1. After a legacy build, agent calls `LegacyVsTest` with `TestAssemblies` and optional `/Framework` override.
2. Tool verifies the target assemblies exist, resolves VS `vstest.console.exe`, and launches it using the same Windows environment context.
3. Parses summary lines to return pass/fail counts; attaches truncated stdout plus a pointer to the `.trx` file for deep dive.
4. Friendly errors for missing assemblies, platform mismatches, or permission denials reference the troubleshooting section in `help.md`.

## Workflow 4 – Runner Discovery
1. Agent calls `ListBuildRunners` (planned) to see available dotnet SDKs and VS toolsets.
2. Service reuses `roslyn_env` probing plus Visual Studio setup APIs to enumerate versions, install paths, and supported frameworks.
3. Response includes flags like `supportsNetFramework`, `supportsArm64`, `default=true`, enabling agents to script fallback logic before invoking builds/tests.

## Dependencies & Links
- Relies on existing `roslyn_env` tool for baseline environment telemetry.
- Reuses `SecurityValidator` for path translation and permission checks.
- Integrates with `DiagnosticLogger` for operation tracing.

## Open Questions
- Should `BuildSolution` always emit a `.binlog`, or only when a `collectBinlog` flag is set?
- Do we need per-tool rate limits to avoid saturating Windows resources when multiple agents run builds simultaneously?
