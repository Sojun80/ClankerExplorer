using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Supports direct image decoding and Windows Shell thumbnail providers with cancellation.
/// </summary>
public class ThumbnailService : IDisposable
{
    private const int CacheFormatVersion = 2;
    private const int CanonicalThumbnailSizeSmall = 128;
    private const int MaxQueuedRequests = 512;
    private static readonly Lazy<ThumbnailService> _instance = new(() => new ThumbnailService());
    public static ThumbnailService Instance => _instance.Value;

    private readonly object _memoryGate = new();
    private readonly Dictionary<string, MemoryEntry> _memoryCache = new();
    private readonly LinkedList<string> _memoryLru = new();
    private long _memoryBytes;

    private readonly object _queueGate = new();
    private readonly Queue<ThumbnailWorkItem> _visibleQueue = new();
    private readonly Queue<ThumbnailWorkItem> _prefetchQueue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly CancellationTokenSource _workerCts = new();
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inflight = new();
    private readonly ConcurrentDictionary<string, byte> _failedSources = new();
    private readonly ConcurrentQueue<string> _failureLru = new();
    private readonly string _diskCacheDirectory;
    private readonly int _workerCount;
    private int _cleanupScheduled;
    private int _workersStarted;
    private long _lastCleanupUtcTicks;

