# Roslyn Code Navigator – Test Plan

## Goals
- Exercise the Roslyn-backed services end-to-end (SymbolSearchService, CodeAnalysisService, SecurityValidator, CodeNavigationTools).
- Lock down recent behaviors: namespace-aware symbol matching, path auto-normalization, SDK/target framework validation.
- Provide a regression harness that can be run locally (`dotnet test`) or during CI without depending on external solutions.

## Test Harness Overview

| Area | Approach | Notes |
| --- | --- | --- |
| Unit tests | New project `RoslynMcpServer.Tests` (xUnit + FluentAssertions) targeting `net8.0`. | References `RoslynMcpServer` project directly. |
| Test assets | Add `TestAssets/SampleSolution` containing 3 projects: `Sample.App` (net8.0, C#), `Sample.Legacy` (net48, C#), and `Sample.VbLib` (net8.0, VB). | Includes namespaces `MyCompany.Services.*` to drive wildcard tests and cross-language coverage. |
| Integration smoke | Optional CLI-level tests (future) invoking the MCP server via `dotnet run` + JSON-RPC harness. | Out of scope for first pass; document for later. |

## Fixtures
- `Sample.App`: Contains classes (`SerilogWidget`, `TextProcessor`, `VbBridge`), methods (`Process`, `ProcessAsync`), and nested namespaces for wildcard coverage. Uses `Serilog` reference to mimic customer repro.
- `Sample.Legacy`: Targets .NET Framework 4.8 to verify SecurityValidator detects reference assemblies (these should exist on build agents) and ensures MSBuild can load multi-target solutions.
- `Sample.VbLib`: VB.NET library with `LegacyCalculator` referenced by `VbBridge`, ensuring mixed-language solutions work.
- Synthetic solution file intentionally includes all projects so dependency analysis can report cross-project references (including C# ↔ VB).

## Test Matrix

### SymbolSearchService.SearchSymbolsAsync

| Scenario | Fixture | Assertions |
| --- | --- | --- |
| Basic wildcard | pattern `Serilog*`, `symbolTypes=class` | Returns `SerilogWidget` with correct `FullName`, `ProjectName`, `Namespace`. |
| Namespace-qualified wildcard | pattern `MyCompany.Services.Text*.*` | Every member in `MyCompany.Services.TextProcessing` is returned; no other namespaces included. |
| Case-insensitive | Mixed-case pattern vs actual symbol names. | Results present even when pattern casing differs. |
| Type filters | `symbolTypes="method"` | Only methods included; verify properties/fields excluded. |
| Error handling | Non-existent solution path | Throws / returns safe error (verified through tool wrapper tests). |

### SymbolSearchService.FindReferencesAsync

| Scenario | Description | Assertions |
| --- | --- | --- |
| Short name lookup | `symbolName="SerilogWidget"` | All references & definition returned; duplicates removed per document/line. |
| Fully qualified wildcard | `symbolName="MyCompany.Services.Text*.Process*"` | Only methods under targeted namespace matched. |
| Include/Exclude definitions | `includeDefinition=false` | Definition lines omitted. |

### SymbolSearchService.GetSymbolInfoAsync

| Scenario | Assertions |
| --- | --- |
| Method info | Parameters, return type, namespace, accessibility populated. |
| Type info | `Kind=Class`, attributes list, source location set. |
| Unknown symbol | Returns `null`. |

### CodeAnalysisService.AnalyzeDependenciesAsync

| Scenario | Assertions |
| --- | --- |
| Max depth enforcement | With `maxDepth=1`, child dependencies truncated. |
| Namespace usage counts | Known namespace counts match doc structure. |
| Symbol counts | Total/Public/Internal counts align with fixture. |

### CodeAnalysisService.AnalyzeCodeComplexityAsync

| Scenario | Assertions |
| --- | --- |
| Threshold filtering | Only methods with complexity ≥ threshold reported. |
| Context info | Each result includes method name, file, class, namespace. |

### SecurityValidator

| Scenario | Assertions |
| --- | --- |
| Path normalization Windows→WSL | Input `E:\Repo\Sol.sln` when `_isWslHost=true` transforms to `/mnt/e/Repo/Sol.sln`. |
| Path normalization WSL→Windows | Input `/mnt/e/Repo/Sol.sln` on Windows converts to `E:\Repo\Sol.sln`. |
| Qualified path style mismatch | Friendly error returned when translation impossible. |
| SDK detection | When sample solution targets net8.0, missing SDK list is empty; when environment is stubbed to only show net8.0, message contains required majors. |
| .NET Framework targeting pack | On Windows w/out reference assemblies (simulate via temp dir), validator reports missing pack; on Linux, fails fast instructing to run on Windows. |

### CodeNavigationTools (Tool Wrappers)

These tests should mock `IServiceProvider` and verify:
- `SearchSymbols` returns formatted payload with header lines and truncated lists.
- Timeout handling: Cancel token triggers graceful message.
- Error path when services aren’t registered (should return user-friendly error).

### Build/Test Runner Tools (`BuildSolution`, `TestSolution`, `LegacyMsBuild`, `LegacyVsTest`)

| Scenario | Assertions |
| --- | --- |
| Dotnet SDK build | Invoking `BuildSolution` on `SampleSolution.sln` from WSL launches Windows `dotnet.exe` with translated path, applies `MSBUILDDISABLENODEREUSE=1`, and returns success exit code/log stream. |
| SDK selection override | Supplying `sdkVersion=9.0.xxx` pins `DOTNET_ROOT`/probing directories to that SDK even if 10.0 is installed; error message clearly states when requested version is missing. |
| Legacy MSBuild fallback | For a synthetic `.sln` targeting `net462`, tool switches to `MSBuild.exe` under VS 2022 when `PreferredRunner=LegacyMsBuild`; verifies x86 platform props propagate. |
| VSTest runner | `LegacyVsTest` launches `vstest.console.exe` for a .NET Framework test DLL and surfaces friendly guidance when the executable path lacks permission or the test binary is missing. |
| Structured logging | Each tool wraps stdout/stderr with timestamps and truncates large logs while surfacing the file path to the full log artifact. |
| Error messaging | Permission-denied or SDK-missing failures produce the same actionable text described in `agents.md` and `help.md`. |

## Implementation Notes

- **Test Assets Loading**: Place sample solution under `TestAssets`. Tests copy the solution to a temp directory to avoid path conflicts and ensure SecurityValidator sees absolute paths.
- **MSBuild Locator**: For unit tests, call `MSBuildLocator.RegisterInstance` with the test host’s SDK before running fixtures (xUnit collection fixture).
- **Cancellation Tokens**: For long-running tests, set generous timeouts but ensure `CancellationTokenSource` usage is exercised in at least one test (e.g., `SearchSymbolsAsync` canceled mid-way via small throttling limit).
- **Snapshot helpers**: Provide golden-file helpers (e.g., `ExpectedDependencies.json`) so dependency analysis output comparisons stay readable.

## Next Steps
1. Scaffold `RoslynMcpServer.Tests` with shared `TestSolutionFixture`.
2. Check in `TestAssets/SampleSolution` (documented README inside describing layout).
3. Implement priority tests (SearchSymbols wildcard, FindReferences qualified names, SecurityValidator normalization).
4. Expand coverage to dependency/complexity analysis.
