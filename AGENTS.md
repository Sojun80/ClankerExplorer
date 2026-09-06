# Agent Instructions & Project Notes for ClankerExplorer

## Build Output & Execution Location

> **CRITICAL EXPECTATION**: The user expects the build executable to land and run from:
> ```text
> C:\ClankerExplorer\bin\Debug\net8.0\ClankerExplorer.exe
> ```

### Background & Architecture
- `ClankerExplorer.csproj` builds to the target framework architecture directory:
  ```text
  C:\ClankerExplorer\bin\Debug\net8.0-windows10.0.19041.0\win-x64\
  ```
- The post-build target `SyncNet8LegacyOutput` automatically synchronizes/copies all build artifacts to:
  ```text
  C:\ClankerExplorer\bin\Debug\net8.0\
  ```

### Common Gotcha & Prevention Rule
- **File Locking on Build**: If `ClankerExplorer.exe` is running while `dotnet build` executes, Windows file locks will prevent overwriting:
  ```text
  bin\Debug\net8.0\ClankerExplorer.exe
  bin\Debug\net8.0\ClankerExplorer.dll
  ```
- Because `SyncNet8LegacyOutput` has `ContinueOnError="true"`, the build will succeed with a warning (`warning MSB3021`), but `bin\Debug\net8.0\` will silently retain stale binaries from previous versions.
- **Rule for Agents**:
  1. Always ensure any running `ClankerExplorer.exe` process is terminated before building.
  2. Inspect build output for `MSB3021: Unable to copy file`.
  3. Verify that `bin\Debug\net8.0\ClankerExplorer.dll` FileVersion matches the expected version before handing off to the user.
