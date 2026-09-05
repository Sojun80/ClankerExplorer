using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace ClankerExplorer.Services;

/// <summary>
/// Hardware-accelerated video thumbnail and frame extraction service using Windows MediaComposition.
/// </summary>
public class VideoThumbnailService : IDisposable
{
    private static readonly Lazy<VideoThumbnailService> _instance = new(() => new VideoThumbnailService());
    public static VideoThumbnailService Instance => _instance.Value;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".flv", ".vob",
        ".ogv", ".ogg", ".drc", ".gifv", ".mng", ".asf", ".mts", ".m2ts", ".ts",
        ".qt", ".yuv", ".rm", ".rmvb", ".viv", ".amv", ".m4p", ".mpg", ".mp2",
        ".mpeg", ".mpe", ".mpv", ".m2v", ".svi", ".3gp", ".3g2", ".mxf", ".roq",
        ".nsv", ".f4v", ".f4p", ".f4a", ".f4b"
    };

    // Depth ratios used when cycling "New Thumbnail": 15%, 30%, 45%, 60%, 75%, 90%
    private static readonly double[] DepthRatios = new[] { 0.15, 0.30, 0.45, 0.60, 0.75, 0.90 };
    private readonly ConcurrentDictionary<string, int> _depthIndexByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeExtractionsByPath = new(StringComparer.OrdinalIgnoreCase);

    // Throttle concurrent video decodes so disk/GPU are not overwhelmed
    private readonly SemaphoreSlim _decodeThrottle = new(3, 3);
    private bool _isDisposed;

    public static bool IsVideoFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return VideoExtensions.Contains(ext);
    }

    private static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    /// <summary>
    /// Cancels any active thumbnail extraction work currently running for the specified file.
    /// </summary>
    public Task CancelWorkForFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Task.CompletedTask;
        try
        {
            string key = NormalizePath(filePath);
            if (_activeExtractionsByPath.TryGetValue(key, out var cts))
            {
                cts.Cancel();
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Parses timestamp strings formatted as "mm:ss", "hh:mm:ss", or raw seconds into a TimeSpan.
    /// </summary>
    public static bool TryParseTimestamp(string input, out TimeSpan timeSpan)
    {
        timeSpan = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string trimmed = input.Trim();

        // Standard TimeSpan parser
        if (TimeSpan.TryParseExact(trimmed, new[] { @"h\:m\:s", @"h\:mm\:ss", @"hh\:mm\:ss", @"m\:s", @"mm\:ss", @"%s" }, System.Globalization.CultureInfo.InvariantCulture, out timeSpan))
        {
            return true;
        }

        // Custom colon parsing
        var parts = trimmed.Split(':');
        if (parts.Length == 1 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double totalSeconds))
        {
            if (totalSeconds >= 0)
            {
                timeSpan = TimeSpan.FromSeconds(totalSeconds);
                return true;
            }
        }
        else if (parts.Length == 2 &&
                 int.TryParse(parts[0], out int minutes) &&
                 double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            if (minutes >= 0 && seconds >= 0 && seconds < 60)
            {
                timeSpan = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
                return true;
            }
        }
        else if (parts.Length == 3 &&
                 int.TryParse(parts[0], out int hours) &&
                 int.TryParse(parts[1], out int mins) &&
                 double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double secs))
        {
            if (hours >= 0 && mins >= 0 && mins < 60 && secs >= 0 && secs < 60)
            {
                timeSpan = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asynchronously retrieves the total duration of a video file.
    /// </summary>
    public async Task<TimeSpan> GetVideoDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath)) return TimeSpan.Zero;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string normalizedKey = NormalizePath(filePath);
        _activeExtractionsByPath[normalizedKey] = linkedCts;
        var token = linkedCts.Token;

        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(token).ConfigureAwait(false);
            var clip = await MediaClip.CreateFromFileAsync(storageFile).AsTask(token).ConfigureAwait(false);
            return clip.OriginalDuration;
        }
        catch
        {
            return TimeSpan.Zero;
        }
        finally
        {
            _activeExtractionsByPath.TryRemove(normalizedKey, out _);
        }
    }

    /// <summary>
    /// Extracts the next depth frame across the video duration (e.g. 15%, 30%, 45%, 60%, 75%, 90%).
    /// </summary>
    public async Task<Bitmap?> ExtractNextDepthFrameAsync(string filePath, int targetSize, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !File.Exists(filePath)) return null;

        var duration = await GetVideoDurationAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (duration <= TimeSpan.Zero)
        {
            duration = TimeSpan.FromSeconds(30);
        }

        int nextIndex = _depthIndexByPath.AddOrUpdate(filePath, 0, (_, current) => (current + 1) % DepthRatios.Length);
        double ratio = DepthRatios[nextIndex];
        var targetTime = TimeSpan.FromTicks((long)(duration.Ticks * ratio));

        return await ExtractFrameAtTimeAsync(filePath, targetTime, targetSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously extracts a video thumbnail at a specific timestamp.
    /// </summary>
    public async Task<Bitmap?> ExtractFrameAtTimeAsync(string filePath, TimeSpan timeOffset, int targetSize, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !OperatingSystem.IsWindows() || !File.Exists(filePath)) return null;

        if (targetSize <= 0) targetSize = 256;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string normalizedKey = NormalizePath(filePath);
        _activeExtractionsByPath[normalizedKey] = linkedCts;
        var token = linkedCts.Token;

        await _decodeThrottle.WaitAsync(token).ConfigureAwait(false);
        MediaComposition? composition = null;
        Windows.Storage.Streams.IRandomAccessStreamWithContentType? imageStream = null;
        try
        {
            if (token.IsCancellationRequested) return null;

            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(token).ConfigureAwait(false);
            var clip = await MediaClip.CreateFromFileAsync(storageFile).AsTask(token).ConfigureAwait(false);

            if (timeOffset < TimeSpan.Zero) timeOffset = TimeSpan.Zero;
            if (clip.OriginalDuration > TimeSpan.Zero && timeOffset > clip.OriginalDuration)
            {
                timeOffset = clip.OriginalDuration - TimeSpan.FromMilliseconds(100);
                if (timeOffset < TimeSpan.Zero) timeOffset = TimeSpan.Zero;
            }

            composition = new MediaComposition();
            composition.Clips.Add(clip);

            imageStream = await composition.GetThumbnailAsync(
                timeOffset,
                targetSize,
                0,
                VideoFramePrecision.NearestFrame).AsTask(token).ConfigureAwait(false);

            if (imageStream == null || imageStream.Size == 0) return null;

            using var netStream = imageStream.AsStreamForRead();
            using var mem = new MemoryStream();
            await netStream.CopyToAsync(mem, token).ConfigureAwait(false);
            mem.Position = 0;
            return new Bitmap(mem);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // Silent, headless fallback to Windows Shell Thumbnail Provider
            try
            {
                if (OperatingSystem.IsWindows() && !token.IsCancellationRequested)
                {
                    return ThumbnailService.ExtractWindowsShellThumbnail(filePath, targetSize, token);
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
        finally
        {
            imageStream?.Dispose();
            try { composition?.Clips.Clear(); } catch { }
            _activeExtractionsByPath.TryRemove(normalizedKey, out _);
            _decodeThrottle.Release();
        }
    }

    /// <summary>
    /// Fast frame extractor returning MemoryStream for video scrubbing and playback preview.
    /// </summary>
    public async Task<MemoryStream?> ExtractFrameDirectAsync(string filePath, TimeSpan timeOffset, int targetWidth, int targetHeight, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !OperatingSystem.IsWindows() || !File.Exists(filePath)) return null;

        if (targetWidth <= 0) targetWidth = 640;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string normalizedKey = NormalizePath(filePath);
        _activeExtractionsByPath[normalizedKey] = linkedCts;
        var token = linkedCts.Token;

        await _decodeThrottle.WaitAsync(token).ConfigureAwait(false);
        MediaComposition? composition = null;
        Windows.Storage.Streams.IRandomAccessStreamWithContentType? imageStream = null;
        try
        {
            if (token.IsCancellationRequested) return null;

            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(token).ConfigureAwait(false);
            var clip = await MediaClip.CreateFromFileAsync(storageFile).AsTask(token).ConfigureAwait(false);

            if (timeOffset < TimeSpan.Zero) timeOffset = TimeSpan.Zero;
            if (clip.OriginalDuration > TimeSpan.Zero && timeOffset > clip.OriginalDuration)
            {
                timeOffset = clip.OriginalDuration - TimeSpan.FromMilliseconds(100);
                if (timeOffset < TimeSpan.Zero) timeOffset = TimeSpan.Zero;
            }

            composition = new MediaComposition();
            composition.Clips.Add(clip);

            imageStream = await composition.GetThumbnailAsync(
                timeOffset,
                targetWidth,
                targetHeight,
                VideoFramePrecision.NearestFrame).AsTask(token).ConfigureAwait(false);

            if (imageStream == null || imageStream.Size == 0) return null;

            using var netStream = imageStream.AsStreamForRead();
            var mem = new MemoryStream();
            await netStream.CopyToAsync(mem, token).ConfigureAwait(false);
            mem.Position = 0;
            return mem;
        }
        catch
        {
            return null;
        }
        finally
        {
            imageStream?.Dispose();
            try { composition?.Clips.Clear(); } catch { }
            _activeExtractionsByPath.TryRemove(normalizedKey, out _);
            _decodeThrottle.Release();
        }
    }

    /// <summary>
    /// Asynchronously extracts the best representative video thumbnail using default offset (10% depth).
    /// </summary>
    public async Task<Bitmap?> ExtractSmartVideoThumbnailAsync(string filePath, int targetSize, CancellationToken cancellationToken = default)
    {
        return await ExtractFrameAtTimeAsync(filePath, TimeSpan.FromSeconds(2), targetSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scores a candidate frame based on luminance distribution, contrast/variance,
    /// edge detail density, and penalties for solid black/white/blank frames.
    /// </summary>
    public static double ScoreCandidateFrame(byte[] bgraPixels, int width, int height)
    {
        if (bgraPixels == null || bgraPixels.Length < 4 || width <= 0 || height <= 0) return double.MinValue;

        int totalPixels = width * height;
        int step = Math.Max(1, (int)Math.Sqrt(totalPixels / 4000.0));

        long sumLuminance = 0;
        long sumLuminanceSq = 0;
        long edgeDiffSum = 0;
        int blackCount = 0;
        int whiteCount = 0;
        int sampledCount = 0;

        int bytesPerPixel = 4;
        int rowBytes = width * bytesPerPixel;

        for (int y = 0; y < height - step; y += step)
        {
            int rowOffset = y * rowBytes;
            int nextRowOffset = (y + step) * rowBytes;

            for (int x = 0; x < width - step; x += step)
            {
                int offset = rowOffset + x * bytesPerPixel;
                if (offset + 2 >= bgraPixels.Length) break;

                int b = bgraPixels[offset];
                int g = bgraPixels[offset + 1];
                int r = bgraPixels[offset + 2];

                int lum = (29 * b + 150 * g + 77 * r) >> 8;

                sumLuminance += lum;
                sumLuminanceSq += (lum * lum);
                sampledCount++;

                if (lum < 18) blackCount++;
                else if (lum > 238) whiteCount++;

                int rightOffset = rowOffset + (x + step) * bytesPerPixel;
                int bottomOffset = nextRowOffset + x * bytesPerPixel;

                if (rightOffset + 2 < bgraPixels.Length)
                {
                    int lumRight = (29 * bgraPixels[rightOffset] + 150 * bgraPixels[rightOffset + 1] + 77 * bgraPixels[rightOffset + 2]) >> 8;
                    edgeDiffSum += Math.Abs(lum - lumRight);
                }

                if (bottomOffset + 2 < bgraPixels.Length)
                {
                    int lumBottom = (29 * bgraPixels[bottomOffset] + 150 * bgraPixels[bottomOffset + 1] + 77 * bgraPixels[bottomOffset + 2]) >> 8;
                    edgeDiffSum += Math.Abs(lum - lumBottom);
                }
            }
        }

        if (sampledCount == 0) return double.MinValue;

        double meanLuminance = (double)sumLuminance / sampledCount;
        double variance = ((double)sumLuminanceSq / sampledCount) - (meanLuminance * meanLuminance);
        double edgeDensity = (double)edgeDiffSum / sampledCount;
        double blackRatio = (double)blackCount / sampledCount;
        double whiteRatio = (double)whiteCount / sampledCount;

        double penalty = 0.0;
        if (blackRatio > 0.75) penalty += (blackRatio - 0.75) * 3000.0;
        if (whiteRatio > 0.75) penalty += (whiteRatio - 0.75) * 3000.0;
        if (variance < 25.0) penalty += (25.0 - variance) * 40.0;

        double brightnessPenalty = Math.Abs(meanLuminance - 120.0) * 0.4;
        double contrastReward = Math.Min(variance, 2000.0) * 0.4;
        double detailReward = edgeDensity * 3.0;

        return contrastReward + detailReward - penalty - brightnessPenalty;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _decodeThrottle.Dispose();
    }
}
