using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;

namespace ClankerExplorer.Services.Preview;

/// <summary>
/// Universal Video Playback & Snapshot service powered by LibVLC.
/// </summary>
public class VlcVideoService : IDisposable
{
    private static readonly Lazy<VlcVideoService> _instance = new(() => new VlcVideoService());
    public static VlcVideoService Instance => _instance.Value;

    private LibVLC? _libVlc;
    private readonly object _lock = new();
    private bool _isInitialized;
    private bool _isDisposed;

    public LibVLC? LibVLC
    {
        get
        {
            EnsureInitialized();
            return _libVlc;
        }
    }

    public bool IsAvailable => LibVLC != null;

    public void EnsureInitialized()
    {
        if (_isInitialized) return;
        lock (_lock)
        {
            if (_isInitialized) return;

            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string libVlcDir = Path.Combine(baseDir, "libvlc", "win-x64");
                if (Directory.Exists(libVlcDir) && File.Exists(Path.Combine(libVlcDir, "libvlc.dll")))
                {
                    Core.Initialize(libVlcDir);
                }
                else
                {
                    Core.Initialize();
                }

                _libVlc = new LibVLC(
                    "--no-osd",
                    "--no-stats",
                    "--no-video-title-show",
                    "--file-caching=300",
                    "--network-caching=300"
                );
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize LibVLC: {ex.Message}");
                _libVlc = null;
                _isInitialized = true; // Don't retry continually on fatal missing dlls
            }
        }
    }

    public MediaPlayer? CreateMediaPlayer()
    {
        var vlc = LibVLC;
        if (vlc == null) return null;
        try
        {
            return new MediaPlayer(vlc)
            {
                EnableHardwareDecoding = true
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Asynchronously extracts a snapshot bitmap using LibVLC as a universal fallback for exotic video formats.
    /// </summary>
    public async Task<Bitmap?> ExtractSnapshotAsync(string filePath, TimeSpan timeOffset, CancellationToken cancellationToken = default)
    {
        var vlc = LibVLC;
        if (vlc == null || string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

        string tempSnapshot = Path.Combine(Path.GetTempPath(), $"vlc_snap_{Guid.NewGuid():N}.png");
        MediaPlayer? mp = null;
        Media? media = null;

        try
        {
            mp = new MediaPlayer(vlc)
            {
                EnableHardwareDecoding = true
            };

            media = new Media(vlc, filePath, FromType.FromPath);
            media.AddOption(":no-audio");
            media.AddOption(":video-filter=null");

            var tcs = new TaskCompletionSource<bool>();
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());

            mp.Playing += (s, e) =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        if (timeOffset > TimeSpan.Zero)
                        {
                            mp.Time = (long)timeOffset.TotalMilliseconds;
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        }

                        // Take snapshot (width 0, height 0 preserves original aspect ratio)
                        bool ok = mp.TakeSnapshot(0, tempSnapshot, 0, 0);
                        if (ok)
                        {
                            // Wait up to 1 second for file to be flushed to disk
                            for (int i = 0; i < 20; i++)
                            {
                                if (File.Exists(tempSnapshot) && new FileInfo(tempSnapshot).Length > 0)
                                {
                                    tcs.TrySetResult(true);
                                    return;
                                }
                                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        tcs.TrySetResult(false);
                    }
                    catch
                    {
                        tcs.TrySetResult(false);
                    }
                });
            };

            mp.EncounteredError += (s, e) => tcs.TrySetResult(false);
            mp.EndReached += (s, e) => tcs.TrySetResult(false);

            mp.Play(media);

            bool success = await Task.WhenAny(tcs.Task, Task.Delay(3500, cancellationToken)).ConfigureAwait(false) == tcs.Task && await tcs.Task;

            mp.Stop();

            if (success && File.Exists(tempSnapshot))
            {
                using var fs = new FileStream(tempSnapshot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
                return new Bitmap(ms);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                mp?.Stop();
                mp?.Dispose();
                media?.Dispose();
                if (File.Exists(tempSnapshot)) File.Delete(tempSnapshot);
            }
            catch { }
        }

        return null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _libVlc?.Dispose();
        _libVlc = null;
    }
}