    private static readonly HashSet<string> DirectImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tiff", ".tif"
    };

    public ThumbnailService(string? cacheDirectory = null, int? workerCount = null)
    {
        _diskCacheDirectory = cacheDirectory ?? Path.Combine(AppStoragePaths.GetDataDirectory(), $"thumbnail-cache-v{CacheFormatVersion}");
        Directory.CreateDirectory(_diskCacheDirectory);

        _workerCount = Math.Clamp(workerCount ?? SettingsService.Instance.CurrentSettings.ThumbnailWorkerCount, 1, 8);
    }

    /// <summary>
    /// Replaces the outstanding viewport request. Visible work is always dequeued before
    /// prefetch work; callers cancel the previous token when the viewport changes.
    /// </summary>
    public Task LoadViewportAsync(
        IEnumerable<FileItem> visibleItems,
        IEnumerable<FileItem> prefetchItems,
        int targetSize,
        CancellationToken cancellationToken)
    {
        PruneCancelledRequests();
        var completions = new List<Task>();
        foreach (var item in visibleItems)
        {
            if (!item.IsDirectory && item.SizeBytes > 0)
            {
                completions.Add(EnqueueAsync(item, targetSize, ThumbnailPriority.Visible, cancellationToken));
            }
        }

        foreach (var item in prefetchItems)
        {
            if (!item.IsDirectory && item.SizeBytes > 0)
            {
                completions.Add(EnqueueAsync(item, targetSize, ThumbnailPriority.Prefetch, cancellationToken));
            }
        }

        return Task.WhenAll(completions);
    }

    private void PruneCancelledRequests()
    {
        lock (_queueGate)
        {
            PruneQueue(_visibleQueue);
            PruneQueue(_prefetchQueue);
        }
    }

    private static void PruneQueue(Queue<ThumbnailWorkItem> queue)
    {
        int count = queue.Count;
        for (int index = 0; index < count; index++)
        {
            var request = queue.Dequeue();
            if (request.CancellationToken.IsCancellationRequested) request.Completion.TrySetResult();
            else queue.Enqueue(request);
        }
    }

    /// <summary>
    /// Retrieves a single thumbnail from cache or generates it asynchronously.
    /// </summary>
    public async Task<Bitmap?> GetThumbnailAsync(string path, DateTime modifiedTime, int targetSize, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        var fileInfo = new FileInfo(path);
        int sizeBucket = GetCanonicalSize(targetSize);
        string key = GetCacheKey(path, fileInfo.Length, modifiedTime.Ticks, sizeBucket);

        if (TryGetMemoryEntry(key, out var cached))
        {
            return cached;
        }

        if (_failedSources.ContainsKey(key)) return null;

        var loadTask = _inflight.GetOrAdd(key, _ => LoadOrGenerateAsync(path, key, sizeBucket, CancellationToken.None));
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
        string diskPath = GetDiskPath(key);
        var diskBitmap = await Task.Run(() => TryLoadDiskEntry(diskPath), cancellationToken);
        if (diskBitmap != null)
        {
            AddMemoryEntry(key, diskBitmap);
            return diskBitmap;
        }

        Bitmap? result = null;
        string ext = Path.GetExtension(path);

        // 1. Direct Image Provider
        if (DirectImageExtensions.Contains(ext))
        {
            result = await Task.Run(() => DecodeImageFile(path, sizeBucket), cancellationToken);
        }
        // 2. Smart Video Thumbnail Provider (Multi-candidate scoring, seek-based, non-blocking)
        else if (VideoThumbnailService.IsVideoFile(path))
        {
            result = await VideoThumbnailService.Instance.ExtractSmartVideoThumbnailAsync(path, sizeBucket, cancellationToken);
        }
        // 3. 3D Model Provider (STL)
        else if (Preview.StlPreviewService.Instance.IsStlFile(path))
        {
            result = await Preview.StlPreviewService.Instance.GenerateThumbnailAsync(path, sizeBucket, cancellationToken);
        }

        // 4. Windows Shell Provider (for PDFs, 3D models, Documents, and fallback)
        if (result == null && OperatingSystem.IsWindows())
        {
            result = await Task.Run(() => ExtractWindowsShellThumbnail(path, sizeBucket), cancellationToken);
        }

        if (result != null)
        {
            AddMemoryEntry(key, result);
            await Task.Run(() => SaveDiskEntry(diskPath, result), CancellationToken.None);
        }
        else
        {
            AddFailure(key);
        }

        return result;
    }

    private Task EnqueueAsync(FileItem item, int targetSize, ThumbnailPriority priority, CancellationToken cancellationToken)
    {
        EnsureWorkersStarted();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ThumbnailWorkItem(item, targetSize, priority, cancellationToken, completion);
        lock (_queueGate)
        {
            int count = _visibleQueue.Count + _prefetchQueue.Count;
            if (count >= MaxQueuedRequests)
            {
                if (priority == ThumbnailPriority.Prefetch)
                {
                    completion.TrySetResult();
                    return completion.Task;
                }

                if (_prefetchQueue.TryDequeue(out var dropped))
                {
                    dropped.Completion.TrySetResult();
                }
                else if (_visibleQueue.TryDequeue(out dropped))
                {
                    dropped.Completion.TrySetResult();
                }
            }

            (priority == ThumbnailPriority.Visible ? _visibleQueue : _prefetchQueue).Enqueue(request);
        }

        _queueSignal.Release();
        return completion.Task;
    }

    private void EnsureWorkersStarted()
    {
        if (Interlocked.Exchange(ref _workersStarted, 1) != 0) return;
        for (int i = 0; i < _workerCount; i++) _ = Task.Run(WorkerLoopAsync);
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

            ThumbnailWorkItem? request = null;
            lock (_queueGate)
            {
                if (!_visibleQueue.TryDequeue(out request))
                {
                    _prefetchQueue.TryDequeue(out request);
                }
            }

            if (request == null) continue;
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetResult();
                continue;
            }

            try
            {
                var bitmap = await GetThumbnailAsync(
                    request.Item.FullPath,
                    request.Item.ModifiedTime,
                    request.TargetSize,
                    request.CancellationToken);

                if (bitmap != null && !request.CancellationToken.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!request.CancellationToken.IsCancellationRequested)
                        {
                            request.Item.ThumbnailImage = bitmap;
                        }
                    }, request.Priority == ThumbnailPriority.Visible
                        ? DispatcherPriority.Normal
                        : DispatcherPriority.Background);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                request.Completion.TrySetResult();
            }
        }
    }

    private static string GetCacheKey(string path, long fileSize, long ticks, int sizeBucket)
    {
        string normalized = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        byte[] input = Encoding.UTF8.GetBytes($"{CacheFormatVersion}|{normalized}|{fileSize}|{ticks}|{sizeBucket}");
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private bool TryGetMemoryEntry(string key, out Bitmap? bitmap)
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

    private void AddMemoryEntry(string key, Bitmap bitmap)
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

    public int MemoryCacheEntryCount
    {
        get
        {
            lock (_memoryGate) return _memoryCache.Count;
        }
    }

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

    private static Bitmap? DecodeImageFile(string path, int targetWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, targetWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }

    private sealed record MemoryEntry(Bitmap Bitmap, LinkedListNode<string> Node, long ApproximateBytes);
    private sealed record ThumbnailWorkItem(
        FileItem Item,
        int TargetSize,
        ThumbnailPriority Priority,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion);
    private enum ThumbnailPriority { Visible, Prefetch }

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

    private static Bitmap? ExtractWindowsShellThumbnail(string filePath, int targetSize)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, IShellItemGuid, out var shellItem);
            if (hr != 0 || shellItem == null) return null;

            int hrBind = shellItem.BindToHandler(IntPtr.Zero, BHID_ThumbnailHandler, IID_IThumbnailProvider, out var pProvider);
            if (hrBind == 0 && pProvider != IntPtr.Zero)
            {
                try
                {
                    var provider = (IThumbnailProvider)Marshal.GetObjectForIUnknown(pProvider);
                    hr = provider.GetThumbnail((uint)targetSize, out hBitmap, out _);
                }
                finally
                {
                    Marshal.Release(pProvider);
                }
            }

            if (hr == 0 && hBitmap != IntPtr.Zero)
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
                // Invert the scanlines to top-down order for Avalonia.
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
