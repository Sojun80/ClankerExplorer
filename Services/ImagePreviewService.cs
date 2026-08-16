using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace ClankerExplorer.Services;

/// <summary>
/// Result of an image preview loading operation.
/// </summary>
public sealed class ImagePreviewResult
{
    public bool Success { get; init; }
    public Bitmap? Bitmap { get; init; }
    public int OriginalWidth { get; init; }
    public int OriginalHeight { get; init; }
    public string? ErrorMessage { get; init; }

    public string FormattedDimensions
    {
        get
        {
            if (OriginalWidth <= 0 || OriginalHeight <= 0) return string.Empty;
            double mp = (double)OriginalWidth * OriginalHeight / 1_000_000.0;
            return mp >= 0.1
                ? $"{OriginalWidth} × {OriginalHeight} ({mp:F1} MP)"
                : $"{OriginalWidth} × {OriginalHeight}";
        }
    }

    public static ImagePreviewResult Succeeded(Bitmap bitmap, int width, int height) =>
        new() { Success = true, Bitmap = bitmap, OriginalWidth = width, OriginalHeight = height };

    public static ImagePreviewResult Failed(string error) =>
        new() { Success = false, ErrorMessage = error };
}

/// <summary>
/// High-performance asynchronous image preview service.
/// Provides memory-bounded decoding, direct header dimension extraction, and LRU memory caching.
/// </summary>
public class ImagePreviewService
{
    private static readonly Lazy<ImagePreviewService> _instance = new(() => new ImagePreviewService());
    public static ImagePreviewService Instance => _instance.Value;

