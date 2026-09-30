# Recycling and replacement (1.6.13)

Local Windows paths use the desktop Recycle Bin. UNC paths and mapped network drives use a managed ".clanker-recycle" folder beside the item's original parent directory. No recycle failure falls back to permanent deletion.

Delete, Copy -> Replace, and Move -> Replace share the recycle service. The incoming item is prepared first; the old destination must be recycled before promotion. Each conflict is handled separately. Failed promotion attempts to restore the old destination without overwriting anything that appeared in the meantime. Archive extraction and ZIP creation also preserve replaced items.

Names include local time to the second, for example report_2026-09-29_12-34-56.txt. Same-second collisions use -1, -2, and so on. Windows items are renamed before shell recycling; if the shell refuses recycling, that rename is undone. Windows Restore restores the timestamped name into the original parent folder. The application history retains the exact pre-rename original path.

Managed-bin entries have a JSON restore record under .metadata. Open Recycle Bin in a pane's context menu opens the native bin for local paths and the folder's managed bin for network paths. Select a managed entry and choose Restore to Original Location. Restore refuses an occupied original path; move or rename that current item first.

Open Recycle History opens the persistent JSON-lines journal in Notepad. It lives in recycle-history.jsonl in the application's data directory (normally %APPDATA%\C-Explorer). Records include original paths, timestamped names, native recycle identifiers where available, incoming sources, timestamps, and requested/success/failure states. The service flushes recovery records before destructive transitions. An interruption or an I/O failure after a committed move can leave a requested event without its completion event; retain that record for reconciliation.

Explicit permanent deletes are recorded but remain permanent. Copies or moves performed by external applications, including the standalone 7-Zip GUI, are outside this application's replacement service.

## Validation

The normal suite uses isolated managed storage and a fake Windows backend. It covers copy/move replacement, nested copy conflicts, independent batch failures, rollback, restore collisions, persistent metadata, name collisions, local/network routing, ordinary Delete, and refusal when the journal cannot be written.

An opt-in native round-trip test is available with CLANKER_TEST_WINDOWS_RECYCLE=1 and the filter FullyQualifiedName~RealWindowsBin_OptInSmokeTest. It creates only a uniquely named synthetic file beside the test executable. In the agent's current execution session Windows reported that recycling was unavailable; the safeguard aborted and preserved the file. A successful native recycle/restore round trip has therefore not been verified in that session. The production adapter retains the pre-delete safeguard used by Electron's Windows trash implementation: https://github.com/electron/electron/blob/main/shell/common/platform_util_win.cc .
