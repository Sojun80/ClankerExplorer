# ClankerExplorer regression tests

Run the complete suite from the repository root:

```bash
dotnet test
```

The suite is split by behavior rather than by implementation detail:

- service and view-model tests cover tabs, panes, navigation, filtering, sorting, persistence, Quick Access, and pointer-gesture decisions;
- filesystem integration tests use a fresh `TemporaryFileSystem` tree for every test;
- two Avalonia Headless smoke tests verify that the application window initializes and that a tab header is actually rendered.

The test assembly sets `CLANKEREXPLORER_DATA_DIR` to a process-specific temporary directory before application singletons initialize. Tests must use that location or `TemporaryFileSystem`; they must never use a real user profile or folder.

Keep UI tests limited to behavior that cannot be covered reliably below the view layer. Native DataGrid keyboard and click-selection semantics, operating-system drag/drop, recycle-bin integration, external tools, network discovery, and live filesystem-watcher timing are intentionally outside the fast suite for now.

For a reproducible bug, add a failing regression test first, make the smallest production fix, and retain the test.
