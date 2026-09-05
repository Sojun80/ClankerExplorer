using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

/// <summary>
/// High-performance asynchronous thumbnail retrieval and caching service.
/// Uses a bounded background worker pipeline with visible request prioritization,
/// cancellation propagation, memory/disk LRU caching, virtualization safety,
/// scroll-aware throttling, request de-duplication, and UI update batching.
/// </summary>
public class ThumbnailService : IDisposable
{
    private const int CacheFormatVersion = 2;
    private const int MaxTotalQueue = 48;
    private const int MaxVisibleQueue = 36;
    private const int MaxPrefetchQueue = 12;

    private static readonly Lazy<ThumbnailService> _instance = new(() => new ThumbnailService());
    public static ThumbnailService Instance => _instance.Value;

    private readonly object _memoryGate = new();
    private readonly Dictionary<string, MemoryEntry> _memoryCache = new();
    private readonly LinkedList<string> _memoryLru = new();
    private long _memoryBytes;

    private readonly object _queueGate = new();
    private readonly Queue<ThumbnailRequest> _visibleQueue = new();
    private readonly Queue<ThumbnailRequest> _prefetchQueue = new();
    private readonly HashSet<string> _queuedKeys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly CancellationTokenSource _workerCts = new();
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inflight = new();
    private readonly ConcurrentDictionary<string, byte> _failedSources = new();
    private readonly ConcurrentQueue<string> _failureLru = new();
    private readonly string _diskCacheDirectory;
    private readonly int _workerCount;

    private int _cleanupScheduled;
    private int _workersStarted;
    private int _activeWorkers;
    private int _activeGenerationWorkers;
    private int _viewportGeneration;
    private long _lastCleanupUtcTicks;
    private long _lastScrollTimestamp;

    // Diagnostics / Instrumentation counters
    private int _maxObservedQueueDepth;
    private long _droppedQueueFullCount;
    private long _discardedStaleCount;
    private long _suppressedDuplicateCount;
    private long _memoryCacheHits;
    private long _diskCacheHits;
    private long _cacheMisses;
    private long _generatedCount;
    private long _cancelledCount;
    private long _failedCount;

    // Batched UI Publication queue to prevent dispatcher flood
    private readonly ConcurrentQueue<ThumbnailPublication> _publicationQueue = new();
    private int _publicationScheduled;