    private const int MaxPreviewDimension = 2560; // Max dimension to decode toward for large images
    private const long MaxMemoryCacheBytes = 48 * 1024 * 1024; // 48 MB memory cache limit

    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lruList = new();
    private long _currentCacheBytes;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".tif", ".ico"
    };

    public bool IsSupportedImageExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        return SupportedExtensions.Contains(extension);
    }

    /// <summary>
    /// Asynchronously loads and decodes an image preview with memory bounding and cancellation.
    /// </summary>
    public async Task<ImagePreviewResult> LoadImagePreviewAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return ImagePreviewResult.Failed("File not found");
        }

        string ext = Path.GetExtension(filePath);
        if (!IsSupportedImageExtension(ext))
        {
            return ImagePreviewResult.Failed($"Unsupported image format: {ext}");
        }

        long fileSize = 0;
        long lastWriteTicks = 0;
        try
        {
            var fi = new FileInfo(filePath);
            fileSize = fi.Length;
            lastWriteTicks = fi.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex)
        {
            return ImagePreviewResult.Failed($"Cannot access file: {ex.Message}");
        }

        if (fileSize == 0)
        {
            return ImagePreviewResult.Failed("File is empty");
        }

        string cacheKey = ComputeCacheKey(filePath, fileSize, lastWriteTicks);

        // Check memory cache first
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                _lruList.Remove(entry.Node);
                _lruList.AddFirst(entry.Node);
                return ImagePreviewResult.Succeeded(entry.Bitmap, entry.OriginalWidth, entry.OriginalHeight);
            }
        }

        // Decode on background thread
        return await Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                // 1. Try to read exact dimensions from the image header
                var (hdrWidth, hdrHeight) = TryReadHeaderDimensions(fileStream, ext);

                // If header parsing failed on a format that should be valid, or file has invalid magic header
                if (hdrWidth <= 0 && !IsValidImageMagic(fileStream, ext))
                {
                    return ImagePreviewResult.Failed("Corrupt or invalid image data");
                }

                fileStream.Seek(0, SeekOrigin.Begin);
                cancellationToken.ThrowIfCancellationRequested();

                // 2. Decode bitmap
                Bitmap bitmap;
                int originalWidth;
                int originalHeight;

                try
                {
                    bitmap = new Bitmap(fileStream);
                    originalWidth = hdrWidth > 0 ? hdrWidth : bitmap.PixelSize.Width;
                    originalHeight = hdrHeight > 0 ? hdrHeight : bitmap.PixelSize.Height;
                }
                catch (Exception ex)
                {
                    return ImagePreviewResult.Failed($"Corrupt or unreadable image: {ex.Message}");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 3. If image is enormously large (e.g. > 2560px), downsample to max preview dimension to conserve RAM
                if (originalWidth > MaxPreviewDimension || originalHeight > MaxPreviewDimension)
                {
                    try
                    {
                        fileStream.Seek(0, SeekOrigin.Begin);
                        Bitmap downscaled;
                        if (originalWidth >= originalHeight)
                        {
                            downscaled = Bitmap.DecodeToWidth(fileStream, MaxPreviewDimension, BitmapInterpolationMode.HighQuality);
                        }
                        else
                        {
                            downscaled = Bitmap.DecodeToHeight(fileStream, MaxPreviewDimension, BitmapInterpolationMode.HighQuality);
                        }

                        bitmap.Dispose();
                        bitmap = downscaled;
                    }
                    catch
                    {
                        // Keep original bitmap if downscale fails
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 4. Add to LRU cache
                AddCacheEntry(cacheKey, bitmap, originalWidth, originalHeight);

                return ImagePreviewResult.Succeeded(bitmap, originalWidth, originalHeight);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ImagePreviewResult.Failed($"Failed to decode image: {ex.Message}");
            }
        }, cancellationToken);
    }

    private static bool IsValidImageMagic(Stream stream, string ext)
    {
        try
        {
            if (stream.Length < 4) return false;
            stream.Seek(0, SeekOrigin.Begin);
            byte[] header = new byte[12];
            int read = stream.Read(header, 0, header.Length);
            if (read < 4) return false;

            // PNG
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return true;
            // JPEG
            if (header[0] == 0xFF && header[1] == 0xD8) return true;
            // BMP
            if (header[0] == 0x42 && header[1] == 0x4D) return true;
            // GIF
            if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46) return true;
            // WEBP
            if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                read >= 12 && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return true;
            // TIFF
            if ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
                (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A)) return true;
            // ICO
            if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static (int width, int height) TryReadHeaderDimensions(Stream stream, string ext)
    {
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            byte[] buffer = new byte[64];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read < 8) return (0, 0);

            // PNG: width at bytes 16..19, height at bytes 20..23 (Big Endian)
            if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47 && read >= 24)
            {
                int w = (buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19];
                int h = (buffer[20] << 24) | (buffer[21] << 16) | (buffer[22] << 8) | buffer[23];
                return (w, h);
            }

            // BMP: width at bytes 18..21, height at bytes 22..25 (Little Endian)
            if (buffer[0] == 0x42 && buffer[1] == 0x4D && read >= 26)
            {
                int w = buffer[18] | (buffer[19] << 8) | (buffer[20] << 16) | (buffer[21] << 24);
                int h = buffer[22] | (buffer[23] << 8) | (buffer[24] << 16) | (buffer[25] << 24);
                return (Math.Abs(w), Math.Abs(h));
            }

            // GIF: width at bytes 6..7, height at bytes 8..9 (Little Endian)
            if (buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46 && read >= 10)
            {
                int w = buffer[6] | (buffer[7] << 8);
                int h = buffer[8] | (buffer[9] << 8);
                return (w, h);
            }

            // JPEG: scan markers for SOF0/SOF2
            if (buffer[0] == 0xFF && buffer[1] == 0xD8)
            {
                stream.Seek(2, SeekOrigin.Begin);
                using var reader = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
                while (stream.Position < stream.Length - 8)
                {
                    byte markerPrefix = reader.ReadByte();
                    if (markerPrefix != 0xFF) continue;

                    byte marker = reader.ReadByte();
                    if (marker == 0xD9 || marker == 0xDA) break; // End of image or start of scan

                    int length = (reader.ReadByte() << 8) | reader.ReadByte();
                    if (length < 2) break;

                    // SOF markers: C0, C1, C2, C3, C5, C6, C7, C9, CA, CB, CD, CE, CF
                    if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) ||
                        (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
                    {
                        reader.ReadByte(); // precision
                        int h = (reader.ReadByte() << 8) | reader.ReadByte();
                        int w = (reader.ReadByte() << 8) | reader.ReadByte();
                        return (w, h);
                    }

                    stream.Seek(length - 2, SeekOrigin.Current);
                }
            }

            // WEBP
            if (buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 && read >= 30 &&
                buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50)
            {
                // VP8 (lossy)
                if (buffer[12] == 0x56 && buffer[13] == 0x50 && buffer[14] == 0x38 && buffer[15] == 0x20 && read >= 30)
                {
                    int w = (buffer[26] | (buffer[27] << 8)) & 0x3FFF;
                    int h = (buffer[28] | (buffer[29] << 8)) & 0x3FFF;
                    return (w, h);
                }
                // VP8L (lossless)
                if (buffer[12] == 0x56 && buffer[13] == 0x50 && buffer[14] == 0x38 && buffer[15] == 0x4C && read >= 25)
                {
                    int b0 = buffer[21], b1 = buffer[22], b2 = buffer[23], b3 = buffer[24];
                    int w = 1 + (((b1 & 0x3F) << 8) | b0);
                    int h = 1 + (((b3 & 0xF) << 10) | (b2 << 2) | ((b1 & 0xC0) >> 6));
                    return (w, h);
                }
                // VP8X (extended)
                if (buffer[12] == 0x56 && buffer[13] == 0x50 && buffer[14] == 0x38 && buffer[15] == 0x58 && read >= 30)
                {
                    int w = 1 + (buffer[24] | (buffer[25] << 8) | (buffer[26] << 16));
                    int h = 1 + (buffer[27] | (buffer[28] << 8) | (buffer[29] << 16));
                    return (w, h);
                }
            }
        }
        catch
        {
            // Fall back to bitmap decoding
        }

        return (0, 0);
    }

    private void AddCacheEntry(string key, Bitmap bitmap, int width, int height)
    {
        long approxBytes = Math.Max(1, (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
        lock (_cacheGate)
        {
            if (_cache.ContainsKey(key)) return;

            var node = _lruList.AddFirst(key);
            _cache[key] = new CacheEntry(bitmap, width, height, approxBytes, node);
            _currentCacheBytes += approxBytes;

            // Evict oldest entries if exceeding cache limit
            while (_currentCacheBytes > MaxMemoryCacheBytes && _lruList.Last is { } oldest)
            {
                _lruList.RemoveLast();
                if (_cache.Remove(oldest.Value, out var removed))
                {
                    _currentCacheBytes -= removed.ApproximateBytes;
                }
            }
        }
    }

    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
            _lruList.Clear();
            _currentCacheBytes = 0;
        }
    }

    private static string ComputeCacheKey(string path, long size, long ticks)
    {
        string normalized = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        byte[] input = Encoding.UTF8.GetBytes($"{normalized}|{size}|{ticks}");
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private sealed record CacheEntry(
        Bitmap Bitmap,
        int OriginalWidth,
        int OriginalHeight,
        long ApproximateBytes,
        LinkedListNode<string> Node);
}
