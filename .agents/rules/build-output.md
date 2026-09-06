# Build Output & Binary Synchronization Rule

## Expected Executable Path
The user expects the primary development build to land and execute from:
```text
C:\ClankerExplorer\bin\Debug\net8.0\ClankerExplorer.exe
```

## Post-Build Synchronization
- `ClankerExplorer.csproj` builds to `bin\Debug\net8.0-windows10.0.19041.0\win-x64\`.
- The `SyncNet8LegacyOutput` target copies all output files to `bin\Debug\net8.0\`.

## Critical Gotcha: File Locking
- If `ClankerExplorer.exe` is running when building, Windows file locking causes `SyncNet8LegacyOutput` to fail with `warning MSB3021`.
- Because of `ContinueOnError="true"`, the build succeeds without error, but `bin\Debug\net8.0\` remains on the stale older binary.
- Agents must ensure no running `ClankerExplorer` process is locking files before building, and verify the file version in `bin\Debug\net8.0\ClankerExplorer.dll` matches after build.
