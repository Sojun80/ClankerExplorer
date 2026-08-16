# Large-folder scalability pass

## Result

Thumbnail mode is now viewport-driven and vertically virtualized. Opening a folder no longer starts thumbnail work for every file, and a 50,000-item headless UI regression proves that both details and thumbnail modes keep realized visuals below 200 controls.

The measurements below were taken on Windows 10 with .NET 8.0.204 against the same generated directories and one-byte JPG files. Each process performed an unmeasured warm-up pass before the measured pass. The baseline executable came from the untouched `HEAD` snapshot; the after executable came from this checkout.

| Items | Enumeration before | Enumeration after | Allocated before | Allocated after | Natural sort before | Natural sort after |
|---:|---:|---:|---:|---:|---:|---:|
| 1,000 | 28.37 ms | 1.46 ms | 0.97 MiB | 0.75 MiB | 4.13 ms | 3.73 ms |
| 10,000 | 289.36 ms | 11.47 ms | 9.59 MiB | 7.51 MiB | 21.29 ms | 22.71 ms |
| 50,000 | 1,345.26 ms | 63.03 ms | 46.04 MiB | 37.25 MiB | 107.06 ms | 108.81 ms |

Enumeration improved primarily by avoiding per-entry `LinkTarget`, last-access-time, POSIX-permission, and owner/group work when the corresponding data is not required. Natural sort CPU cost is essentially unchanged and remains O(n log n), but production refresh/filter/sort paths now perform it off the Avalonia UI thread and marshal back one completed collection.

Run the repeatable probe with:

```text
dotnet run --project tools/ClankerExplorer.PerformanceProbe -- <existing-directory>
```

## Main bottlenecks found

1. Thumbnail mode passed the complete filtered directory to `LoadThumbnailsAsync`, which created one task per eligible item. A 50,000-file folder therefore created a huge task/decode backlog before scrolling occurred.
2. Thumbnail mode used `ListBox` plus a plain `WrapPanel`. That panel is not virtualizing, so the UI created and measured a full thumbnail card for every file.
3. Enumeration eagerly read metadata that was hidden in the current column configuration, and queried `LinkTarget` for every entry.
4. Sorting itself costs about 109 ms at 50,000 items. It ran synchronously in production property-change paths.
5. The old memory cache was entry-count bounded FIFO rather than byte-bounded LRU, had no disk tier, and keyed without file size or an explicit cache-format version.

## Thumbnail scheduling

- The thumbnail surface groups items into lightweight logical rows and uses `VirtualizingStackPanel` vertically. Only realized rows get cell visuals.
- A 50–150 ms configurable debounce (90 ms default) waits for fast scrolling to settle.
- The realized row range defines visible items. The retained window adds a configurable 1–2 viewport band (1.5 default) above and below.
- Changing the viewport immediately cancels the previous generation, removes cancelled queued work, clears far-away `FileItem.ThumbnailImage` references, and submits the new visible range first.
- A 50,000-item planner regression with six visible five-column rows requests 30 visible items and retains only 120 total items.
- The queue is capped at 512 entries. Visible requests displace prefetch requests when full.

## Cache and workers

- Canonical thumbnail classes are 128, 256, and 512 pixels.
- Cache identity is SHA-256 over cache-format version, normalized full path, file size, source last-write ticks, and canonical size.
- The memory tier is a byte-bounded LRU (256 MiB default).
- The persistent tier lives under the app data directory in `thumbnail-cache-v1`, defaults to 2 GiB, records access time, and evicts least-recently-used entries to 90% of the limit on a throttled background cleanup.
- Disk writes use a temporary file followed by atomic replacement. `ClearDiskCacheAsync` is the future UI hook for “Clear thumbnail cache.”
- Generation uses three workers by default, configurable from one to eight. Image decode, shell extraction, disk reads/writes, and eviction stay off the UI thread.
- Unsupported/corrupt sources get a source-version-specific negative-cache entry, preventing retry loops until path/size/mtime/size-class changes.

All cache sizes, worker count, debounce, and prefetch distance are persisted settings rather than architectural constants.

## Virtualization and UI-thread audit

The details `DataGrid` was already virtualized. Its marquee code enumerates realized `DataGridRow` visuals, not all models. Thumbnail mode was accidentally de-virtualized by `WrapPanel`; it is now covered by the same 50,000-item headless regression as details mode. Variable thumbnail size rebuilds cheap logical row groupings and does not realize every card.

Filesystem enumeration and requested metadata already ran through `Task.Run`. Sorting/filtering now does as well. Thumbnail cache lookup, decode, generation, persistence, and cleanup are also background work. Only the final collection swap and thumbnail property assignment return to the UI dispatcher.

## Remaining scaling limits

- Directory publication is still one completed batch. Local warm enumeration is 63 ms at 50,000 items, so progressive insertion would currently add repeated collection/layout work for little benefit. Very slow network shares may still justify a separately measured, sorted batched-enumeration design.
- Natural sorting remains the largest measured CPU stage at roughly 109 ms for 50,000 names, although it no longer blocks the UI thread in production paths.
- ClankerExplorer currently has no `FileSystemWatcher` implementation. There is therefore no watcher event storm or reload loop to optimize, but views still require manual/operation-triggered refreshes.
- Started Windows shell thumbnail extraction cannot always be interrupted safely. At most the bounded worker count can remain occupied by already-started stale work; queued stale work is removed.
- The headless suite proves realization bounds and scheduling/cache correctness. A final subjective fast-scroll check with thousands of real image/video files on the release target is still worthwhile because shell providers and storage devices have widely different latency.

## Regression coverage

The suite now covers canonical size selection, persistent-cache reuse and source invalidation, viewport bounds at 50,000 items, Ctrl/Shift thumbnail selection, and details/thumbnail visual realization bounds at 50,000 items. Final result for this pass: 97 tests passed, zero failed.
