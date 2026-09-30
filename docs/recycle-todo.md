# Recycle bin: open follow-ups

Remaining items from the code review of "Refactor explorer pane and preserve deleted and
replaced items". Findings 1, 2, 4 and 5 were fixed in v1.6.15.

## Verify on Windows (v1.6.15 changes)

- [ ] Run the full test suite on Windows, especially `RecycleRoutingTests`
      (`WindowsRecycleFailure_FallsBackToManagedBin`, `WindowsRecycle_RemovesNameReservation`).
      On Linux these pass without reaching the Windows-bin code.
- [ ] Manual check: Copy → Replace onto a USB stick; the old file should land in
      `.clanker-recycle` on the stick instead of the operation failing.
- [ ] Manual check: extract an archive containing file `data` over an existing folder `data`;
      it should be refused with the folder left intact.
- [ ] Clear leftover empty files in `%APPDATA%\C-Explorer\recycle-names` (older versions never
      deleted them).

## Open

- [ ] **`.clanker-recycle` is visible and treated as a normal folder**
      (`Services/RecycleBinService.cs`, `DirectoryName`). Nothing hides it or leaves it out of
      listings and transfers:
  - Select All + Delete tries to move the bin into itself and throws.
  - Copying the parent folder also copies every recycled version and its `.metadata` records.
  - Deleting an item inside the bin nests it in `.clanker-recycle/.clanker-recycle`, so space
    is never freed; deleting from within the bin should be a permanent delete (with confirm).
  - The v1.6.15 fallback means this folder can now also appear on local/USB drives, not only
    network shares.
  - Likely fix: set Hidden (+System) on creation, skip it in `FileSystemService` listings (or
    behind the show-hidden setting), exclude it from `TransferEngine` recursion and from
    Select All deletes, and special-case deletes whose source is inside a bin.
- [ ] **Windows recycle runs under the global lock.** `RecycleLocal` starts an STA thread and
      a shell `IFileOperation` call while holding `Gate`, so parallel replacements serialise
      on the shell call. Consider narrowing the lock to name reservation and journaling.