    private static readonly HashSet<string> DirectImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tiff", ".tif"
    };

    public ThumbnailService(string? cacheDirectory = null, int? workerCount = null)
    {
        _diskCacheDirectory = cacheDirectory ?? Path.Combine(AppStoragePaths.GetDataDirectory(), $"thumbnail-cache-v{CacheFormatVersion}");
        Directory.CreateDirectory(_diskCacheDirectory);

        int configuredWorkers = SettingsService.Instance.CurrentSettings.ThumbnailWorkerCount;
        _workerCount = Math.Clamp(workerCount ?? configuredWorkers, 2, 4);
    }

    /// <summary>
    /// Notifies the thumbnail service of user scrolling activity to throttle background generation.
    /// </summary>
    public void NotifyScrollActivity()
    {
        Volatile.Write(ref _lastScrollTimestamp, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Indicates whether the user was scrolling within the last 200 ms.
    /// </summary>
    public bool IsActivelyScrolling
    {
        get
        {
            long last = Volatile.Read(ref _lastScrollTimestamp);
            return last != 0 && Stopwatch.GetElapsedTime(last).TotalMilliseconds < 200;
        }
    }

    /// <summary>
    /// Synchronously assigns thumbnails from the in-memory cache to the given items without
    /// blocking or enqueuing background tasks. Ideal during scrolling.
    /// </summary>
    public int TryPopulateFromMemoryCache(IEnumerable<FileItem> items, int targetSize)
    {
        int sizeBucket = GetCanonicalSize(targetSize);
        int hits = 0;
        foreach (var item in items)
        {
            if (item.IsDirectory || item.SizeBytes <= 0 || item.HasThumbnail) continue;

            string key = GetCacheKey(item.FullPath, item.SizeBytes, item.ModifiedTime.Ticks, sizeBucket);
            if (TryGetMemoryEntry(key, out var cached))
            {
                item.ThumbnailImage = cached;
                Interlocked.Increment(ref _memoryCacheHits);
                hits++;
            }
        }
        return hits;
    }

    /// <summary>
    /// Loads thumbnails for the current realized viewport and speculative prefetch range.
    /// Cancels previous speculative work, prioritizes visible items, and bounds queue depth.
    /// </summary>
    public Task LoadViewportAsync(
        IEnumerable<FileItem> visibleItems,
        IEnumerable<FileItem> prefetchItems,
        int targetSize,
        CancellationToken cancellationToken)
    {
        int generation = Interlocked.Increment(ref _viewportGeneration);

        lock (_queueGate)
        {
            // Drop any unstarted prefetch items immediately:
            // Stale prefetch requests outside the new scroll position are no longer relevant
            while (_prefetchQueue.TryDequeue(out var droppedPrefetch))
            {
                _queuedKeys.Remove(droppedPrefetch.Key);
                droppedPrefetch.Completion.TrySetCanceled();
                Interlocked.Increment(ref _discardedStaleCount);
            }

            // Prune cancelled requests from visible queue
            PruneQueue(_visibleQueue);
        }

        // Fast-path: satisfy visible items from memory cache first so they don't enter the queue
        int sizeBucket = GetCanonicalSize(targetSize);
        var unassignedVisible = new List<FileItem>();
        foreach (var item in visibleItems)
        {
            if (item.IsDirectory || item.SizeBytes <= 0 || cancellationToken.IsCancellationRequested) continue;

            string key = GetCacheKey(item.FullPath, item.SizeBytes, item.ModifiedTime.Ticks, sizeBucket);
            if (TryGetMemoryEntry(key, out var memBitmap))
            {
                Interlocked.Increment(ref _memoryCacheHits);
                if (item.ThumbnailImage == null)
                {
                    item.ThumbnailImage = memBitmap;
                }
            }
            else
            {
                unassignedVisible.Add(item);
            }
        }

        var completions = new List<Task>();

        foreach (var item in unassignedVisible)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                completions.Add(EnqueueAsync(item, targetSize, ThumbnailPriority.Visible, generation, cancellationToken));
            }
        }

        // Only enqueue prefetch if we are NOT actively scrolling and the queue has room
        if (!IsActivelyScrolling)
        {
            foreach (var item in prefetchItems)
            {
                if (!item.IsDirectory && item.SizeBytes > 0 && !cancellationToken.IsCancellationRequested)
                {
                    completions.Add(EnqueueAsync(item, targetSize, ThumbnailPriority.Prefetch, generation, cancellationToken));
                }
            }
        }

        return Task.WhenAll(completions);
    }

    /// <summary>
    /// Cancels all pending unstarted requests in the queues (e.g. when changing folders or closing views).
    /// </summary>
    public void CancelPendingRequests()
    {
        lock (_queueGate)
        {
            while (_visibleQueue.TryDequeue(out var req))
            {
                _queuedKeys.Remove(req.Key);
                req.Completion.TrySetCanceled();
                Interlocked.Increment(ref _discardedStaleCount);
            }

            while (_prefetchQueue.TryDequeue(out var req))
            {
                _queuedKeys.Remove(req.Key);
                req.Completion.TrySetCanceled();
                Interlocked.Increment(ref _discardedStaleCount);
            }

            _queuedKeys.Clear();
        }

        while (_publicationQueue.TryDequeue(out _)) { }
    }

    private void PruneQueue(Queue<ThumbnailRequest> queue)
    {
        int count = queue.Count;
        for (int index = 0; index < count; index++)
        {
            var request = queue.Dequeue();
            if (request.CancellationToken.IsCancellationRequested)
            {
                _queuedKeys.Remove(request.Key);
                request.Completion.TrySetCanceled(request.CancellationToken);
                Interlocked.Increment(ref _discardedStaleCount);
            }
            else
            {
                queue.Enqueue(request);
            }
        }
    }

    private Task EnqueueAsync(
        FileItem item,
        int targetSize,
        ThumbnailPriority priority,
        int generation,
        CancellationToken cancellationToken)
    {
        EnsureWorkersStarted();
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        int sizeBucket = GetCanonicalSize(targetSize);
        string key = GetCacheKey(item.FullPath, item.SizeBytes, item.ModifiedTime.Ticks, sizeBucket);

        // De-duplication: check if already in memory cache
        if (TryGetMemoryEntry(key, out var cached))
        {
            Interlocked.Increment(ref _memoryCacheHits);
            Interlocked.Increment(ref _suppressedDuplicateCount);
            PublishToUi(new ThumbnailPublication(item, item.FullPath, item.ModifiedTime, cached));
            return Task.FromResult<Bitmap?>(cached);
        }

        var completion = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ThumbnailRequest(item, targetSize, priority, generation, cancellationToken, completion, key);

        lock (_queueGate)
        {
            // De-duplication: check if already queued
            if (_queuedKeys.Contains(key))
            {
                Interlocked.Increment(ref _suppressedDuplicateCount);
                completion.TrySetResult(null);
                return completion.Task;
            }

            int currentTotal = _visibleQueue.Count + _prefetchQueue.Count;

            // Enforce hard upper bound on total queue
            if (currentTotal >= MaxTotalQueue)
            {
                if (priority == ThumbnailPriority.Prefetch)
                {
                    // Discard speculative prefetch immediately
                    Interlocked.Increment(ref _droppedQueueFullCount);
                    completion.TrySetCanceled(cancellationToken);
                    return completion.Task;
                }

                // For visible requests, evict prefetch work first
                if (_prefetchQueue.TryDequeue(out var evictedPrefetch))
                {
                    _queuedKeys.Remove(evictedPrefetch.Key);
                    evictedPrefetch.Completion.TrySetCanceled();
                    Interlocked.Increment(ref _droppedQueueFullCount);
                }
                // If no prefetch, evict the oldest visible request to make room
                else if (_visibleQueue.TryDequeue(out var evictedVisible))
                {
                    _queuedKeys.Remove(evictedVisible.Key);
                    evictedVisible.Completion.TrySetCanceled();
                    Interlocked.Increment(ref _droppedQueueFullCount);
                }
            }

            // Enforce per-queue bounds
            if (priority == ThumbnailPriority.Prefetch && _prefetchQueue.Count >= MaxPrefetchQueue)
            {
                Interlocked.Increment(ref _droppedQueueFullCount);
                completion.TrySetCanceled(cancellationToken);
                return completion.Task;
            }
            else if (priority == ThumbnailPriority.Visible && _visibleQueue.Count >= MaxVisibleQueue)
            {
                if (_visibleQueue.TryDequeue(out var oldestVisible))
                {
                    _queuedKeys.Remove(oldestVisible.Key);
                    oldestVisible.Completion.TrySetCanceled();
                    Interlocked.Increment(ref _droppedQueueFullCount);
                }
            }

            _queuedKeys.Add(key);
            (priority == ThumbnailPriority.Visible ? _visibleQueue : _prefetchQueue).Enqueue(request);

            int newTotal = _visibleQueue.Count + _prefetchQueue.Count;
            if (newTotal > _maxObservedQueueDepth)
            {
                _maxObservedQueueDepth = newTotal;
            }
        }

        _queueSignal.Release();
        return completion.Task;
    }

    private void EnsureWorkersStarted()
    {
        if (Interlocked.Exchange(ref _workersStarted, 1) != 0) return;
        for (int i = 0; i < _workerCount; i++)
        {
            _ = Task.Run(WorkerLoopAsync);
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (!_workerCts.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(_workerCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ThumbnailRequest? request = null;
            lock (_queueGate)
            {
                // Always pull visible requests first before speculative prefetch
                if (!_visibleQueue.TryDequeue(out request))
                {
                    _prefetchQueue.TryDequeue(out request);
                }

                if (request != null)
                {
                    _queuedKeys.Remove(request.Key);
                }
            }

            if (request == null) continue;

            // Check stale: cancelled?
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
                Interlocked.Increment(ref _discardedStaleCount);
                continue;
            }

            // Check stale: old prefetch generation?
            if (request.Priority == ThumbnailPriority.Prefetch && request.Generation < _viewportGeneration)
            {
                request.Completion.TrySetCanceled();
                Interlocked.Increment(ref _discardedStaleCount);
                continue;
            }

            // Check active scrolling throttling:
            // While actively scrolling, drop prefetch requests immediately
            if (IsActivelyScrolling && request.Priority == ThumbnailPriority.Prefetch)
            {
                request.Completion.TrySetCanceled();
                Interlocked.Increment(ref _discardedStaleCount);
                continue;
            }

            Interlocked.Increment(ref _activeWorkers);
            try
            {
                var bitmap = await ProcessRequestAsync(request);
                request.Completion.TrySetResult(bitmap);
            }
            catch (OperationCanceledException)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
                Interlocked.Increment(ref _discardedStaleCount);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedCount);
                request.Completion.TrySetResult(null);
                System.Diagnostics.Debug.WriteLine($"[ThumbnailService] Error generating thumbnail for {request.Path}: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeWorkers);
            }
        }
    }

    private async Task<Bitmap?> ProcessRequestAsync(ThumbnailRequest request)
    {
        var ct = request.CancellationToken;
        ct.ThrowIfCancellationRequested();

        string path = request.Path;
        long fileSize = request.FileSize;
        DateTime modifiedTime = request.ModifiedTime;
        int targetSize = request.TargetSize;
        int sizeBucket = GetCanonicalSize(targetSize);

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // Fast probe without blocking disk I/O if possible:
        // If FileSize is unknown (<= 0), check File.Exists
        if (fileSize <= 0 && !File.Exists(path))
        {
            return null;
        }

        string key = request.Key;

        // 1. Check in-memory cache
        if (TryGetMemoryEntry(key, out var memBitmap))
        {
            Interlocked.Increment(ref _memoryCacheHits);
            PublishToUi(new ThumbnailPublication(request.Item, request.Path, request.ModifiedTime, memBitmap));
            return memBitmap;
        }

        if (_failedSources.ContainsKey(key))
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        // 2. Check disk cache (off UI thread)
        string diskPath = GetDiskPath(key);
        var diskBitmap = TryLoadDiskEntry(diskPath);
        if (diskBitmap != null)
        {
            Interlocked.Increment(ref _diskCacheHits);
            AddMemoryEntry(key, diskBitmap);
            PublishToUi(new ThumbnailPublication(request.Item, request.Path, request.ModifiedTime, diskBitmap));
            return diskBitmap;
        }

        ct.ThrowIfCancellationRequested();

        // Dynamic worker concurrency check:
        // During active scrolling, at most 1 worker performs new generation
        while (IsActivelyScrolling && Volatile.Read(ref _activeGenerationWorkers) >= 1)
        {
            await Task.Delay(50, ct);
            ct.ThrowIfCancellationRequested();
        }

        // 3. Cache Miss: Generate
        Interlocked.Increment(ref _activeGenerationWorkers);
        Bitmap? bitmap = null;
        try
        {
            Interlocked.Increment(ref _cacheMisses);
            bitmap = await LoadOrGenerateAsync(path, key, sizeBucket, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeGenerationWorkers);
        }

        if (bitmap != null)
        {
            Interlocked.Increment(ref _generatedCount);
            AddMemoryEntry(key, bitmap);
            SaveDiskEntry(diskPath, bitmap);
            PublishToUi(new ThumbnailPublication(request.Item, request.Path, request.ModifiedTime, bitmap));
            return bitmap;
        }
        else
        {
            AddFailure(key);
            Interlocked.Increment(ref _failedCount);
            return null;
        }
    }

    private void PublishToUi(ThumbnailPublication pub)
    {
        _publicationQueue.Enqueue(pub);
        if (Interlocked.CompareExchange(ref _publicationScheduled, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(DrainPublications, DispatcherPriority.Normal);
        }
    }

    private void DrainPublications()
    {
        Volatile.Write(ref _publicationScheduled, 0);
        int count = 0;
        while (count < 32 && _publicationQueue.TryDequeue(out var pub))
        {
            count++;
            var item = pub.Item;
            if (item == null) continue;

            // Protection against recycled/virtualized rows or folder navigation:
            if (!string.Equals(item.FullPath, pub.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.ModifiedTime != pub.ModifiedTime)
            {
                continue;
            }

            item.ThumbnailImage = pub.Bitmap;
        }

        if (!_publicationQueue.IsEmpty)
        {
            if (Interlocked.CompareExchange(ref _publicationScheduled, 1, 0) == 0)
            {
                Dispatcher.UIThread.Post(DrainPublications, DispatcherPriority.Normal);
            }
        }
    }

    /// <summary>
    /// Retrieves a single thumbnail from cache or generates it asynchronously.
    /// Compatible with standalone callers such as InspectorViewModel and unit tests.
    /// </summary>
    public async Task<Bitmap?> GetThumbnailAsync(string path, DateTime modifiedTime, int targetSize, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        var fileInfo = new FileInfo(path);
        int sizeBucket = GetCanonicalSize(targetSize);
        string key = GetCacheKey(path, fileInfo.Length, modifiedTime.Ticks, sizeBucket);

        if (TryGetMemoryEntry(key, out var cached))
        {
            Interlocked.Increment(ref _memoryCacheHits);
            return cached;
        }

        if (_failedSources.ContainsKey(key)) return null;

        string diskPath = GetDiskPath(key);
        var diskBitmap = TryLoadDiskEntry(diskPath);
        if (diskBitmap != null)
        {
            Interlocked.Increment(ref _diskCacheHits);
            AddMemoryEntry(key, diskBitmap);
            return diskBitmap;
        }

        var loadTask = _inflight.GetOrAdd(key, _ => Task.Run(async () =>
        {
            Interlocked.Increment(ref _cacheMisses);
            var generated = await LoadOrGenerateAsync(path, key, sizeBucket, CancellationToken.None);
            if (generated != null)
            {
                Interlocked.Increment(ref _generatedCount);
                AddMemoryEntry(key, generated);
                SaveDiskEntry(diskPath, generated);
            }
            else
            {
                AddFailure(key);
                Interlocked.Increment(ref _failedCount);
            }
            return generated;
        }, CancellationToken.None));

        try
        {
            return await loadTask.WaitAsync(cancellationToken);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Task<Bitmap?>>(key, loadTask));
        }
    }

    public static int GetCanonicalSize(int size)
    {
        if (size <= 128) return 128;
        if (size <= 256) return 256;
        return 512;
    }

    /// <summary>
    /// Explicitly caches a custom-generated thumbnail for a file, updating memory and disk cache.
    /// </summary>
    public void SetCustomThumbnail(string path, DateTime modifiedTime, Bitmap bitmap, int targetSize)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || bitmap == null) return;
        try
        {
            var fileInfo = new FileInfo(path);
            int sizeBucket = GetCanonicalSize(targetSize);
            string key = GetCacheKey(path, fileInfo.Length, modifiedTime.Ticks, sizeBucket);

            AddMemoryEntry(key, bitmap);
            _ = Task.Run(() => SaveDiskEntry(GetDiskPath(key), bitmap));
        }
        catch { }
    }

    private async Task<Bitmap?> LoadOrGenerateAsync(string path, string key, int sizeBucket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Bitmap? result = null;
        string ext = Path.GetExtension(path);

        try
        {
            // 1. Direct Image Provider
            if (DirectImageExtensions.Contains(ext))
            {
                result = DecodeImageFile(path, sizeBucket, cancellationToken);
            }
            // 2. Video Provider (try Windows Shell provider first for silent, fast background extraction)
            else if (VideoThumbnailService.IsVideoFile(path))
            {
                if (OperatingSystem.IsWindows() && !cancellationToken.IsCancellationRequested)
                {
                    result = ExtractWindowsShellThumbnail(path, sizeBucket, cancellationToken);
                }

                if (result == null && !cancellationToken.IsCancellationRequested)
                {
                    result = await VideoThumbnailService.Instance.ExtractSmartVideoThumbnailAsync(path, sizeBucket, cancellationToken);
                }
            }
            // 3. 3D Model Provider (STL)
            else if (Preview.StlPreviewService.Instance.IsStlFile(path))
            {
                result = await Preview.StlPreviewService.Instance.GenerateThumbnailAsync(path, sizeBucket, cancellationToken);
            }

            // 4. Windows Shell Provider (for PDFs, 3D models, Documents, and fallback)
            if (result == null && OperatingSystem.IsWindows() && !cancellationToken.IsCancellationRequested)
            {
                result = ExtractWindowsShellThumbnail(path, sizeBucket, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThumbnailService] Extraction error for {path}: {ex.Message}");
            return null;
        }

        return result;
    }

    public static string GetCacheKey(string path, long fileSize, long ticks, int sizeBucket)
    {
        string normalized = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        byte[] input = Encoding.UTF8.GetBytes($"{CacheFormatVersion}|{normalized}|{fileSize}|{ticks}|{sizeBucket}");
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private bool TryGetMemoryEntry(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Bitmap? bitmap)
    {
        lock (_memoryGate)
        {
            if (_memoryCache.TryGetValue(key, out var entry))
            {
                _memoryLru.Remove(entry.Node);
                _memoryLru.AddFirst(entry.Node);
                bitmap = entry.Bitmap;
                return true;
            }
        }

        bitmap = null;
        return false;
    }

    public void AddMemoryEntry(string key, Bitmap bitmap)
    {
        long approximateBytes = Math.Max(1, (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
        long limit = Math.Max(16 * 1024 * 1024, SettingsService.Instance.CurrentSettings.ThumbnailMemoryCacheMaxBytes);
        lock (_memoryGate)
        {
            if (_memoryCache.ContainsKey(key)) return;
            var node = _memoryLru.AddFirst(key);
            _memoryCache.Add(key, new MemoryEntry(bitmap, node, approximateBytes));
            _memoryBytes += approximateBytes;

            while (_memoryBytes > limit && _memoryLru.Last is { } oldest)
            {
                _memoryLru.RemoveLast();
                if (_memoryCache.Remove(oldest.Value, out var removed))
                {
                    _memoryBytes -= removed.ApproximateBytes;
                }
            }
        }
    }

    public void ClearCache()
    {
        lock (_memoryGate)
        {
            _memoryCache.Clear();
            _memoryLru.Clear();
            _memoryBytes = 0;
        }
        _failedSources.Clear();
        while (_failureLru.TryDequeue(out _)) { }
    }

    private void AddFailure(string key)
    {
        if (!_failedSources.TryAdd(key, 0)) return;
        _failureLru.Enqueue(key);
        while (_failedSources.Count > 4096 && _failureLru.TryDequeue(out var oldest))
        {
            _failedSources.TryRemove(oldest, out _);
        }
    }

    public int QueuedRequestCount
    {
        get
        {
            lock (_queueGate) return _visibleQueue.Count + _prefetchQueue.Count;
        }
    }

    public int MaxObservedQueueDepth => Volatile.Read(ref _maxObservedQueueDepth);
    public long DroppedQueueFullCount => Interlocked.Read(ref _droppedQueueFullCount);
    public long DiscardedStaleCount => Interlocked.Read(ref _discardedStaleCount);
    public long SuppressedDuplicateCount => Interlocked.Read(ref _suppressedDuplicateCount);
    public int ActiveWorkerCount => Volatile.Read(ref _activeWorkers);
    public int ActiveGenerationWorkerCount => Volatile.Read(ref _activeGenerationWorkers);

    public int MemoryCacheEntryCount
    {
        get
        {
            lock (_memoryGate) return _memoryCache.Count;
        }
    }

    public long MemoryCacheHitCount => Interlocked.Read(ref _memoryCacheHits);
    public long DiskCacheHitCount => Interlocked.Read(ref _diskCacheHits);
    public long CacheMissCount => Interlocked.Read(ref _cacheMisses);
    public long GeneratedCount => Interlocked.Read(ref _generatedCount);
    public long CancelledCount => Interlocked.Read(ref _cancelledCount);
    public long FailedCount => Interlocked.Read(ref _failedCount);

    public Task ClearDiskCacheAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(_diskCacheDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(_diskCacheDirectory, "*.png"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Delete(file); } catch { }
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        _workerCts.Cancel();
        CancelPendingRequests();
        ClearCache();
    }

    private string GetDiskPath(string key) => Path.Combine(_diskCacheDirectory, key + ".png");

    private static Bitmap? TryLoadDiskEntry(string diskPath)
    {
        try
        {
            if (!File.Exists(diskPath)) return null;
            var bitmap = new Bitmap(diskPath);
            try { File.SetLastAccessTimeUtc(diskPath, DateTime.UtcNow); } catch { }
            return bitmap;
        }
        catch
        {
            try { File.Delete(diskPath); } catch { }
            return null;
        }
    }

    private void SaveDiskEntry(string diskPath, Bitmap bitmap)
    {
        string tempPath = diskPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                bitmap.Save(stream);
            }
            File.Move(tempPath, diskPath, true);
            ScheduleDiskCleanup();
        }
        catch { }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private void ScheduleDiskCleanup()
    {
        long now = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref _lastCleanupUtcTicks);
        if (last != 0 && now - last < TimeSpan.FromMinutes(5).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastCleanupUtcTicks, now, last) != last) return;
        if (Interlocked.Exchange(ref _cleanupScheduled, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                long limit = Math.Max(64 * 1024 * 1024, SettingsService.Instance.CurrentSettings.ThumbnailDiskCacheMaxBytes);
                var files = new DirectoryInfo(_diskCacheDirectory)
                    .EnumerateFiles("*.png")
                    .OrderBy(file => file.LastAccessTimeUtc)
                    .ToList();
                long total = files.Sum(file => file.Length);
                long target = (long)(limit * 0.9);
                foreach (var file in files)
                {
                    if (total <= target) break;
                    long length = file.Length;
                    try
                    {
                        file.Delete();
                        total -= length;
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                Volatile.Write(ref _cleanupScheduled, 0);
            }
        });
    }

    private static Bitmap? DecodeImageFile(string path, int targetWidth, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            cancellationToken.ThrowIfCancellationRequested();
            return Bitmap.DecodeToWidth(stream, targetWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private sealed record MemoryEntry(Bitmap Bitmap, LinkedListNode<string> Node, long ApproximateBytes);
    private sealed record ThumbnailPublication(FileItem Item, string Path, DateTime ModifiedTime, Bitmap Bitmap);

    #region Windows Shell Thumbnail Extraction

    [ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailProvider
    {
        [PreserveSig]
        int GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler([In] IntPtr pbc, [In, MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppv);
        void GetParent();
        void GetDisplayName();
        void GetAttributes();
        void Compare();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] lpBits, ref BITMAPINFO pbmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    private static readonly Guid IShellItemGuid = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid BHID_ThumbnailHandler = new("7b2e650a-8e20-4f4a-b09e-6597afc72fb0");
    private static readonly Guid IID_IThumbnailProvider = new("e357fccd-a995-4576-b01f-234630154e96");

    internal static Bitmap? ExtractWindowsShellThumbnail(string filePath, int targetSize, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return null;

        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, IShellItemGuid, out var shellItem);
            if (hr != 0 || shellItem == null) return null;

            if (cancellationToken.IsCancellationRequested) return null;

            int hrBind = shellItem.BindToHandler(IntPtr.Zero, BHID_ThumbnailHandler, IID_IThumbnailProvider, out var pProvider);
            if (hrBind == 0 && pProvider != IntPtr.Zero)
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested) return null;
                    var provider = (IThumbnailProvider)Marshal.GetObjectForIUnknown(pProvider);
                    hr = provider.GetThumbnail((uint)targetSize, out hBitmap, out _);
                }
                finally
                {
                    Marshal.Release(pProvider);
                }
            }

            if (hr == 0 && hBitmap != IntPtr.Zero && !cancellationToken.IsCancellationRequested)
            {
                return ConvertHBitmapToAvaloniaBitmap(hBitmap);
            }
        }
        catch
        {
            // Fall back gracefully if COM extraction fails
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }
        }
        return null;
    }

    private static Bitmap? ConvertHBitmapToAvaloniaBitmap(IntPtr hBitmap)
    {
        if (hBitmap == IntPtr.Zero) return null;

        try
        {
            if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0)
            {
                return null;
            }

            int width = bm.bmWidth;
            int height = Math.Abs(bm.bmHeight);
            if (width <= 0 || height <= 0) return null;

            int stride = width * 4;
            int byteCount = stride * height;
            byte[] pixelData = new byte[byteCount];

            // If Shell returned a 32-bit DIB section with direct memory pointer:
            if (bm.bmBits != IntPtr.Zero && bm.bmBitsPixel == 32)
            {
                // Standard Windows DIB sections (bmHeight > 0) are stored bottom-up.
                // Invert scanlines to top-down order for Avalonia.
                if (bm.bmHeight > 0)
                {
                    for (int y = 0; y < height; y++)
                    {
                        int srcRow = height - 1 - y;
                        IntPtr srcPtr = bm.bmBits + (srcRow * stride);
                        Marshal.Copy(srcPtr, pixelData, y * stride, stride);
                    }
                }
                else
                {
                    Marshal.Copy(bm.bmBits, pixelData, 0, byteCount);
                }
            }
            else
            {
                // Fallback to GetDIBits with explicit top-down biHeight for DDBs
                IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
                try
                {
                    var bmi = new BITMAPINFO();
                    bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                    bmi.bmiHeader.biWidth = width;
                    bmi.bmiHeader.biHeight = -height; // Request top-down DIB from GDI
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0; // BI_RGB

                    int lines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixelData, ref bmi, 0);
                    if (lines == 0) return null;
                }
                finally
                {
                    if (hdc != IntPtr.Zero) DeleteDC(hdc);
                }
            }

            // Ensure RGBX/BGRX bitmaps with 0 alpha are opaque
            bool hasNonZeroAlpha = false;
            for (int i = 3; i < pixelData.Length; i += 4)
            {
                if (pixelData[i] != 0)
                {
                    hasNonZeroAlpha = true;
                    break;
                }
            }
            if (!hasNonZeroAlpha)
            {
                for (int i = 3; i < pixelData.Length; i += 4)
                {
                    pixelData[i] = 255;
                }
            }

            // Create WriteableBitmap from raw 32bpp BGRA pixels
            var wbm = new WriteableBitmap(
                new Avalonia.PixelSize(width, height),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using (var fb = wbm.Lock())
            {
                Marshal.Copy(pixelData, 0, fb.Address, pixelData.Length);
            }
            return wbm;
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
