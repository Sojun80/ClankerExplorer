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
  4. **ALWAYS BUMP THE VERSION EVERY SINGLE TIME**: Never deliver changes without incrementing the version in `ClankerExplorer.csproj` (`<Version>`, `<AssemblyVersion>`, `<FileVersion>`) and `Services/BuildInfoService.cs`. The user strictly requires a version bump for every iteration.

## Git Remotes & Repository Topology

The project maintains a three-tier git repository setup:

1. **Local Git**:
   - Working tree and repository located on this machine at `C:\ClankerExplorer`.
   - Primary branch: `main`.

2. **Local Network Git (Gitea / WilkinsNAS)**:
   - Remote name: `gitea`
   - Web URL: `http://wilkinsnas.local:30008/sojun/ClankerExplorer`
   - SSH URL: `ssh://git@wilkinsnas.local:30009/sojun/ClankerExplorer.git`
   - SSH Key: `C:\Users\5900x\.ssh\id_ed25519_gitea` (mapped in `C:\Users\5900x\.ssh\config` to port `30009` and user `git`)
   - Used for private on-premises backup and continuous local-network synchronization.

3. **GitHub**:
   - Remote name: `origin`
   - URL: `https://github.com/Sojun80/ClankerExplorer.git`
   - Companion project remote: `museviewer` (`https://github.com/Sojun80/MuseImageViewer.git`)

### Remote Rules for Agents
- Never overwrite, rename, or remove `origin` or `museviewer` when configuring or modifying remotes.
- When instructed to push to local git / local network git, push to `gitea` (`git push gitea <branch>`).

