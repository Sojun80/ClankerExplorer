using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClankerExplorer.Services.Preview;

public sealed class PdfInfo
{
    public uint PageCount { get; init; }
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// High-performance asynchronous PDF document and page rendering service using Windows.Data.Pdf.
/// </summary>
public class PdfPreviewService
{
    private static readonly Lazy<PdfPreviewService> _instance = new(() => new PdfPreviewService());
    public static PdfPreviewService Instance => _instance.Value;

    private const long MaxMemoryCacheBytes = 32 * 1024 * 1024; // 32MB page cache
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, (Bitmap Bitmap, long Bytes)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private long _currentCacheBytes;

    public bool IsPdfFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        return string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asynchronously retrieves basic document info (such as total page count).
    /// </summary>
    public async Task<PdfInfo> GetPdfInfoAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return new PdfInfo { IsValid = false, ErrorMessage = "File not found" };
        }

        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken).ConfigureAwait(false);
            var doc = await PdfDocument.LoadFromFileAsync(storageFile).AsTask(cancellationToken).ConfigureAwait(false);
            return new PdfInfo
            {
                IsValid = true,
                PageCount = doc.PageCount
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PdfInfo { IsValid = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Asynchronously renders a single PDF page at a target pixel width.
    /// </summary>
    public async Task<Bitmap?> RenderPageAsync(string filePath, uint pageIndex, int targetWidth, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

        if (targetWidth <= 0) targetWidth = 1000;
        string cacheKey = $"{filePath}|{pageIndex}|{targetWidth}";

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                _lru.Remove(cacheKey);
                _lru.AddFirst(cacheKey);
                return entry.Bitmap;
            }
        }

        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken).ConfigureAwait(false);
            var doc = await PdfDocument.LoadFromFileAsync(storageFile).AsTask(cancellationToken).ConfigureAwait(false);
            if (pageIndex >= doc.PageCount) pageIndex = 0;

            using var page = doc.GetPage(pageIndex);
            using var stream = new InMemoryRandomAccessStream();

            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Clamp(targetWidth, 100, 3840)
            };

            await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken).ConfigureAwait(false);

            using var netStream = stream.AsStreamForRead();
            using var mem = new MemoryStream();
            await netStream.CopyToAsync(mem, cancellationToken).ConfigureAwait(false);
            mem.Position = 0;

            var bitmap = new Bitmap(mem);

            // Add to LRU cache
            long estBytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
            lock (_cacheGate)
            {
                while (_currentCacheBytes + estBytes > MaxMemoryCacheBytes && _lru.Last != null)
                {
                    string oldKey = _lru.Last.Value;
                    _lru.RemoveLast();
                    if (_cache.Remove(oldKey, out var removed))
                    {
                        _currentCacheBytes -= removed.Bytes;
                    }
                }

                _cache[cacheKey] = (bitmap, estBytes);
                _lru.AddFirst(cacheKey);
                _currentCacheBytes += estBytes;
            }

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
