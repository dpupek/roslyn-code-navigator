# MCP Hardening To-Do

## 1. Retry or Evict Faulted Solution Loads
- **File:** `RoslynMcpServer/Services/CodeAnalysisService.cs:31-54`
- **Issue:** `_solutionCache` holds a `Lazy<Task<Solution>>`. When `LoadSolutionAsync` fails (NuGet fallback, workspace errors, etc.) the faulted task stays cached forever so every future call to the same solution path instantly repeats the failure.
- **Action:** remove the cache entry when the load throws, or wrap the lazy in a retry/eviction policy so a subsequent request can attempt a clean `OpenSolutionAsync`.

## 2. Fail Fast on Workspace Load Errors
- **File:** `RoslynMcpServer/Services/CodeAnalysisService.cs:42-53`
- **Issue:** `MSBuildWorkspace.OpenSolutionAsync` is awaited without cancellation or timeout. When MSBuild gets stuck resolving packages the tool call sits until Codex times it out (120s) even though we already logged the failure.
- **Action:** plumb a `CancellationToken` from the MCP request (or a hard timeout) into `OpenSolutionAsync`, and surface fatal `workspace.WorkspaceFailed` diagnostics as immediate exceptions so the tool fails quickly instead of waiting for the entire load to finish.

## 3. Validate NuGet Fallbacks on Startup
- **File:** `RoslynMcpServer/Program.cs:98-117`
- **Issue:** `ConfigureEnvironment` only sets fallback folders when running inside WSL. On Windows we depend on the shell environment and don’t check whether the referenced fallback path actually exists before we start servicing requests.
- **Action:** at startup, verify that the fallback path(s) exist (or allow overriding them via env). If a required folder is missing, log an error and exit rather than letting MSBuild fail minutes later.

## 4. Throttle Per-Project Compilation Fan-Out
- **File:** `RoslynMcpServer/Services/SymbolSearchService.cs:26-77`
- **Issue:** `SearchSymbolsAsync` launches one `GetCompilationAsync()` per project and `Task.WhenAll` waits for them all concurrently. Large solutions can spawn dozens of compilations at once, consuming memory/CPU until the MCP request times out.
- **Action:** introduce bounded parallelism (e.g., `Parallel.ForEachAsync` or a semaphore) so only a handful of compilations run simultaneously, or reuse the existing batch logic from `IncrementalAnalyzer`.

## 5. Surface Project-Level Failures Upstream
- **File:** `RoslynMcpServer/Services/SymbolSearchService.cs:71-77`
- **Issue:** when a project search throws we log a warning but still return partial results. To the MCP client it looks like success until the overall call times out.
- **Action:** aggregate project failures (or fail immediately on the first fatal error) and propagate them back to the tool caller so Codex receives a clear error instead of a silent timeout.
