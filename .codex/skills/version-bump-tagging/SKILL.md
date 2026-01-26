---
name: version-bump-tagging
description: Version bump and tagging workflow for RoslynMCP releases. Use when asked to bump the tool/package version, tag a release, or prepare a release after completing an issue or reaching an MVP milestone.
---

# Version Bump + Tagging (RoslynMCP)

## Decision rules (recommendation)
- Recommend a version bump when an issue is completed or an MVP milestone is reached.
- Use semantic versioning guidance:
  - Patch: fixes/maintenance with no new features.
  - Minor: new features or tool additions without breaking changes.
  - Major: breaking changes or incompatible behavior.
- If ambiguous, ask the user to confirm the bump level before editing files.

## Files to update
- `RoslynMcpServer/RoslynMcpServer.csproj`: update `<Version>`.
- `README.md` and `agents.md`: update any version references or release instructions.
- If the repo uses release notes elsewhere, update them if referenced by the user.

Tip: search for version strings before editing:
- `rg -n "Version>|v\d+\.\d+\.\d+|Dpupek.Code.Nav.Mcp" README.md agents.md RoslynMcpServer/RoslynMcpServer.csproj`

## Tagging scheme
- Tag format: `vX.Y.Z` (must match `<Version>` in the csproj).
- Push tag to trigger the NuGet publish workflow.

## Git commands (WSL + /mnt)
- Use Windows Git for all git operations:
  - `"/mnt/c/Program Files/Git/bin/git.exe" status -sb`
  - `"/mnt/c/Program Files/Git/bin/git.exe" add <files>`
  - `"/mnt/c/Program Files/Git/bin/git.exe" commit -m "Bump version to X.Y.Z"`
  - `"/mnt/c/Program Files/Git/bin/git.exe" tag vX.Y.Z`
  - `"/mnt/c/Program Files/Git/bin/git.exe" push`
  - `"/mnt/c/Program Files/Git/bin/git.exe" push origin vX.Y.Z`

## Optional validation
- If tests/build are requested, use Windows dotnet via `NEXPORT_WINDOTNET` when working under `/mnt`:
  - `"${NEXPORT_WINDOTNET}dotnet.exe" build RoslynMcpServer/RoslynMcpServer.csproj -c Release`
  - `"${NEXPORT_WINDOTNET}dotnet.exe" test RoslynMCP.sln -c Release`

## Completion checklist
- Version updated in csproj.
- README/agents version references updated (if any).
- Commit created.
- Tag `vX.Y.Z` created and pushed.
