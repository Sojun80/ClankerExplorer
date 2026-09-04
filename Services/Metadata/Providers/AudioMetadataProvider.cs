using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using LibVLCSharp.Shared;
using ClankerExplorer.Services.Preview;

namespace ClankerExplorer.Services.Metadata.Providers;

/// <summary>
/// Extracts audio metadata: duration, codec, bitrate, channels, sample rate, title, artist, album, track, year, genre.
/// </summary>
public class AudioMetadataProvider : IMetadataProvider
{
    public int Order => 10;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".wma", ".aac", ".opus",
        ".aiff", ".aif", ".ape", ".alac", ".mid", ".midi"
    };

    public bool CanHandle(MetadataExtractionContext context)
    {
        return !context.IsDirectory && AudioExtensions.Contains(context.Extension);
    }

    public async Task ProvideMetadataAsync(MetadataExtractionContext context, CancellationToken cancellationToken)
    {
        string path = context.FilePath;
        if (!File.Exists(path)) return;

        TimeSpan duration = TimeSpan.Zero;
        uint bitrate = 0;
        string? title = null;
        string? artist = null;
        string? album = null;
        string? albumArtist = null;
        uint trackNumber = 0;
        uint year = 0;
        string? genre = null;
        string? codec = null;
        uint channels = 0;
        uint sampleRate = 0;

        // 1. WAV Header Direct Parser for .wav
        if (string.Equals(context.Extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new BinaryReader(fs);
                if (fs.Length >= 44)
                {
                    string riff = new string(reader.ReadChars(4));
                    uint fileSize = reader.ReadUInt32();
                    string wave = new string(reader.ReadChars(4));

                    if (riff == "RIFF" && wave == "WAVE")
                    {
                        // Scan chunks
                        while (fs.Position < fs.Length - 8)
                        {
                            string chunkId = new string(reader.ReadChars(4));
                            uint chunkSize = reader.ReadUInt32();

                            if (chunkId == "fmt " && chunkSize >= 16)
                            {
                                ushort audioFormat = reader.ReadUInt16();
                                ushort numChannels = reader.ReadUInt16();
                                uint sRate = reader.ReadUInt32();
                                uint byteRate = reader.ReadUInt32();
                                ushort blockAlign = reader.ReadUInt16();
                                ushort bitsPerSample = reader.ReadUInt16();

                                channels = numChannels;
                                sampleRate = sRate;
                                bitrate = byteRate * 8;
                                codec = audioFormat switch
                                {
                                    1 => $"Uncompressed PCM ({bitsPerSample}-bit)",
                                    3 => $"IEEE Float ({bitsPerSample}-bit)",
                                    6 => "A-law",
                                    7 => "mu-law",
                                    0xFFFE => $"Extensible PCM ({bitsPerSample}-bit)",
                                    _ => $"Format 0x{audioFormat:X4}"
                                };

                                // Skip remainder of fmt chunk if > 16
                                if (chunkSize > 16)
                                {
                                    fs.Seek(chunkSize - 16, SeekOrigin.Current);
                                }
                            }
                            else if (chunkId == "data")
                            {
                                if (byteRateFromHeader(bitrate) > 0)
                                {
                                    duration = TimeSpan.FromSeconds((double)chunkSize / (bitrate / 8));
                                }
                                break;
                            }
                            else
                            {
                                fs.Seek(chunkSize, SeekOrigin.Current);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // 2. Windows Storage & MusicProperties
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
                var musicProps = await storageFile.Properties.GetMusicPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);

                if (musicProps != null)
                {
                    if (duration <= TimeSpan.Zero) duration = musicProps.Duration;
                    if (bitrate == 0) bitrate = musicProps.Bitrate;
                    if (string.IsNullOrEmpty(title)) title = musicProps.Title;
                    if (string.IsNullOrEmpty(artist)) artist = musicProps.Artist;
                    if (string.IsNullOrEmpty(album)) album = musicProps.Album;
                    if (string.IsNullOrEmpty(albumArtist)) albumArtist = musicProps.AlbumArtist;
                    if (trackNumber == 0) trackNumber = musicProps.TrackNumber;
                    if (year == 0) year = musicProps.Year;
                    if (string.IsNullOrEmpty(genre) && musicProps.Genre.Count > 0) genre = string.Join(", ", musicProps.Genre);
                }

                var extra = await storageFile.Properties.RetrievePropertiesAsync(new[]
                {
                    "System.Audio.Format",
                    "System.Audio.ChannelCount",
                    "System.Audio.SampleRate",
                    "System.Audio.EncodingBitrate"
                }).AsTask(cancellationToken).ConfigureAwait(false);

                if (extra != null)
                {
                    if (string.IsNullOrEmpty(codec) && extra.TryGetValue("System.Audio.Format", out var fVal) && fVal is string fStr)
                    {
                        codec = FormatAudioCodec(fStr);
                    }
                    if (channels == 0 && extra.TryGetValue("System.Audio.ChannelCount", out var chVal) && chVal is uint ch)
                    {
                        channels = ch;
                    }
                    if (sampleRate == 0 && extra.TryGetValue("System.Audio.SampleRate", out var srVal) && srVal is uint sr)
                    {
                        sampleRate = sr;
                    }
                    if (bitrate == 0 && extra.TryGetValue("System.Audio.EncodingBitrate", out var ebVal) && ebVal is uint eb)
                    {
                        bitrate = eb;
                    }
                }
            }
            catch { }
        }

        // 3. LibVLC Fallback
        if (duration <= TimeSpan.Zero || string.IsNullOrEmpty(codec) || sampleRate == 0)
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

                    if (string.IsNullOrEmpty(title)) title = media.Meta(MetadataType.Title);
                    if (string.IsNullOrEmpty(artist)) artist = media.Meta(MetadataType.Artist);
                    if (string.IsNullOrEmpty(album)) album = media.Meta(MetadataType.Album);
                    if (string.IsNullOrEmpty(genre)) genre = media.Meta(MetadataType.Genre);

                    if (year == 0 && uint.TryParse(media.Meta(MetadataType.Date), out var parsedYear))
                    {
                        year = parsedYear;
                    }

                    if (trackNumber == 0 && uint.TryParse(media.Meta(MetadataType.TrackNumber), out var parsedTrack))
                    {
                        trackNumber = parsedTrack;
                    }

                    foreach (var track in media.Tracks)
                    {
                        if (track.TrackType == TrackType.Audio)
                        {
                            if (string.IsNullOrEmpty(codec)) codec = FourCCToString(track.Codec);
                            if (channels == 0 && track.Data.Audio.Channels > 0) channels = track.Data.Audio.Channels;
                            if (sampleRate == 0 && track.Data.Audio.Rate > 0) sampleRate = track.Data.Audio.Rate;
                            if (bitrate == 0 && track.Bitrate > 0) bitrate = track.Bitrate;
                        }
                    }
                }
            }
            catch { }
        }

        // Add fields to Audio Section
        if (duration > TimeSpan.Zero)
        {
            context.AddItem("Audio", "🎵", "Duration", FormatDuration(duration), isCopyable: true, isMonospace: true);
        }

        if (!string.IsNullOrEmpty(codec))
        {
            context.AddItem("Audio", "🎵", "Codec", codec, isCopyable: true);
        }

        if (bitrate > 0)
        {
            context.AddItem("Audio", "🎵", "Bitrate", FormatBitrate(bitrate), isCopyable: true, isMonospace: true);
        }

        if (channels > 0)
        {
            string chText = channels switch
            {
                1 => "1 (Mono)",
                2 => "2 (Stereo)",
                6 => "6 (5.1 Surround)",
                8 => "8 (7.1 Surround)",
                _ => $"{channels} Channels"
            };
            context.AddItem("Audio", "🎵", "Channels", chText, isCopyable: true);
        }

        if (sampleRate > 0)
        {
            string srDisplay = sampleRate >= 88200 ? $"{sampleRate:N0} Hz (Hi-Res)" : $"{sampleRate:N0} Hz";
            context.AddItem("Audio", "🎵", "Sample Rate", srDisplay, isCopyable: true, isMonospace: true);
        }

        if (!string.IsNullOrEmpty(title))
        {
            context.AddItem("Audio", "🎵", "Title", title, isCopyable: true);
        }

        if (!string.IsNullOrEmpty(artist))
        {
            context.AddItem("Audio", "🎵", "Artist", artist, isCopyable: true);
        }

        if (!string.IsNullOrEmpty(album))
        {
            context.AddItem("Audio", "🎵", "Album", album, isCopyable: true);
        }

        if (!string.IsNullOrEmpty(albumArtist) && !string.Equals(albumArtist, artist, StringComparison.OrdinalIgnoreCase))
        {
            context.AddItem("Audio", "🎵", "Album Artist", albumArtist, isCopyable: true);
        }

        if (trackNumber > 0)
        {
            context.AddItem("Audio", "🎵", "Track #", trackNumber.ToString(), isCopyable: true, isMonospace: true);
        }

        if (year > 0)
        {
            context.AddItem("Audio", "🎵", "Year", year.ToString(), isCopyable: true, isMonospace: true);
        }

        if (!string.IsNullOrEmpty(genre))
        {
            context.AddItem("Audio", "🎵", "Genre", genre, isCopyable: true);
        }
    }

    private static uint byteRateFromHeader(uint bitrate) => bitrate / 8;

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

    private static string FormatAudioCodec(string codec)
    {
        var upper = codec.Trim().ToUpperInvariant();
        return upper switch
        {
            "MP3" or ".MP3" or "MPEG3" => "MP3 (MPEG-1 Audio Layer 3)",
            "AAC" or "AACL" or "MP4A" => "AAC (Advanced Audio Coding)",
            "FLAC" => "FLAC (Free Lossless Audio Codec)",
            "WAV" or "WAVE" or "PCM" => "PCM Waveform Audio",
            "VORBIS" or "OGG" => "Ogg Vorbis",
            "OPUS" => "Opus Audio",
            "ALAC" => "Apple Lossless (ALAC)",
            "WMA" or "WMA2" => "Windows Media Audio",
            _ => codec
        };
    }

    private static string FourCCToString(uint fourcc)
    {
        byte[] bytes = BitConverter.GetBytes(fourcc);
        string str = System.Text.Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
        return string.IsNullOrEmpty(str) ? $"0x{fourcc:X8}" : FormatAudioCodec(str);
    }
}
