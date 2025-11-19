# Build/Test Runner Tools – Edge Cases

> _Placeholder case number; rename directory once the FogBugz epic ID is assigned._

## Environment & SDK Detection
- Requested `sdkVersion` is not installed or only available as a preview build. Tool must fall back with a friendly message that lists installed SDKs (from `roslyn_env`) and suggests re-running with an available version.
- Solution resides on `/mnt/*` path that maps to a Windows drive lacking permissions or mounted read-only; detect before spawning the process and explain how to remap or relocate.
- Windows binaries blocked because CLI approvals expired mid-session. Return the standardized “permissions denied—ask user to re-approve Windows binaries” guidance without hanging.
- Multiple Visual Studio installations detected; ensure deterministic selection (prefer configured path) and surface which one was chosen.
- VS installed in a non-default path (e.g., `D:\VS2022`); runner must honor overrides and fail fast if `MSBuild.exe`/`vstest.console.exe` cannot be located.

## Build Execution
- Mixed solution containing both SDK-style (net10) and legacy (net462) projects. Decide whether to split invocations (dotnet vs MSBuild) or emit a clear message when a single runner cannot satisfy all TFMs.
- `/p:Platform=x86` specified but project only supports AnyCPU; propagate MSBuild’s diagnostic rather than masking it.
- User provides unsupported CLI switches. Validate upfront and either pass-through safely or respond with guidance on accepted parameters.
- `MSBUILDDISABLENODEREUSE` overridden in the environment by the caller; log the final env snapshot and warn when node reuse is re-enabled.
- Cancellation token triggered (CLI timeout) while dotnet/MSBuild is running. Ensure the child process is terminated and the MCP response reflects the cancellation status.

## Logging & Artifacts
- Builds/tests produce large stdout/stderr streams that exceed MCP payload limits. Implement log truncation with links to persisted files and warn agents when trimming occurs.
- Binary log (`.binlog`) growth when `collectBinlog=true`; enforce rotation/size limits and document where artifacts are stored.
- `LegacyVsTest` run fails before generating `.trx`; tool should mention that no artifact was produced instead of pointing to a missing file.

## Test Execution
- Legacy test assembly path does not exist (e.g., wrong configuration folder). Provide specific guidance (“build Debug|x86 first”) instead of generic file-not-found errors.
- Target framework mismatch between vstest runner and compiled tests (e.g., requesting `.NETFramework,Version=v4.6.2` but assembly built for v4.8). Detect via assembly metadata and emit clearer messaging.

## Concurrency & Resource Usage
- Multiple agents invoke builds simultaneously, exhausting Windows CPU/RAM. Consider queuing or at least logging concurrency level so operators can throttle.
- Dotnet/VS runners left running after completion (zombie processes). Ensure watchdog cleanup and document mitigation steps.

## Platform Support
- Tool invoked on a machine lacking the Windows SDK path (`NEXPORT_WINDOTNET` unset). Respond with the setup checklist rather than attempting a Linux build that will fail on net10 targets.
- Attempting to run VS runners on ARM64 hosts without matching toolchains; detect unsupported architecture and guide agents to appropriate build agents.
