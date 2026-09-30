# Thumbnail cache: startup "rebuild" follow-ups

Symptom: thumbnails appear to regenerate on every app launch.

The persistent disk cache already exists (`%APPDATA%\C-Explorer\thumbnail-cache-v2\*.png`,
keyed on path + size + modified ticks + size bucket, 2 GB LRU). The likely problem is how
startup reaches it, not that it is missing.

## Suspected causes

1. **Disk hits are queued behind generation.** On a cold start the memory cache is empty and
   the viewport fast path (`ThumbnailService.RequestViewportThumbnailsAsync`) only checks
   memory, so every visible item is enqueued. The 2–3 FIFO workers (`WorkerLoopAsync`) can
   all be parked in `ProcessRequestAsync` waiting on the expensive-generation slot or on
   `IsActivelyScrolling` (video shell extraction may take up to 8 s), so millisecond disk
   hits wait behind slow misses.
2. **Failures are only remembered in memory.** `_failedSources` isn't saved to disk, so known-bad
   files (odd videos, locked files) are retried with up to 8 s timeouts on every launch.
3. **Possible real misses.** The key uses local `LastWriteTime.Ticks`
   (`FileSystemService.cs:353`), so a timezone change invalidates everything. Hit/miss
   counters (`_diskCacheHits`, `_cacheMisses`) are never surfaced, and the
   `DiskCache_PersistsAcrossServiceInstances…` test does not assert that the second read was
   actually a disk hit.

## Planned changes

- [ ] Surface memory-hit / disk-hit / miss counters (status bar or debug log) and measure a
      cold start.
- [ ] Serve disk hits before or outside the generation queue (check disk cache before
      enqueueing, or a dedicated fast I/O lane).
- [ ] Save the failure cache to disk under the same key scheme.
- [ ] Key on `LastWriteTimeUtc`; bump `CacheFormatVersion` to 3.
- [ ] Strengthen the disk-cache test to assert a disk hit (not a regeneration).
- [ ] Optional: query the Windows shell thumbnail cache first
      (`IShellItemImageFactory` + `SIIGBF_INCACHEONLY`).
- [ ] Optional: store entries as JPEG/WebP instead of PNG to make them smaller and faster to load.
