# Roslyn Code Navigator Help

Welcome! This server exposes C# code navigation and analysis tooling through the Model Context Protocol (MCP). Use this help resource when you need a quick reminder of the available capabilities.

## Available Tools

- **SearchSymbols** – Wildcard search across symbol names (`*Service`, `Get*User`, etc.).
- **FindReferences** – Locate every reference to a specific symbol.
- **GetSymbolInfo** – Retrieve metadata (signature, accessibility, attributes) for a symbol.
- **AnalyzeDependencies** - Summarize project/package relationships and namespace usage.
- **AnalyzeCodeComplexity** - Report methods whose cyclomatic complexity exceeds a threshold.
- **FindImplementations** - List classes implementing an interface or deriving from a base type.
- **ShowHelp** - Emit this help document directly via an MCP tool call.
- **ListProjects** - Display every project in a solution along with its target frameworks.
- **RoslynEnv** - Display the server’s runtime/MSBuild environment plus dotnet/Visual Studio inventories for troubleshooting build/load issues.
- **ListBuildRunners** - Return the structured list of dotnet SDKs, runtimes, Visual Studio instances, and shared runtime probe paths detected at startup.
- **BuildSolution** - Run `dotnet build` using the Windows SDK/targeting packs detected by the server.
- **TestSolution** - Run `dotnet test` with optional TRX logging via the Windows SDK.
- **LegacyMsBuild** - Invoke Visual Studio’s `MSBuild.exe` for .NET Framework solutions/projects (x86/x64 aware).
- **LegacyVsTest** - Invoke `vstest.console.exe` for .NET Framework test assemblies using Visual Studio Test tools.
- **StartAspNet** - Run `dotnet run` for an ASP.NET project with launch-profile selection, returning status + token, PID, URLs, log file (optional), and recent output tail (friendly errors for port-in-use, bad profiles, invalid paths). Child runs are placed in a Windows Job Object so they terminate if the MCP server exits.
- **StopAspNet** - Stop a running ASP.NET session using the token from `StartAspNet` (returns structured status, handles stale tokens gracefully; configurable timeout).
- **ListLaunchProfiles** - Read `Properties/launchSettings.json` and return available launch profile names/URLs plus a message when the file is missing or malformed.
- **ListAspNetSessions** - Show active ASP.NET run sessions (tokens, PIDs, URLs, recent output tail).
- **ListAspNetRecentSessions** - Show the most recent exited sessions (tokens, exit code, tail).
- **GetAspNetOutput** - Retrieve recent stdout/stderr/combined tails for a running ASP.NET session (pollable; not full streaming/logging).
- Mixed-language solutions containing both C# and VB.NET projects are supported; VB symbols and references appear alongside C# ones.

## Usage Tips

1. Always provide an absolute path to the `.sln` you want to inspect.
2. You can narrow symbol searches by comma-separated kinds (e.g., `class,method`).
3. Dependency analysis may take longer on large solutions-expect a few seconds.
4. Complexity analysis defaults to a threshold of 5; raise it if you only want the worst offenders.
5. `FindReferences` and `GetSymbolInfo` accept fully qualified names (with `.` separators) and wildcards, so patterns like `My.Namespace.Text*.*` can target groups of types/members; omit the namespace to stick with the legacy exact-name lookup.
6. When something fails, check the MCP server logs (stderr) for detailed diagnostics.
7. If the MCP server is running from WSL, pass the `/mnt/<drive>/...` form of the solution path; Windows-style `E:\...` paths will be rejected. Conversely, when running on Windows use the drive-qualified form.
8. To keep multiple agents from fighting over MSBuild worker processes, the server sets `MSBUILDDISABLENODEREUSE=1` by default—override it only if you understand the locking consequences.

## Recipes

### Rename a Method (or Type) Safely
1. Use **SearchSymbols** with `symbolTypes="method"` (or `class`, etc.) to locate the declaration and confirm the namespace/assembly.
2. Call **FindReferences** with `symbolName` set to the method name (or qualified wildcard such as `My.Namespace.Service*.Process*`) to enumerate every usage and the definition site.
3. Rename the symbol in its source file, then update each reference reported in step 2. Because `FindReferences` returns exact file/line info, you can work through the list methodically.
4. Re-run **FindReferences** with the *old* name; the result should come back empty. Optionally run it again with the new name to verify the symbol is discoverable in its new form.
5. Finish by rebuilding or running targeted tests to confirm behavior.

