using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
public class ThumbnailService
{
    private static readonly Lazy<ThumbnailService> _instance = new(() => new ThumbnailService());
    public static ThumbnailService Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, Bitmap> _cache = new();
    private readonly ConcurrentQueue<string> _lruQueue = new();
    private const int MaxCacheEntries = 1200;

    private static readonly HashSet<string> DirectImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tiff", ".tif"
    };

    public ThumbnailService()
    {
    }

    /// <summary>
    /// Loads thumbnails for a collection of FileItems in background tasks with cancellation.
    /// </summary>
    public async Task LoadThumbnailsAsync(IEnumerable<FileItem> items, int targetSize, CancellationToken cancellationToken)
    {
        var itemList = new List<FileItem>(items);
        var sizeBucket = GetSizeBucket(targetSize);

        using var semaphore = new SemaphoreSlim(4);
        var tasks = new List<Task>();

        foreach (var item in itemList)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (item.IsDirectory || item.SizeBytes <= 0) continue;

            string key = GetCacheKey(item.FullPath, item.ModifiedTime.Ticks, sizeBucket);
            if (_cache.TryGetValue(key, out var cachedBmp))
            {
                item.ThumbnailImage = cachedBmp;
                continue;
            }

            tasks.Add(Task.Run(async () =>
            {
                if (cancellationToken.IsCancellationRequested) return;

                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    var bmp = await GetThumbnailAsync(item.FullPath, item.ModifiedTime, targetSize, cancellationToken);
                    if (bmp != null && !cancellationToken.IsCancellationRequested)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                item.ThumbnailImage = bmp;
                            }
                        }, DispatcherPriority.Background);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>
    /// Retrieves a single thumbnail from cache or generates it asynchronously.
    /// </summary>
    public async Task<Bitmap?> GetThumbnailAsync(string path, DateTime modifiedTime, int targetSize, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        int sizeBucket = GetSizeBucket(targetSize);
        string key = GetCacheKey(path, modifiedTime.Ticks, sizeBucket);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Bitmap? result = null;
        string ext = Path.GetExtension(path);

        // 1. Direct Image Provider
        if (DirectImageExtensions.Contains(ext))
        {
            result = await Task.Run(() => DecodeImageFile(path, sizeBucket), cancellationToken);
        }

        // 2. Windows Shell Provider (for Videos, PDFs, Documents, and other formats)
        if (result == null && OperatingSystem.IsWindows())
        {
            result = await Task.Run(() => ExtractWindowsShellThumbnail(path, sizeBucket), cancellationToken);
        }

        if (result != null)
        {
            AddCacheEntry(key, result);
        }

        return result;
    }

    private static int GetSizeBucket(int size)
    {
        if (size <= 96) return 96;
        if (size <= 160) return 160;
        if (size <= 256) return 256;
        return 384;
    }

    private static string GetCacheKey(string path, long ticks, int sizeBucket)
    {
        return $"{path}|{ticks}|{sizeBucket}";
    }

    private void AddCacheEntry(string key, Bitmap bmp)
    {
        if (_cache.TryAdd(key, bmp))
        {
            _lruQueue.Enqueue(key);
            if (_cache.Count > MaxCacheEntries)
            {
                if (_lruQueue.TryDequeue(out var oldestKey))
                {
                    _cache.TryRemove(oldestKey, out _);
                }
            }
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
        while (_lruQueue.TryDequeue(out _)) { }
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

    #region Windows Shell Thumbnail Extraction

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c0-d459e9f86333")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            [In, MarshalAs(UnmanagedType.Struct)] SIZE size,
            [In] SIIGBF flags,
            [Out] out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    private static readonly Guid IShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c0-d459e9f86333");

    private static Bitmap? ExtractWindowsShellThumbnail(string filePath, int targetSize)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, IShellItemImageFactoryGuid, out var factory);
            if (hr != 0 || factory == null) return null;

            var size = new SIZE(targetSize, targetSize);
            hr = factory.GetImage(size, SIIGBF.SIIGBF_BIGGERSIZEOK | SIIGBF.SIIGBF_RESIZETOFIT, out hBitmap);
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

    private static Bitmap? ConvertHBitmapToAvaloniaBitmap(IntPtr hBitmap)
    {
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();

            // Query bitmap dimensions
            if (GetDIBits(hdc, hBitmap, 0, 0, null!, ref bmi, 0) == 0) return null;

            int width = bmi.bmiHeader.biWidth;
            int height = Math.Abs(bmi.bmiHeader.biHeight);
            if (width <= 0 || height <= 0) return null;

            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB
            bmi.bmiHeader.biHeight = -height; // Top-down DIB

            byte[] pixelData = new byte[width * height * 4];
            int lines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixelData, ref bmi, 0);
            if (lines == 0) return null;

            // Create WriteableBitmap from raw 32bpp BGRA pixels
            var wbm = new WriteableBitmap(new Avalonia.PixelSize(width, height), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
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
        finally
        {
            if (hdc != IntPtr.Zero)
            {
                DeleteDC(hdc);
            }
        }
    }

    #endregion
}
