using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClankerExplorer.Services;

/// <summary>
/// Intelligent video thumbnail extraction service.
/// Samples candidate frames across the video duration, evaluates them using
/// luminance, contrast, detail density, and blank/black/white penalties, and
/// returns the most representative frame.
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

    // Candidate seek percentage ratios: 10%, 25%, 40%, 55%, 70%
    private static readonly double[] CandidateRatios = new[] { 0.10, 0.25, 0.40, 0.55, 0.70 };

    // Depth ratios used when cycling "New Thumbnail": 15%, 30%, 45%, 60%, 75%, 90%
    private static readonly double[] DepthRatios = new[] { 0.15, 0.30, 0.45, 0.60, 0.75, 0.90 };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _depthIndexByPath = new(StringComparer.OrdinalIgnoreCase);

    // Throttle concurrent video decodes so large folders do not overwhelm CPU / disk / hardware decoders
    private readonly SemaphoreSlim _decodeThrottle = new(2, 2);
    private bool _mfInitialized;
    private bool _isDisposed;

    public VideoThumbnailService()
    {
        InitializeMediaFoundation();
    }

    public static bool IsVideoFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return VideoExtensions.Contains(ext);
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
        if (!OperatingSystem.IsWindows() || !_mfInitialized || !File.Exists(filePath)) return TimeSpan.Zero;

        await _decodeThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                IMFSourceReader? reader = null;
                try
                {
                    int hr = MFCreateSourceReaderFromURL(filePath, null, out reader);
                    if (hr != 0 || reader == null) return TimeSpan.Zero;

                    var durationVar = new PROPVARIANT();
                    if (reader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION, out durationVar) == 0)
                    {
                        long ticks = (long)durationVar.uhVal;
                        if (ticks > 0) return TimeSpan.FromTicks(ticks);
                    }
                }
                catch { }
                finally
                {
                    if (reader != null) { try { Marshal.ReleaseComObject(reader); } catch { } }
                }
                return TimeSpan.Zero;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return TimeSpan.Zero;
        }
        finally
        {
            _decodeThrottle.Release();
        }
    }

    /// <summary>
    /// Extracts the next depth frame across the video duration (e.g. 15%, 30%, 45%, 60%, 75%, 90%).
    /// </summary>
    public async Task<Bitmap?> ExtractNextDepthFrameAsync(string filePath, int targetSize, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !File.Exists(filePath)) return null;

        var duration = await GetVideoDurationAsync(filePath, cancellationToken);
        if (duration <= TimeSpan.Zero)
        {
            duration = TimeSpan.FromSeconds(30);
        }

        int nextIndex = _depthIndexByPath.AddOrUpdate(filePath, 0, (_, current) => (current + 1) % DepthRatios.Length);
        double ratio = DepthRatios[nextIndex];
        var targetTime = TimeSpan.FromTicks((long)(duration.Ticks * ratio));

        return await ExtractFrameAtTimeAsync(filePath, targetTime, targetSize, cancellationToken);
    }

    /// <summary>
    /// Asynchronously extracts a video thumbnail at a specific timestamp.
    /// </summary>
    public async Task<Bitmap?> ExtractFrameAtTimeAsync(string filePath, TimeSpan timeOffset, int targetSize, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !File.Exists(filePath)) return null;

        await _decodeThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cancellationToken.IsCancellationRequested) return null;

            return await Task.Run(() => ExtractSingleFrame(filePath, timeOffset.Ticks, targetSize, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _decodeThrottle.Release();
        }
    }

    private Bitmap? ExtractSingleFrame(string filePath, long seekTicks, int targetSize, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !_mfInitialized) return null;

        IMFSourceReader? reader = null;
        IMFAttributes? attributes = null;
        IMFMediaType? mediaType = null;

        try
        {
            int hr = MFCreateAttributes(out attributes, 1);
            if (hr == 0 && attributes != null)
            {
                attributes.SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
            }

            hr = MFCreateSourceReaderFromURL(filePath, attributes, out reader);
            if (hr != 0 || reader == null) return null;

            hr = MFCreateMediaType(out mediaType);
            if (hr == 0 && mediaType != null)
            {
                mediaType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                mediaType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, mediaType);
            }

            reader.SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

            var propVar = new PROPVARIANT { vt = 20 /* VT_I8 */, hVal = seekTicks };
            try
            {
                reader.SetCurrentPosition(Guid.Empty, ref propVar);
            }
            catch { }

            int readHr = reader.ReadSample(
                MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                0,
                out _,
                out _,
                out _,
                out IMFSample sample);

            if (readHr == 0 && sample != null)
            {
                try
                {
                    var candidate = ExtractCandidateFrame(reader, sample);
                    if (candidate != null)
                    {
                        return CreateBitmapFromCandidate(candidate.Value, targetSize);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }
        }
        catch { }
        finally
        {
            if (mediaType != null) { try { Marshal.ReleaseComObject(mediaType); } catch { } }
            if (attributes != null) { try { Marshal.ReleaseComObject(attributes); } catch { } }
            if (reader != null) { try { Marshal.ReleaseComObject(reader); } catch { } }
        }

        return null;
    }

    private void InitializeMediaFoundation()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            int hr = MFStartup(0x00020070, 1); // MF_VERSION, MFSTARTUP_NOSOCKET
            _mfInitialized = (hr == 0);
        }
        catch
        {
            _mfInitialized = false;
        }
    }

    /// <summary>
    /// Asynchronously extracts the best representative video thumbnail using smart candidate scoring.
    /// </summary>
    public async Task<Bitmap?> ExtractSmartVideoThumbnailAsync(string filePath, int targetSize, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !File.Exists(filePath)) return null;

        await _decodeThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cancellationToken.IsCancellationRequested) return null;

            return await Task.Run(() => ExtractBestFrame(filePath, targetSize, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _decodeThrottle.Release();
        }
    }

    private Bitmap? ExtractBestFrame(string filePath, int targetSize, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !_mfInitialized) return null;

        IMFSourceReader? reader = null;
        IMFAttributes? attributes = null;
        IMFMediaType? mediaType = null;

        try
        {
            // Configure source reader with video processing enabled for automatic RGB32 conversion
            int hr = MFCreateAttributes(out attributes, 1);
            if (hr == 0 && attributes != null)
            {
                attributes.SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
            }

            hr = MFCreateSourceReaderFromURL(filePath, attributes, out reader);
            if (hr != 0 || reader == null) return null;

            // Configure output media type as RGB32
            hr = MFCreateMediaType(out mediaType);
            if (hr == 0 && mediaType != null)
            {
                mediaType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                mediaType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, mediaType);
            }

            reader.SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

            // Query duration in 100-nanosecond units (ticks)
            long durationTicks = 0;
            try
            {
                var durationVar = new PROPVARIANT();
                if (reader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION, out durationVar) == 0)
                {
                    durationTicks = (long)durationVar.uhVal;
                }
            }
            catch { }

            // If duration unknown or 0, assume default 30 seconds
            if (durationTicks <= 0)
            {
                durationTicks = 30L * TimeSpan.TicksPerSecond;
            }

            CandidateFrame? bestCandidate = null;
            double bestScore = double.MinValue;

            // Sample candidate timestamps
            foreach (double ratio in CandidateRatios)
            {
                if (cancellationToken.IsCancellationRequested) break;

                long seekTicks = (long)(durationTicks * ratio);
                var propVar = new PROPVARIANT { vt = 20 /* VT_I8 */, hVal = seekTicks };

                try
                {
                    reader.SetCurrentPosition(Guid.Empty, ref propVar);
                }
                catch { }

                // Read sample
                int readHr = reader.ReadSample(
                    MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0,
                    out uint actualStreamIndex,
                    out uint streamFlags,
                    out long timestamp,
                    out IMFSample sample);

                if (readHr == 0 && sample != null)
                {
                    try
                    {
                        var candidate = ExtractCandidateFrame(reader, sample);
                        if (candidate != null)
                        {
                            double score = ScoreCandidateFrame(candidate.Value.Pixels, candidate.Value.Width, candidate.Value.Height);
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestCandidate = candidate;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                }
            }

            if (bestCandidate.HasValue)
            {
                return CreateBitmapFromCandidate(bestCandidate.Value, targetSize);
            }
        }
        catch
        {
            // Return null so caller falls back gracefully
        }
        finally
        {
            if (mediaType != null) { try { Marshal.ReleaseComObject(mediaType); } catch { } }
            if (attributes != null) { try { Marshal.ReleaseComObject(attributes); } catch { } }
            if (reader != null) { try { Marshal.ReleaseComObject(reader); } catch { } }
        }

        return null;
    }

    private readonly struct CandidateFrame
    {
        public readonly byte[] Pixels;
        public readonly int Width;
        public readonly int Height;

        public CandidateFrame(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }
    }

    private static CandidateFrame? ExtractCandidateFrame(IMFSourceReader reader, IMFSample sample)
    {
        IMFMediaBuffer? buffer = null;
        IMFMediaType? currentType = null;
        try
        {
            int hr = sample.ConvertToContiguousBuffer(out buffer);
            if (hr != 0 || buffer == null) return null;

            hr = buffer.Lock(out IntPtr pData, out uint maxLen, out uint currentLen);
            if (hr != 0 || pData == IntPtr.Zero || currentLen == 0) return null;

            try
            {
                int width = 0;
                int height = 0;

                hr = reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, out currentType);
                if (hr == 0 && currentType != null)
                {
                    hr = currentType.GetUINT64(MF_MT_FRAME_SIZE, out ulong frameSize);
                    if (hr == 0)
                    {
                        width = (int)(frameSize >> 32);
                        height = (int)(frameSize & 0xFFFFFFFF);
                    }
                }

                if (width <= 0 || height <= 0)
                {
                    // Fallback to 16:9 estimation from byte length
                    int pixelCount = (int)(currentLen / 4);
                    if (pixelCount > 0)
                    {
                        width = (int)Math.Sqrt(pixelCount * (16.0 / 9.0));
                        height = pixelCount / Math.Max(1, width);
                    }
                }

                if (width <= 0 || height <= 0) return null;

                int byteCount = Math.Min((int)currentLen, width * height * 4);
                byte[] pixelData = new byte[byteCount];
                Marshal.Copy(pData, pixelData, 0, byteCount);

                return new CandidateFrame(pixelData, width, height);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                if (currentType != null) { try { Marshal.ReleaseComObject(currentType); } catch { } }
                if (buffer != null) { try { Marshal.ReleaseComObject(buffer); } catch { } }
            }
        }
    }

    /// <summary>
    /// Scores a candidate frame based on luminance distribution, contrast/variance,
    /// edge detail density, and penalties for solid black/white/blank frames.
    /// </summary>
    public static double ScoreCandidateFrame(byte[] bgraPixels, int width, int height)
    {
        if (bgraPixels == null || bgraPixels.Length < 4 || width <= 0 || height <= 0) return double.MinValue;

        int totalPixels = width * height;
        // Step stride to keep candidate evaluation under 0.5ms even for 1080p/4K frames
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

                // Fast integer luminance: (29*B + 150*G + 77*R) >> 8
                int lum = (29 * b + 150 * g + 77 * r) >> 8;

                sumLuminance += lum;
                sumLuminanceSq += (lum * lum);
                sampledCount++;

                if (lum < 18) blackCount++;
                else if (lum > 238) whiteCount++;

                // Spatial edge detail difference with right and bottom neighbors
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

        // Heavy penalty for frames that are mostly blank black, white, or low contrast
        double penalty = 0.0;
        if (blackRatio > 0.75) penalty += (blackRatio - 0.75) * 3000.0;
        if (whiteRatio > 0.75) penalty += (whiteRatio - 0.75) * 3000.0;
        if (variance < 25.0) penalty += (25.0 - variance) * 40.0;

        // Penalty for extreme brightness deviation from ideal midtones (~120)
        double brightnessPenalty = Math.Abs(meanLuminance - 120.0) * 0.4;

        // Rewards for healthy contrast and rich detail/edges
        double contrastReward = Math.Min(variance, 2000.0) * 0.4;
        double detailReward = edgeDensity * 3.0;

        return contrastReward + detailReward - penalty - brightnessPenalty;
    }

    private static Bitmap CreateBitmapFromCandidate(CandidateFrame frame, int targetSize)
    {
        int srcW = frame.Width;
        int srcH = frame.Height;

        // If frame is already around or smaller than targetSize, load directly
        if (srcW <= targetSize && srcH <= targetSize)
        {
            var direct = new WriteableBitmap(
                new PixelSize(srcW, srcH),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var fb = direct.Lock())
            {
                int copyLen = Math.Min(frame.Pixels.Length, srcW * srcH * 4);
                Marshal.Copy(frame.Pixels, 0, fb.Address, copyLen);
            }
            return direct;
        }

        // Crisp bilinear/box downsampling to target thumbnail dimensions preserving aspect ratio
        double scale = Math.Min((double)targetSize / srcW, (double)targetSize / srcH);
        int dstW = Math.Max(1, (int)(srcW * scale));
        int dstH = Math.Max(1, (int)(srcH * scale));

        byte[] dstPixels = new byte[dstW * dstH * 4];
        double scaleX = (double)srcW / dstW;
        double scaleY = (double)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            int sy = Math.Clamp((int)(dy * scaleY), 0, srcH - 1);
            int srcRowOffset = sy * srcW * 4;
            int dstRowOffset = dy * dstW * 4;

            for (int dx = 0; dx < dstW; dx++)
            {
                int sx = Math.Clamp((int)(dx * scaleX), 0, srcW - 1);
                int srcOffset = srcRowOffset + sx * 4;
                int dstOffset = dstRowOffset + dx * 4;

                if (srcOffset + 3 < frame.Pixels.Length && dstOffset + 3 < dstPixels.Length)
                {
                    dstPixels[dstOffset] = frame.Pixels[srcOffset];         // B
                    dstPixels[dstOffset + 1] = frame.Pixels[srcOffset + 1]; // G
                    dstPixels[dstOffset + 2] = frame.Pixels[srcOffset + 2]; // R
                    dstPixels[dstOffset + 3] = 255;                         // A
                }
            }
        }

        var wbm = new WriteableBitmap(
            new PixelSize(dstW, dstH),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = wbm.Lock())
        {
            Marshal.Copy(dstPixels, 0, fb.Address, dstPixels.Length);
        }
        return wbm;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_mfInitialized && OperatingSystem.IsWindows())
        {
            try { MFShutdown(); } catch { }
            _mfInitialized = false;
        }

        _decodeThrottle.Dispose();
    }

    #region Media Foundation Native Declarations

    private const uint MF_SOURCE_READER_MEDIASOURCE = 0xffffffff;
    private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xfffffffc;

    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("cf0b5d04-54f4-4361-8b9f-44b446ec4670");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f829-4679-a7e0-4924f7f40717");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-4296-440b-8386-cc4a00c20f5d");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4b68-8a07-ac3838e9302e");
    private static readonly Guid MF_PD_DURATION = new("6c990a77-ac62-4758-8316-89de900944f2");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint Version, uint dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType([Out] out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes([Out] out IMFAttributes ppMFAttributes, uint cInitialSize);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        [In] IMFAttributes? pAttributes,
        [Out] out IMFSourceReader ppSourceReader);

    [ComImport, Guid("70ae66f2-c809-4e4f-aa91-40c211047fde"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        void GetStreamSelection([In] uint dwStreamIndex, [Out, MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
        void SetStreamSelection([In] uint dwStreamIndex, [In, MarshalAs(UnmanagedType.Bool)] bool fSelected);
        void GetNativeMediaType([In] uint dwStreamIndex, [In] uint dwMediaTypeIndex, [Out] out IMFMediaType ppMediaType);
        int GetCurrentMediaType([In] uint dwStreamIndex, [Out] out IMFMediaType ppMediaType);
        void SetCurrentMediaType([In] uint dwStreamIndex, [In] IntPtr pdwReserved, [In] IMFMediaType pMediaType);
        void SetCurrentPosition([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidTimeFormat, [In] ref PROPVARIANT varPosition);
        int ReadSample([In] uint dwStreamIndex, [In] uint dwControlFlags, [Out] out uint pdwActualStreamIndex, [Out] out uint pdwStreamFlags, [Out] out long pllTimestamp, [Out] out IMFSample ppSample);
        void Flush([In] uint dwStreamIndex);
        void GetServiceForStream([In] uint dwStreamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidService, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppvObject);
        int GetPresentationAttribute([In] uint dwStreamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidAttribute, [Out] out PROPVARIANT pvarAttribute);
    }

    [ComImport, Guid("44ae3870-d286-4e50-a8fe-ebde8f397373"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        void GetItem();
        void GetItemType();
        void CompareItem();
        void Compare();
        void GetUINT32();
        int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out ulong punValue);
        void GetDouble();
        void GetGUID();
        void GetStringLength();
        void GetString();
        void GetAllocatedString();
        void GetBlobSize();
        void GetBlob();
        void GetAllocatedBlob();
        void GetUnknown();
        void SetItem();
        void DeleteItem();
        void DeleteAllItems();
        void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] uint unValue);
        void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] ulong unValue);
        void SetDouble();
        void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
        void SetString();
        void SetBlob();
        void SetUnknown();
        void LockStore();
        void UnlockStore();
        void GetCount();
        void GetItemByIndex();
        void CopyAllItems();
        void GetMajorType();
        void IsCompressedFormat();
        void IsEqual();
        void GetRepresentation();
        void FreeRepresentation();
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4ad570c600a3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        void GetItem();
        void GetItemType();
        void CompareItem();
        void Compare();
        void GetUINT32();
        void GetUINT64();
        void GetDouble();
        void GetGUID();
        void GetStringLength();
        void GetString();
        void GetAllocatedString();
        void GetBlobSize();
        void GetBlob();
        void GetAllocatedBlob();
        void GetUnknown();
        void SetItem();
        void DeleteItem();
        void DeleteAllItems();
        void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] uint unValue);
        void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] ulong unValue);
        void SetDouble();
        void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
        void SetString();
        void SetBlob();
        void SetUnknown();
        void LockStore();
        void UnlockStore();
        void GetCount();
        void GetItemByIndex();
        void CopyAllItems();
    }

    [ComImport, Guid("c40a0074-b928-4246-9864-0ec4487f6004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        void GetItem();
        void GetItemType();
        void CompareItem();
        void Compare();
        void GetUINT32();
        void GetUINT64();
        void GetDouble();
        void GetGUID();
        void GetStringLength();
        void GetString();
        void GetAllocatedString();
        void GetBlobSize();
        void GetBlob();
        void GetAllocatedBlob();
        void GetUnknown();
        void SetItem();
        void DeleteItem();
        void DeleteAllItems();
        void SetUINT32();
        void SetUINT64();
        void SetDouble();
        void SetGUID();
        void SetString();
        void SetBlob();
        void SetUnknown();
        void LockStore();
        void UnlockStore();
        void GetCount();
        void GetItemByIndex();
        void CopyAllItems();
        void GetSampleFlags();
        void SetSampleFlags();
        void GetSampleTime();
        void SetSampleTime();
        void GetSampleDuration();
        void SetSampleDuration();
        void GetBufferCount();
        void GetBufferByIndex();
        int ConvertToContiguousBuffer([Out] out IMFMediaBuffer ppBuffer);
        void AddBuffer();
        void RemoveBufferByIndex();
        void RemoveAllBuffers();
        void GetTotalLength();
        void CopyToBuffer();
    }

    [ComImport, Guid("045355b5-855e-479a-9c5b-8e284f934ef4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        int Lock([Out] out IntPtr ppbBuffer, [Out] out uint pcbMaxLength, [Out] out uint pcbCurrentLength);
        int Unlock();
        void GetCurrentLength();
        void SetCurrentLength();
        void GetMaxLength();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public long hVal;
        [FieldOffset(8)] public ulong uhVal;
        [FieldOffset(8)] public IntPtr ptrVal;
    }

    #endregion
}