### Map the Workspace Quickly
1. Call **SearchSymbols** with a broad pattern (e.g., `*Controller`, `symbolTypes=class`) to see what modules exist.
2. Follow up with **AnalyzeDependencies** (`maxDepth=2`) on the same solution to learn which projects depend on each other.
3. Capture both outputs for your session notes before diving into a feature.

### Track Interface Implementations
1. Run **FindImplementations** on the interface or base class name (wildcards supported) to list implementations/derived types.
2. If you need the exact call sites, follow up with **FindReferences**.
3. Use **SearchSymbols** to narrow to specific namespaces or assemblies before invoking **FindImplementations** when you only want a subset.

### Pre-refactor Impact Check
1. Use **FindReferences** on the symbol you intend to rename/move.
2. Paste the reference list into your PR plan or notebook.
3. After the change, re-run **FindReferences** (old name) to ensure nothing references the stale identifier; run it again with the new name to confirm it’s discoverable.

### Dependency Impact Scan
1. Before touching a shared project, run **AnalyzeDependencies** on the solution with a low depth (1–2).
2. Look for projects/packages that list the target project as a dependency; note them in your plan.
3. If any dependencies look unexpected, inspect their project files directly (the **ListProjects** tool can help confirm target frameworks quickly).

### Cross-language Sanity Check
1. When a solution mixes C# and VB, run **SearchSymbols** twice: once for a C# type, once for a VB type (e.g., `LegacyCalculator`).
2. Use **FindReferences** on the VB type to confirm cross-language call sites appear.
3. If anything fails to load, check `roslyn_env` first (ensure DOTNET_ROOT/MSBuild path are correct) and use **ListProjects** to confirm the solution actually loaded the VB project.

### Environment & Runner Discovery
1. Run **RoslynEnv** after launching the server to capture OS, runtime, and the full toolchain inventory (dotnet SDK paths, runtime probe paths, Visual Studio MSBuild locations). Save the output in your session notes when troubleshooting build/test issues.
2. Use **ListBuildRunners** when you need structured data (e.g., scripting): it returns arrays of SDKs, runtimes, VS instances, and probe paths that downstream tools can parse.
3. Before invoking builds/tests, compare the solution’s target frameworks with the detected SDKs. If a required SDK is missing, surface the friendly guidance from `agents.md` instead of attempting a build that will fail halfway.
4. When permissions suddenly block Windows binaries, re-run **RoslynEnv** to confirm the MSBuild path and remind the operator to re-approve the Windows toolchain, then retry.

