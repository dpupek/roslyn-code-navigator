# Agents Notes

## Lessons Learned
- Long-running MSBuild loads cause Codex tool timeouts unless cancellation tokens are plumbed end-to-end. Every Roslyn call (`OpenSolutionAsync`, `GetCompilationAsync`, syntax APIs) must honor the request token so agents can fail fast when the CLI cancels a tool after 120 s.
- Faulted solution loads poison the cache indefinitely. Cache entries must be evicted when `LoadSolutionAsync` throws to avoid permanent failures for a given `.sln`.
- NuGet fallback folders missing on WSL/Windows are a silent failure mode. Validate the configured fallback directories on startup (before spinning up the MCP server) and exit with a clear log if they are absent.
- Unbounded parallel project fan-out can overwhelm memory/CPU and starve Roslyn, resulting in agent-visible timeouts. Bounded concurrency is essential when a workspace has dozens of compilable projects.

## Established Patterns
- **Cancellation-first APIs**: All service/tool entry points accept a `CancellationToken` and pass it to Roslyn, linked CTS, and any custom loops. Throw on token cancellation inside long loops (project enumeration, namespace scans, symbol recursion).
- **Safe caching**: Solution caching now uses per-solution semaphores and evicts entries on failure, ensuring retries start from a clean state.
- **Startup validation**: `Program.ConfigureEnvironment` ensures `NUGET_PACKAGES` and fallback folders are set and exist before building the host. Missing prerequisites cause an immediate shutdown with actionable error logs.
- **Bounded parallelism**: Symbol search and other project-wide analyses use a shared `SemaphoreSlim` (configurable via `ROSLYN_MAX_PROJECT_CONCURRENCY`) to cap concurrent compilations, preserving responsiveness while still leveraging parallel work.
- **Structured logging**: Diagnostic/console loggers are configured to emit only to stderr with consistent log levels (driven by `ROSLYN_LOG_LEVEL`/`LOG_LEVEL`), so agents can inspect progress without polluting stdout responses.


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