using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using LibVLCSharp.Shared;
using ClankerExplorer.Services.Preview;

namespace ClankerExplorer.Services.Metadata.Providers;

/// <summary>
/// Extracts video metadata: duration, dimensions, aspect ratio, FPS, codecs, bitrates, audio channels/sample rate, container, encoder.
/// </summary>
public class VideoMetadataProvider : IMetadataProvider
{
    public int Order => 10;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".flv", ".vob",
        ".ogv", ".ogg", ".drc", ".gifv", ".mng", ".asf", ".mts", ".m2ts", ".ts",
        ".qt", ".yuv", ".rm", ".rmvb", ".viv", ".amv", ".m4p", ".mpg", ".mp2",
        ".mpeg", ".mpe", ".mpv", ".m2v", ".svi", ".3gp", ".3g2", ".mxf", ".roq",
        ".nsv", ".f4v", ".f4p"
    };

    public bool CanHandle(MetadataExtractionContext context)
    {
        return !context.IsDirectory && VideoExtensions.Contains(context.Extension);
    }

    public async Task ProvideMetadataAsync(MetadataExtractionContext context, CancellationToken cancellationToken)
    {
        string path = context.FilePath;
        if (!File.Exists(path)) return;

        TimeSpan duration = TimeSpan.Zero;
        uint width = 0;
        uint height = 0;
        uint videoBitrate = 0;
        double frameRate = 0;
        string? videoCodec = null;
        string? audioCodec = null;
        uint audioChannels = 0;
        uint audioSampleRate = 0;
        uint audioBitrate = 0;
        string? containerFormat = GetContainerName(context.Extension);
        string? encoder = null;

        // 1. Windows Storage & Shell Property System
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
                var vidProps = await storageFile.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);

                if (vidProps != null)
                {
                    duration = vidProps.Duration;
                    width = vidProps.Width;
                    height = vidProps.Height;
                    videoBitrate = vidProps.Bitrate;
                }

                var extraProps = await storageFile.Properties.RetrievePropertiesAsync(new[]
                {
                    "System.Video.FrameRate",
                    "System.Video.Compression",
                    "System.Video.TotalBitrate",
                    "System.Audio.Format",
                    "System.Audio.ChannelCount",
                    "System.Audio.SampleRate",
                    "System.Audio.EncodingBitrate"
                }).AsTask(cancellationToken).ConfigureAwait(false);

                if (extraProps != null)
                {
                    if (extraProps.TryGetValue("System.Video.FrameRate", out var fpsVal) && fpsVal != null)
                    {
                        if (fpsVal is uint fpsUint && fpsUint > 0)
                        {
                            frameRate = fpsUint >= 1000 ? fpsUint / 1000.0 : fpsUint;
                        }
                    }

                    if (extraProps.TryGetValue("System.Video.Compression", out var compVal) && compVal is string compStr && !string.IsNullOrWhiteSpace(compStr))
                    {
                        videoCodec = FormatCodecName(compStr);
                    }

                    if (extraProps.TryGetValue("System.Audio.Format", out var aFmt) && aFmt is string aFmtStr && !string.IsNullOrWhiteSpace(aFmtStr))
                    {
                        audioCodec = FormatCodecName(aFmtStr);
                    }

                    if (extraProps.TryGetValue("System.Audio.ChannelCount", out var chVal) && chVal is uint ch && ch > 0)
                    {
                        audioChannels = ch;
                    }

                    if (extraProps.TryGetValue("System.Audio.SampleRate", out var srVal) && srVal is uint sr && sr > 0)
                    {
                        audioSampleRate = sr;
                    }

                    if (extraProps.TryGetValue("System.Audio.EncodingBitrate", out var abVal) && abVal is uint ab && ab > 0)
                    {
                        audioBitrate = ab;
                    }
                }
            }
            catch { }
        }

        // 2. LibVLC Fallback / Supplement for exotic containers (MKV, WebM, TS, etc.)
        if (width == 0 || string.IsNullOrEmpty(videoCodec) || frameRate <= 0)
        {
            try
            {
                var vlc = VlcVideoService.Instance.LibVLC;
                if (vlc != null)
                {
                    using var media = new Media(vlc, path, FromType.FromPath);
                    await media.Parse(MediaParseOptions.ParseLocal, 600).ConfigureAwait(false);

                    if (duration <= TimeSpan.Zero && media.Duration > 0)
                    {
                        duration = TimeSpan.FromMilliseconds(media.Duration);
                    }

                    encoder = media.Meta(MetadataType.Setting) ?? media.Meta(MetadataType.EncodedBy);

                    foreach (var track in media.Tracks)
                    {
                        if (track.TrackType == TrackType.Video)
                        {
                            if (width == 0) width = track.Data.Video.Width;
                            if (height == 0) height = track.Data.Video.Height;
                            if (frameRate <= 0 && track.Data.Video.FrameRateDen > 0)
                            {
                                frameRate = (double)track.Data.Video.FrameRateNum / track.Data.Video.FrameRateDen;
                            }
                            if (string.IsNullOrEmpty(videoCodec))
                            {
                                videoCodec = FourCCToString(track.Codec);
                            }
                            if (videoBitrate == 0 && track.Bitrate > 0)
                            {
                                videoBitrate = track.Bitrate;
                            }
                        }
                        else if (track.TrackType == TrackType.Audio)
                        {
                            if (string.IsNullOrEmpty(audioCodec))
                            {
                                audioCodec = FourCCToString(track.Codec);
                            }
                            if (audioChannels == 0 && track.Data.Audio.Channels > 0)
                            {
                                audioChannels = track.Data.Audio.Channels;
                            }
                            if (audioSampleRate == 0 && track.Data.Audio.Rate > 0)
                            {
                                audioSampleRate = track.Data.Audio.Rate;
                            }
                            if (audioBitrate == 0 && track.Bitrate > 0)
                            {
                                audioBitrate = track.Bitrate;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // Add fields to Media section
        if (duration > TimeSpan.Zero)
        {
            context.AddItem("Media", "🎬", "Duration", FormatDuration(duration), isCopyable: true, isMonospace: true);
        }

        if (width > 0 && height > 0)
        {
            string aspect = GetAspectRatio(width, height);
            context.AddItem("Media", "🎬", "Dimensions", $"{width} × {height}", isCopyable: true, isMonospace: true);
            context.AddItem("Media", "🎬", "Aspect Ratio", aspect, isCopyable: true);
        }

        if (frameRate > 0)
        {
            context.AddItem("Media", "🎬", "Frame Rate", $"{frameRate:F2} FPS", isCopyable: true, isMonospace: true);
        }

        if (!string.IsNullOrEmpty(videoCodec))
        {
            context.AddItem("Media", "🎬", "Video Codec", videoCodec, isCopyable: true);
        }

        if (videoBitrate > 0)
        {
            context.AddItem("Media", "🎬", "Video Bitrate", FormatBitrate(videoBitrate), isCopyable: true, isMonospace: true);
        }

        if (!string.IsNullOrEmpty(audioCodec))
        {
            context.AddItem("Media", "🎬", "Audio Codec", audioCodec, isCopyable: true);
        }

        if (audioChannels > 0)
        {
            string chText = audioChannels switch
            {
                1 => "1 (Mono)",
                2 => "2 (Stereo)",
                6 => "6 (5.1 Surround)",
                8 => "8 (7.1 Surround)",
                _ => $"{audioChannels} Channels"
            };
            context.AddItem("Media", "🎬", "Audio Channels", chText, isCopyable: true);
        }

        if (audioSampleRate > 0)
        {
            context.AddItem("Media", "🎬", "Sample Rate", $"{audioSampleRate:N0} Hz", isCopyable: true, isMonospace: true);
        }

        if (audioBitrate > 0)
        {
            context.AddItem("Media", "🎬", "Audio Bitrate", FormatBitrate(audioBitrate), isCopyable: true, isMonospace: true);
        }

        if (!string.IsNullOrEmpty(containerFormat))
        {
            context.AddItem("Media", "🎬", "Container", containerFormat, isCopyable: true);
        }

        if (!string.IsNullOrEmpty(encoder))
        {
            context.AddItem("Media", "🎬", "Encoder", encoder, isCopyable: true);
        }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string FormatBitrate(uint bps)
    {
        if (bps >= 1_000_000)
        {
            return $"{bps / 1_000_000.0:F2} Mbps ({bps:N0} bps)";
        }
        if (bps >= 1_000)
        {
            return $"{bps / 1_000.0:F0} kbps";
        }
        return $"{bps} bps";
    }

    private static string GetAspectRatio(uint w, uint h)
    {
        if (w == 0 || h == 0) return "—";
        double ratio = (double)w / h;

        if (Math.Abs(ratio - 16.0 / 9.0) < 0.03) return "16:9";
        if (Math.Abs(ratio - 4.0 / 3.0) < 0.03) return "4:3";
        if (Math.Abs(ratio - 21.0 / 9.0) < 0.04) return "21:9";
        if (Math.Abs(ratio - 1.0) < 0.02) return "1:1";
        if (Math.Abs(ratio - 3.0 / 2.0) < 0.03) return "3:2";
        if (Math.Abs(ratio - 16.0 / 10.0) < 0.03) return "16:10";
        if (Math.Abs(ratio - 9.0 / 16.0) < 0.03) return "9:16 (Vertical)";

        uint gcd = Gcd(w, h);
        uint rw = w / gcd;
        uint rh = h / gcd;
        if (rw <= 32 && rh <= 32) return $"{rw}:{rh}";
        return $"{ratio:F2}:1";
    }

    private static uint Gcd(uint a, uint b)
    {
        while (b != 0)
        {
            uint temp = b;
            b = a % b;
            a = temp;
        }
        return a;
    }

    private static string GetContainerName(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "MPEG-4 (MP4)",
            ".mkv" => "Matroska (MKV)",
            ".avi" => "Audio Video Interleave (AVI)",
            ".mov" or ".qt" => "QuickTime (MOV)",
            ".webm" => "WebM",
            ".wmv" => "Windows Media Video (WMV)",
            ".flv" => "Flash Video (FLV)",
            ".ts" or ".mts" or ".m2ts" => "MPEG Transport Stream (TS)",
            _ => ext.ToUpperInvariant().TrimStart('.')
        };
    }

    private static string FormatCodecName(string codec)
    {
        var upper = codec.Trim().ToUpperInvariant();
        return upper switch
        {
            "H264" or "AVC" or "AVC1" => "H.264 / AVC",
            "H265" or "HEVC" or "HVC1" => "H.265 / HEVC",
            "AV01" or "AV1" => "AV1",
            "VP90" or "VP9" => "VP9",
            "VP80" or "VP8" => "VP8",
            "MP4V" => "MPEG-4 Video",
            "WVC1" or "WMV3" => "Windows Media Video (WMV)",
            "AACL" or "AAC" or "MP4A" => "AAC",
            "MP3" or ".MP3" => "MP3 (MPEG Layer-3)",
            "FLAC" => "FLAC (Lossless)",
            "AC3" or "A52" => "AC-3 (Dolby Digital)",
            "EAC3" => "E-AC-3 (Dolby Digital Plus)",
            _ => codec
        };
    }

    private static string FourCCToString(uint fourcc)
    {
        byte[] bytes = BitConverter.GetBytes(fourcc);
        string str = System.Text.Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
        return string.IsNullOrEmpty(str) ? $"0x{fourcc:X8}" : FormatCodecName(str);
    }
}