### Build from WSL Using dotnet
1. Call **ListBuildRunners** to confirm the desired dotnet SDK version is installed (e.g., 10.0.100). If you need a specific SDK, pass `sdkVersion="10.0.100"` to **BuildSolution**/**TestSolution**.
2. Run **BuildSolution** with your `/mnt/.../Solution.sln` path, `configuration=Debug` (or Release), and any extra `dotnet` switches via `additionalArguments`.
3. If you only need tests, call **TestSolution** instead; set `collectTrx=true` to generate TRX logs automatically.
4. Review the summarized stdout/stderr. For longer logs, re-run the tool with fewer verbosity switches or inspect the Windows logs directly.

### Start/Stop an ASP.NET Host (with launch profiles)
1. Call **ListLaunchProfiles** with your `/mnt/.../MyApp.csproj` to see available profiles and their `applicationUrl` values.
2. Start the app with **StartAspNet**: set `launchProfile` to one from step 1 (optional; defaults to the first) and leave `noBuild=true` (default) for quicker startup. You can override URLs with `urls="http://localhost:5055;https://localhost:7055"`.
3. The response includes `Succeeded/Message`, `token`, `processId`, `urls`, `runnerDescription`, optional `logFilePath` (when `logToFile=true`), and a `recentOutput` tail. Copy the token.
4. To stop the host, call **StopAspNet(token, timeoutSeconds=60)**. If you forget the token, use **ListAspNetSessions** to see active sessions (or **ListAspNetRecentSessions** for recently exited ones).
5. To check live logs, call **GetAspNetOutput(token, maxLines=200)**; it returns stdout, stderr, and combined tails plus a truncation flag. If you need more than the in-memory tail, enable `logToFile=true` on **StartAspNet** or add file logging inside the ASP.NET app and read those logs. Runs are scoped by project path: on MCP server restart, any orphaned runs for that project are auto-killed; child processes are in a Windows Job Object so they terminate if the MCP server dies.

### Run UI smoke tests against the running host (Playwright example)
Prereqs: Node + Playwright CLI installed locally; app already running via **StartAspNet** and exposing a URL (e.g., https://localhost:7055).
1. Export the URL: `export APP_URL=https://localhost:7055` (or use the HTTP port).
2. Run Playwright tests pointing at that URL, e.g. `npx playwright test --grep @smoke --project=chromium --reporter=list --env APP_URL=$APP_URL`.
3. Need a quick one-off check? Use `npx playwright codegen $APP_URL` to record a script, save it under `tests/ui/` and add an `@smoke` tag.
4. When finished, stop the host via **StopAspNet(token)** to avoid locked binaries during builds/tests.

### Drive UI checks via the Playwright MCP server
If you’re using the Playwright MCP server (for example the reference at <https://github.com/playwright-community/playwright-mcp-server>):
1. Start the ASP.NET host with **StartAspNet** and note the `urls` entry (e.g., https://localhost:7055).
2. In your MCP client, load the Playwright MCP server and set its base URL to the running app (many clients expose this as a `baseUrl` parameter or environment variable—use the same port from step 1).
3. Invoke the MCP tool that runs Playwright scripts (commonly `runPlaywright` or `runTests`) and pass the script path plus any env like `APP_URL=https://localhost:7055`.
4. Use **ListAspNetSessions** to confirm the host is still running while tests execute; stop it with **StopAspNet(token)** afterward to release the binary lock.

### RunOperationResult / output shapes
- `RunOperationResult`: `Succeeded`, `Message`, optional `ExitCode`, optional `Output` (combined), `Suggestions` (array of hints).
- `GetAspNetOutput`: returns `RunOutputSnapshot` with `StdOut`, `StdErr`, `Combined`, `Truncated` flag.
- Tail length defaults to 50 lines; override with env `ROSLYN_ASPNET_TAIL_LINES`. `maxLines` argument on **GetAspNetOutput** lets callers down-sample further.

### Legacy Visual Studio Build/Test
1. Use **LegacyMsBuild** for solutions that rely on the full Visual Studio toolset (e.g., net462). Provide `/p` properties via the `properties` parameter (e.g., `Configuration=Debug;Platform=x86`).
2. If multiple VS installations exist, set `preferredInstance="VisualStudio.17.Release"` (see **ListBuildRunners** output) to pin to a specific build.
3. After building, call **LegacyVsTest** with the produced test assembly paths. Set `framework=".NETFramework,Version=v4.6.2"` or `platform="x86"` to mirror the original vstest commands.
4. Failures will include truncated stdout/stderr plus the runner used; consult those logs and rerun after applying fixes.

### Hotspot / Complexity Sweep
1. Run **AnalyzeCodeComplexity** with a moderate threshold (e.g., 7).
2. Sort the output by complexity and cross-reference with `git blame` to see which high-complexity methods churn frequently.
3. Use the list to propose refactors or add guardrails; re-run after refactoring to confirm the score drops.

## Safety & Validation

- Solution paths are validated to avoid directory traversal and must exist on disk.
- Path style is auto-corrected when possible (e.g., `/mnt/c/...` to `C:\...` or vice versa); if conversion is impossible, the server returns a friendly hint about which style to use.
- Before loading a solution the server checks the target frameworks and ensures the matching .NET SDK / targeting packs are installed, returning actionable errors when they are missing.
- Search patterns are sanitized to alphanumerics plus `*` and `?`.
- File analysis results are cached so repeated requests are faster.

## Need More?

Open this resource again anytime with a `read_resource` call for `roslyn-help`. Feel free to extend this file with project-specific guidance.
