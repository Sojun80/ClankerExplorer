using System;
using System.Collections.Concurrent;
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
/// Intelligent video thumbnail extraction service using Windows Media Foundation
/// and Windows Shell Property System.
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
    private readonly ConcurrentDictionary<string, int> _depthIndexByPath = new(StringComparer.OrdinalIgnoreCase);

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
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath)) return TimeSpan.Zero;

        return await Task.Run(() =>
        {
            try
            {
                // Primary: Windows Shell Property System (PKEY_Media_Duration)
                Guid shellItem2Guid = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
                if (SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref shellItem2Guid, out IntPtr shellItem2Ptr) == 0 && shellItem2Ptr != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr vtable = Marshal.ReadIntPtr(shellItem2Ptr);
                        var getUInt64 = Marshal.GetDelegateForFunctionPointer<GetUInt64Delegate>(Marshal.ReadIntPtr(vtable, 17 * IntPtr.Size));
                        PROPERTYKEY pkeyDuration = new() { fmtid = new Guid("64440490-4c8b-11d1-8b70-080036b11a03"), pid = 3 };

                        if (getUInt64(shellItem2Ptr, ref pkeyDuration, out ulong duration100ns) == 0 && duration100ns > 0)
                        {
                            return TimeSpan.FromTicks((long)duration100ns);
                        }
                    }
                    finally
                    {
                        var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(shellItem2Ptr), 2 * IntPtr.Size));
                        release(shellItem2Ptr);
                    }
                }
            }
            catch { }
            return TimeSpan.Zero;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves native video dimensions (width, height) using Windows Shell Properties.
    /// </summary>
    public static (int Width, int Height) GetVideoDimensions(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath)) return (1920, 1080);

        try
        {
            Guid shellItem2Guid = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
            if (SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref shellItem2Guid, out IntPtr shellItem2Ptr) == 0 && shellItem2Ptr != IntPtr.Zero)
            {
                try
                {
                    IntPtr vtable = Marshal.ReadIntPtr(shellItem2Ptr);
                    var getUInt32 = Marshal.GetDelegateForFunctionPointer<GetUInt32Delegate>(Marshal.ReadIntPtr(vtable, 16 * IntPtr.Size));
                    PROPERTYKEY pkeyW = new() { fmtid = new Guid("64440490-4c8b-11d1-8b70-080036b11a03"), pid = 4 };
                    PROPERTYKEY pkeyH = new() { fmtid = new Guid("64440490-4c8b-11d1-8b70-080036b11a03"), pid = 5 };

                    getUInt32(shellItem2Ptr, ref pkeyW, out uint w);
                    getUInt32(shellItem2Ptr, ref pkeyH, out uint h);

                    if (w > 0 && h > 0) return ((int)w, (int)h);
                }
                finally
                {
                    var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(shellItem2Ptr), 2 * IntPtr.Size));
                    release(shellItem2Ptr);
                }
            }
        }
        catch { }

        return (1920, 1080);
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

            var duration = await GetVideoDurationAsync(filePath, cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => ExtractBestFrame(filePath, duration, targetSize, cancellationToken), cancellationToken).ConfigureAwait(false);
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

        CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);

        IntPtr readerPtr = IntPtr.Zero;
        IntPtr attrPtr = IntPtr.Zero;
        IntPtr mediaTypePtr = IntPtr.Zero;

        try
        {
            int hr = MFCreateAttributes(out attrPtr, 1);
            if (hr == 0 && attrPtr != IntPtr.Zero)
            {
                IntPtr attrVtbl = Marshal.ReadIntPtr(attrPtr);
                var setUint32 = Marshal.GetDelegateForFunctionPointer<SetUINT32Delegate>(Marshal.ReadIntPtr(attrVtbl, 21 * IntPtr.Size));
                Guid enableVideoProcGuid = new("fb394f3d-ccf1-42ee-bb0c-857852e0e6d6");
                setUint32(attrPtr, ref enableVideoProcGuid, 1);
            }

            hr = MFCreateSourceReaderFromURL(filePath, attrPtr, out readerPtr);
            if (hr != 0 || readerPtr == IntPtr.Zero) return null;

            IntPtr readerVtbl = Marshal.ReadIntPtr(readerPtr);
            var setMediaType = Marshal.GetDelegateForFunctionPointer<SetCurrentMediaTypeDelegate>(Marshal.ReadIntPtr(readerVtbl, 7 * IntPtr.Size));
            var setPosition = Marshal.GetDelegateForFunctionPointer<SetCurrentPositionDelegate>(Marshal.ReadIntPtr(readerVtbl, 8 * IntPtr.Size));
            var readSample = Marshal.GetDelegateForFunctionPointer<ReadSampleDelegate>(Marshal.ReadIntPtr(readerVtbl, 9 * IntPtr.Size));
            var setStreamSelection = Marshal.GetDelegateForFunctionPointer<SetStreamSelectionDelegate>(Marshal.ReadIntPtr(readerVtbl, 4 * IntPtr.Size));

            // Create RGB32 media type
            hr = MFCreateMediaType(out mediaTypePtr);
            if (hr == 0 && mediaTypePtr != IntPtr.Zero)
            {
                IntPtr typeVtbl = Marshal.ReadIntPtr(mediaTypePtr);
                var setGuid = Marshal.GetDelegateForFunctionPointer<SetGUIDDelegate>(Marshal.ReadIntPtr(typeVtbl, 24 * IntPtr.Size));

                Guid mfMtMajorType = new("48eba18e-f829-4679-a7e0-4924f7f40717");
                Guid mfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
                Guid mfMtSubType = new("f7e34c9a-4296-440b-8386-cc4a00c20f5d");
                Guid mfVideoFormatRGB32 = new("00000016-0000-0010-8000-00aa00389b71");

                setGuid(mediaTypePtr, ref mfMtMajorType, ref mfMediaTypeVideo);
                setGuid(mediaTypePtr, ref mfMtSubType, ref mfVideoFormatRGB32);

                setMediaType(readerPtr, 0xfffffffc, IntPtr.Zero, mediaTypePtr);
            }

            setStreamSelection(readerPtr, 0xfffffffc, true);

            // Seek to target timestamp
            var propVar = new PROPVARIANT { vt = 20 /* VT_I8 */, hVal = seekTicks };
            Guid timeFormat = Guid.Empty;
            try
            {
                setPosition(readerPtr, ref timeFormat, ref propVar);
            }
            catch { }

            IntPtr samplePtr = IntPtr.Zero;
            for (int retry = 0; retry < 30; retry++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int readHr = readSample(readerPtr, 0xfffffffc, 0, out _, out uint streamFlags, out _, out samplePtr);
                if (readHr != 0 || (streamFlags & 0x00000200 /* MF_SOURCE_READERF_ENDOFSTREAM */) != 0) break;
                if (samplePtr != IntPtr.Zero) break;
            }

            if (samplePtr != IntPtr.Zero)
            {
                try
                {
                    var candidate = ExtractCandidateFrameDirect(readerPtr, samplePtr, filePath);
                    if (candidate != null)
                    {
                        return CreateBitmapFromCandidate(candidate.Value, targetSize);
                    }
                }
                finally
                {
                    var sampleRel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(samplePtr), 2 * IntPtr.Size));
                    sampleRel(samplePtr);
                }
            }
        }
        catch { }
        finally
        {
            if (mediaTypePtr != IntPtr.Zero)
            {
                var typeRel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(mediaTypePtr), 2 * IntPtr.Size));
                typeRel(mediaTypePtr);
            }
            if (readerPtr != IntPtr.Zero)
            {
                var readerRel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(readerPtr), 2 * IntPtr.Size));
                readerRel(readerPtr);
            }
            if (attrPtr != IntPtr.Zero)
            {
                var attrRel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(attrPtr), 2 * IntPtr.Size));
                attrRel(attrPtr);
            }
        }

        return null;
    }

    private Bitmap? ExtractBestFrame(string filePath, TimeSpan duration, int targetSize, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !_mfInitialized) return null;

        CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);

        IntPtr readerPtr = IntPtr.Zero;
        IntPtr attrPtr = IntPtr.Zero;
        IntPtr mediaTypePtr = IntPtr.Zero;

        try
        {
            int hr = MFCreateAttributes(out attrPtr, 1);
            if (hr == 0 && attrPtr != IntPtr.Zero)
            {
                IntPtr attrVtbl = Marshal.ReadIntPtr(attrPtr);
                var setUint32 = Marshal.GetDelegateForFunctionPointer<SetUINT32Delegate>(Marshal.ReadIntPtr(attrVtbl, 21 * IntPtr.Size));
                Guid enableVideoProcGuid = new("fb394f3d-ccf1-42ee-bb0c-857852e0e6d6");
                setUint32(attrPtr, ref enableVideoProcGuid, 1);
            }

            hr = MFCreateSourceReaderFromURL(filePath, attrPtr, out readerPtr);
            if (hr != 0 || readerPtr == IntPtr.Zero) return null;

            IntPtr readerVtbl = Marshal.ReadIntPtr(readerPtr);
            var setMediaType = Marshal.GetDelegateForFunctionPointer<SetCurrentMediaTypeDelegate>(Marshal.ReadIntPtr(readerVtbl, 7 * IntPtr.Size));
            var setPosition = Marshal.GetDelegateForFunctionPointer<SetCurrentPositionDelegate>(Marshal.ReadIntPtr(readerVtbl, 8 * IntPtr.Size));
            var readSample = Marshal.GetDelegateForFunctionPointer<ReadSampleDelegate>(Marshal.ReadIntPtr(readerVtbl, 9 * IntPtr.Size));
            var setStreamSelection = Marshal.GetDelegateForFunctionPointer<SetStreamSelectionDelegate>(Marshal.ReadIntPtr(readerVtbl, 4 * IntPtr.Size));

            // Create RGB32 media type
            hr = MFCreateMediaType(out mediaTypePtr);
            if (hr == 0 && mediaTypePtr != IntPtr.Zero)
            {
                IntPtr typeVtbl = Marshal.ReadIntPtr(mediaTypePtr);
                var setGuid = Marshal.GetDelegateForFunctionPointer<SetGUIDDelegate>(Marshal.ReadIntPtr(typeVtbl, 24 * IntPtr.Size));

                Guid mfMtMajorType = new("48eba18e-f829-4679-a7e0-4924f7f40717");
                Guid mfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
                Guid mfMtSubType = new("f7e34c9a-4296-440b-8386-cc4a00c20f5d");
                Guid mfVideoFormatRGB32 = new("00000016-0000-0010-8000-00aa00389b71");

                setGuid(mediaTypePtr, ref mfMtMajorType, ref mfMediaTypeVideo);
                setGuid(mediaTypePtr, ref mfMtSubType, ref mfVideoFormatRGB32);

                setMediaType(readerPtr, 0xfffffffc, IntPtr.Zero, mediaTypePtr);
            }

            setStreamSelection(readerPtr, 0xfffffffc, true);

            // Duration in ticks
            long durationTicks = duration > TimeSpan.Zero ? duration.Ticks : (30L * TimeSpan.TicksPerSecond);

            CandidateFrame? bestCandidate = null;
            double bestScore = double.MinValue;

            foreach (double ratio in CandidateRatios)
            {
                if (cancellationToken.IsCancellationRequested) break;

                long seekTicks = (long)(durationTicks * ratio);
                var propVar = new PROPVARIANT { vt = 20 /* VT_I8 */, hVal = seekTicks };
                Guid timeFormat = Guid.Empty;

                try
                {
                    setPosition(readerPtr, ref timeFormat, ref propVar);
                }
                catch { }

                IntPtr samplePtr = IntPtr.Zero;
                for (int retry = 0; retry < 15; retry++)
                {
                    int readHr = readSample(readerPtr, 0xfffffffc, 0, out _, out uint streamFlags, out _, out samplePtr);
                    if (readHr != 0 || (streamFlags & 0x00000200) != 0 || samplePtr != IntPtr.Zero) break;
                }

                if (samplePtr != IntPtr.Zero)
                {
                    try
                    {
                        var candidate = ExtractCandidateFrameDirect(readerPtr, samplePtr, filePath);
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
                        var sampleRel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(samplePtr), 2 * IntPtr.Size));
                        sampleRel(samplePtr);
                    }
                }
            }

            if (bestCandidate.HasValue)
            {
                return CreateBitmapFromCandidate(bestCandidate.Value, targetSize);
            }
        }
        catch { }
        finally
        {
            if (mediaTypePtr != IntPtr.Zero)
            {
                var typeRel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(mediaTypePtr), 2 * IntPtr.Size));
                typeRel(mediaTypePtr);
            }
            if (readerPtr != IntPtr.Zero)
            {
                var readerRel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(readerPtr), 2 * IntPtr.Size));
                readerRel(readerPtr);
            }
            if (attrPtr != IntPtr.Zero)
            {
                var attrRel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(attrPtr), 2 * IntPtr.Size));
                attrRel(attrPtr);
            }
        }

        return null;
    }

    private static CandidateFrame? ExtractCandidateFrameDirect(IntPtr readerPtr, IntPtr samplePtr, string filePath)
    {
        IntPtr bufferPtr = IntPtr.Zero;
        IntPtr curMediaTypePtr = IntPtr.Zero;

        try
        {
            // Convert to contiguous buffer (slot 41)
            IntPtr sampleVtbl = Marshal.ReadIntPtr(samplePtr);
            var convertToBuffer = Marshal.GetDelegateForFunctionPointer<ConvertToContiguousBufferDelegate>(Marshal.ReadIntPtr(sampleVtbl, 41 * IntPtr.Size));
            int hr = convertToBuffer(samplePtr, out bufferPtr);
            if (hr != 0 || bufferPtr == IntPtr.Zero) return null;

            IntPtr bufferVtbl = Marshal.ReadIntPtr(bufferPtr);
            var lockBuffer = Marshal.GetDelegateForFunctionPointer<LockDelegate>(Marshal.ReadIntPtr(bufferVtbl, 3 * IntPtr.Size));
            var unlockBuffer = Marshal.GetDelegateForFunctionPointer<UnlockDelegate>(Marshal.ReadIntPtr(bufferVtbl, 4 * IntPtr.Size));

            hr = lockBuffer(bufferPtr, out IntPtr pData, out uint maxLen, out uint currentLen);
            if (hr != 0 || pData == IntPtr.Zero || currentLen == 0) return null;

            try
            {
                var dims = GetVideoDimensions(filePath);
                int width = dims.Width;
                int height = dims.Height;

                if (width <= 0 || height <= 0)
                {
                    // Fallback to 16:9 ratio based on byte length
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
                unlockBuffer(bufferPtr);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (curMediaTypePtr != IntPtr.Zero)
            {
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(curMediaTypePtr), 2 * IntPtr.Size));
                rel(curMediaTypePtr);
            }
            if (bufferPtr != IntPtr.Zero)
            {
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(bufferPtr), 2 * IntPtr.Size));
                rel(bufferPtr);
            }
        }
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

    private static Bitmap CreateBitmapFromCandidate(CandidateFrame frame, int targetSize)
    {
        int srcW = frame.Width;
        int srcH = frame.Height;

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
                int srcPixelOffset = srcRowOffset + sx * 4;
                int dstPixelOffset = dstRowOffset + dx * 4;

                if (srcPixelOffset + 3 < frame.Pixels.Length && dstPixelOffset + 3 < dstPixels.Length)
                {
                    dstPixels[dstPixelOffset] = frame.Pixels[srcPixelOffset];
                    dstPixels[dstPixelOffset + 1] = frame.Pixels[srcPixelOffset + 1];
                    dstPixels[dstPixelOffset + 2] = frame.Pixels[srcPixelOffset + 2];
                    dstPixels[dstPixelOffset + 3] = frame.Pixels[srcPixelOffset + 3];
                }
            }
        }

        var result = new WriteableBitmap(
            new PixelSize(dstW, dstH),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = result.Lock())
        {
            Marshal.Copy(dstPixels, 0, fb.Address, dstPixels.Length);
        }

        return result;
    }

    private void InitializeMediaFoundation()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
            int hr = MFStartup(0x00020070, 1);
            _mfInitialized = (hr == 0);
        }
        catch
        {
            _mfInitialized = false;
        }
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

    #region Native P/Invoke & COM Function Pointer Declarations

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt64Delegate(IntPtr thisPtr, [In] ref PROPERTYKEY key, [Out] out ulong pull);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt32Delegate(IntPtr thisPtr, [In] ref PROPERTYKEY key, [Out] out uint pull);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetUINT32Delegate(IntPtr thisPtr, [In] ref Guid guidKey, uint unValue);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetGUIDDelegate(IntPtr thisPtr, [In] ref Guid guidKey, [In] ref Guid guidValue);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetStreamSelectionDelegate(IntPtr thisPtr, uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentMediaTypeDelegate(IntPtr thisPtr, uint dwStreamIndex, IntPtr pdwReserved, IntPtr pMediaType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentPositionDelegate(IntPtr thisPtr, [In] ref Guid guidTimeFormat, [In] ref PROPVARIANT varPosition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadSampleDelegate(
        IntPtr thisPtr,
        uint dwStreamIndex,
        uint dwControlFlags,
        out uint pdwActualStreamIndex,
        out uint pdwStreamFlags,
        out long pllTimestamp,
        out IntPtr ppSample);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConvertToContiguousBufferDelegate(IntPtr thisPtr, out IntPtr ppBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockDelegate(IntPtr thisPtr, out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockDelegate(IntPtr thisPtr);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint Version, uint dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType([Out] out IntPtr ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes([Out] out IntPtr ppMFAttributes, uint cInitialSize);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        [In] IntPtr pAttributes,
        [Out] out IntPtr ppSourceReader);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    #endregion
}
