## ASP.NET run tools

- `StartAspNet(projectPath, launchProfile?, configuration=Debug, framework?, urls?, noBuild=true)`
  - Starts `dotnet run` using the selected launch profile (default: first profile) and returns a token, PID, runner description, resolved URLs, and recent output tail.
  - `urls` overrides `applicationUrl`/`ASPNETCORE_URLS`.
  - `noBuild` defaults to true (adds `--no-build`).

- `StopAspNet(token)`
  - Stops the running process tree for the provided token.

- `ListLaunchProfiles(projectPath)`
  - Reads `Properties/launchSettings.json` and returns profile names, commandName, and applicationUrl values.

- `ListAspNetSessions()`
  - Lists active sessions with tokens, PIDs, launch profiles, URLs, and recent output tail.
