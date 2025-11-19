# Build/Test Runner Tools – CRCs

> _Placeholder case number – update folder name when FogBugz epic ID is assigned._

## Classes / Components

### BuildExecutionService
- **Responsibilities**: Orchestrate build/test requests, normalize inputs (paths, configuration, sdkVersion), enforce cancellation tokens, and emit structured results back to MCP tools.
- **Collaborators**: `RunnerSelector`, `SecurityValidator`, `ProcessLogStreamer`, `DiagnosticLogger`.

### RunnerSelector
- **Responsibilities**: Inspect solution metadata + requested overrides to choose between `DotnetSdkRunner`, `VsMsbuildRunner`, or `VsTestRunner`. Validates the requested SDK version is installed (using `roslyn_env`/`ListInstalledSdks`).
- **Collaborators**: `RoslynEnvironmentProbe`, `SolutionTools`, `BuildExecutionService`.

### DotnetSdkRunner
- **Responsibilities**: Launch Windows `dotnet.exe` with translated paths, set `DOTNET_ROOT` and `MSBUILDDISABLENODEREUSE=1`, and pass through CLI arguments (`build`, `test`, `restore`, etc.).
- **Collaborators**: `WindowsProcessLauncher`, `ProcessLogStreamer`.

### VsMsbuildRunner
- **Responsibilities**: Discover Visual Studio installation (via vswhere or configured path), invoke `MSBuild.exe` with legacy switches, and ensure x86/x64 platform arguments propagate.
- **Collaborators**: `WindowsProcessLauncher`, `RunnerSelector`, `DiagnosticLogger`.

### VsTestRunner
- **Responsibilities**: Run `vstest.console.exe` for .NET Framework test assemblies, collect TRX/log files, and translate exit codes to MCP responses.
- **Collaborators**: `WindowsProcessLauncher`, `BuildExecutionService`.

### WindowsProcessLauncher
- **Responsibilities**: Shared utility to spawn Windows binaries from WSL, handle permission-denied retries, and kill processes on cancellation.
- **Collaborators**: All runner implementations, `SecurityValidator`.

### RoslynEnvironmentProbe (existing `roslyn_env` tool backend)
- **Responsibilities**: Enumerate installed dotnet SDKs, runtimes, Visual Studio instances, and publish environment metadata consumed by build/test tools.
- **Collaborators**: `RunnerSelector`, `HelpDocs` (for user guidance).

### ProcessLogStreamer
- **Responsibilities**: Stream stdout/stderr with timestamps, redact secrets, and cap log volume. Provides links to full log files when truncation occurs.
- **Collaborators**: Runner implementations, `DiagnosticLogger`.

### HelpDocs / Recipes (documentation module)
- **Responsibilities**: Surface usage examples (`BuildSolution`, `LegacyMsBuild`, etc.), troubleshooting steps (approvals, missing SDKs), and tie into `agents.md`.
- **Collaborators**: `BuildExecutionService` (for error messaging), developer content pipeline.
