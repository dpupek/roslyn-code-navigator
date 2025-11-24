# Agents Notes

## Lessons Learned
- Long-running MSBuild loads cause Codex tool timeouts unless cancellation tokens are plumbed end-to-end. Every Roslyn call (`OpenSolutionAsync`, `GetCompilationAsync`, syntax APIs) must honor the request token so agents can fail fast when the CLI cancels a tool after 120 s.
- Faulted solution loads poison the cache indefinitely. Cache entries must be evicted when `LoadSolutionAsync` throws to avoid permanent failures for a given `.sln`.
- NuGet fallback folders missing on WSL/Windows are a silent failure mode. Validate the configured fallback directories on startup (before spinning up the MCP server) and exit with a clear log if they are absent.
- MSBuild nodes left running across sessions lock `RoslynMcpServer.exe`; the server now forces `MSBUILDDISABLENODEREUSE=1` so each build shuts down cleanly. Only override this if you are the sole agent on the machine.
- Unbounded parallel project fan-out can overwhelm memory/CPU and starve Roslyn, resulting in agent-visible timeouts. Bounded concurrency is essential when a workspace has dozens of compilable projects.
- Codex CLI does **not** expose `mcp start/stop`. When you need to restart the server (e.g., after a rebuild), stop the running `RoslynMcpServer.exe` process yourself (Task Manager or `Stop-Process`) so builds don’t fail with MSB3026/MSB3021.
- Prompt quality matters: explicitly mention “use the `roslyn_code_navigator` MCP server” (see README prompt recipes) to prevent Codex from guessing or browsing.
- Mixed-language scenarios (C# + VB) are now supported and tested; include VB assets/tests whenever reproducing issues to keep parity coverage.
- MCP server builds run on Windows .NET SDK/MSBuild. When the repo lives under `/mnt/*` in WSL, ensure `$NEXPORT_WINDOTNET` points to `"/mnt/c/Program Files/dotnet/"` and use `"$NEXPORT_WINDOTNET/dotnet.exe"` for `build/test` commands. If you see `Permission denied` launching the Windows binaries, the current Codex session hasn’t granted elevated access—ask the user to update approvals so Windows executables can run.
- Solution reloads now expose `roslyn_env` + `list_projects`, so always capture those outputs after restarting the MCP server; they shortcut many “it doesn’t load” triages.
- Embedding MSBuild 10 inside a net8 host caused missing `System.Runtime` types. Upgrading the MCP server target framework to net10 and hooking `AssemblyLoadContext.Resolving` fixed the issue; if you see similar errors, check the runtime first before blaming MSBuild.
- The .NET 10 SDK ships `NuGet.Frameworks 7.0` in its MSBuild tasks; when tests load projects via MSBuild, the host process must reference the same NuGet.Frameworks version. If you see “manifest definition does not match” errors, update our packages to match the SDK or let MSBuild run out of process (i.e., don’t shadow its dependencies with older versions).

## Established Patterns
- **Cancellation-first APIs**: All service/tool entry points accept a `CancellationToken` and pass it to Roslyn, linked CTS, and any custom loops. Throw on token cancellation inside long loops (project enumeration, namespace scans, symbol recursion).
- **Safe caching**: Solution caching now uses per-solution semaphores and evicts entries on failure, ensuring retries start from a clean state.
- **Startup validation**: `Program.ConfigureEnvironment` ensures `NUGET_PACKAGES` and fallback folders are set and exist before building the host. Missing prerequisites cause an immediate shutdown with actionable error logs.
- **Bounded parallelism**: Symbol search and other project-wide analyses use a shared `SemaphoreSlim` (configurable via `ROSLYN_MAX_PROJECT_CONCURRENCY`) to cap concurrent compilations, preserving responsiveness while still leveraging parallel work.
- **Structured logging**: Diagnostic/console loggers are configured to emit only to stderr with consistent log levels (driven by `ROSLYN_LOG_LEVEL`/`LOG_LEVEL`), so agents can inspect progress without polluting stdout responses.
- **PowerShell-first setup**: All documented setup/publish snippets assume Windows PowerShell. When guiding users, keep commands in that shell and remind them to publish with `-o` so Codex can target a stable exe path.
- **Test harness**: Extend `RoslynMcpServer.Tests` + `TestAssets/SampleSolution` (now includes C# + VB projects) for new regressions instead of crafting ad-hoc samples.
- **ShowHelp tool**: Encourage agents to run the `ShowHelp` MCP tool during sessions to refresh recipes/capabilities quickly.


## Decomposition & Shaping

* Maintain a living plan (checkboxes) under docs for each Epic.
* Generate a baseline spec of current behavior before changing code.
* Group changes by iteration; complete and validate each iteration before the next.
* Record questions and decisions alongside the plan.
* Always decompose user stories into workflows, CRCs, and tasks (use the release roadmap).
* Entropy: Watch for code entropy—prefer refactoring existing services, keep CRCs minimal, follow YAGNI/DTSTTCPW.
* Ask Questions: Gather clarification when the solution is becoming overly complex.
* Design: When writing plans/specs, avoid ambiguous language; write from the user/developer perspective.
* **Epic blueprint**: When starting or continuing a major epic, ask the user if they want you to follow the shaping process below. If they agree, create/update artifacts under `docs/<short-name>-<case-number>/`:
  1. **Epic definition** – summarize the big idea and success criteria (FogBugz epic case).
  2. **Workflow spec** – `workflows.md` with personas, motivations, flow steps, and linked cases.
  3. **Baseline & edge cases** – capture current state (link to existing specs) and record risks in `edge-cases.md`. Iterate workflows ↔ baseline ↔ edge cases.
  4. **CRCs** – `crcs.md` listing classes/services/controllers (with namespaces) and their responsibilities/collaborators referenced to workflows.
  5. **Stakeholder summary** – `stakeholder-summary.md` explaining value and usage for admins/learners/external stakeholders.
  6. **Roadmap** – `roadmap.md` with phased checklists (data models, services, UI, permissions, dependencies).
  7. **Commit** – check in the docs with a commit referencing the epic case.
* Baseline/current-state docs should either link to existing specs or live alongside the new workflows.
* Edge cases should feed back into workflows and CRCs early; don’t leave them for QA to discover.
