using System;
using System.IO;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace ClankerExplorer.Services.Preview;

/// <summary>
/// High-performance video player powered by LibVLC for full 60fps hardware-accelerated playback.
/// Uses a single reusable MediaPlayer instance to avoid native pointer crashes on rapid navigation.
/// </summary>
public class NativeVideoPlayer : IDisposable
{
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private bool _isDisposed;
    private string? _currentFilePath;
    private int _volume = (int)(VideoPreferencesService.Instance.Volume * 100);
    private bool _isMuted = VideoPreferencesService.Instance.IsMuted;
    private readonly object _lock = new();

    public MediaPlayer? VlcMediaPlayer
    {
        get
        {
            EnsurePlayer();
            return _mediaPlayer;
        }
    }

    public bool IsInitialized => _mediaPlayer != null;
    public TimeSpan Duration { get; private set; } = TimeSpan.Zero;
    public string? LastError { get; private set; }

    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<TimeSpan>? TimeChanged;

    private void EnsurePlayer()
    {
        if (_mediaPlayer != null || _isDisposed) return;
        lock (_lock)
        {
            if (_mediaPlayer != null || _isDisposed) return;
            var vlc = VlcVideoService.Instance.LibVLC;
            if (vlc == null) return;

            try
            {
                _mediaPlayer = new MediaPlayer(vlc)
                {
                    EnableHardwareDecoding = true
                };

                _mediaPlayer.Mute = _isMuted;
                _mediaPlayer.Volume = _isMuted ? 0 : _volume;

                _mediaPlayer.LengthChanged += (s, e) =>
                {
                    if (_isDisposed) return;
                    if (e.Length > 0)
                    {
                        Duration = TimeSpan.FromMilliseconds(e.Length);
                        MediaOpened?.Invoke();
                    }
                };

                _mediaPlayer.TimeChanged += (s, e) =>
                {
                    if (_isDisposed) return;
                    if (e.Time >= 0)
                    {
                        TimeChanged?.Invoke(TimeSpan.FromMilliseconds(e.Time));
                    }
                };

                _mediaPlayer.EndReached += (s, e) =>
                {
                    if (_isDisposed) return;
                    MediaEnded?.Invoke();
                };

                _mediaPlayer.EncounteredError += (s, e) =>
                {
                    if (_isDisposed) return;
                    LastError = "Error occurred during video playback.";
                };
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    public bool Open(string filePath)
    {
        if (_isDisposed) return false;
        Close();

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            LastError = "File not found.";
            return false;
        }

        try
        {
            EnsurePlayer();
            var vlc = VlcVideoService.Instance.LibVLC;
            if (_mediaPlayer == null || vlc == null)
            {
                LastError = "LibVLC engine unavailable.";
                return false;
            }

            _currentFilePath = filePath;
            _currentMedia = new Media(vlc, filePath, FromType.FromPath);
            _mediaPlayer.Media = _currentMedia;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Close();
            return false;
        }
    }

    public void Play()
    {
        if (_isDisposed) return;
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Mute = _isMuted;
                _mediaPlayer.Volume = _isMuted ? 0 : _volume;
                _mediaPlayer.Play();
            }
        }
        catch { }
    }

    public void Pause()
    {
        if (_isDisposed) return;
        try { _mediaPlayer?.Pause(); } catch { }
    }

    public void Stop()
    {
        if (_isDisposed) return;
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop();
            }
        }
        catch { }
    }

    public void SetPosition(TimeSpan position)
    {
        if (_isDisposed) return;
        try
        {
            if (_mediaPlayer != null && position >= TimeSpan.Zero)
            {
                _mediaPlayer.Time = (long)position.TotalMilliseconds;
            }
        }
        catch { }
    }

    public TimeSpan GetPosition()
    {
        if (_isDisposed) return TimeSpan.Zero;
        try
        {
            long timeMs = _mediaPlayer?.Time ?? 0;
            return timeMs > 0 ? TimeSpan.FromMilliseconds(timeMs) : TimeSpan.Zero;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    public void SetVolume(double volume)
    {
        _volume = (int)Math.Clamp(volume * 100.0, 0, 100);
        if (_mediaPlayer != null && !_isDisposed)
        {
            try { _mediaPlayer.Volume = _isMuted ? 0 : _volume; } catch { }
        }
    }

    public void SetMute(bool isMuted)
    {
        _isMuted = isMuted;
        if (_mediaPlayer != null && !_isDisposed)
        {
            try
            {
                _mediaPlayer.Mute = isMuted;
                _mediaPlayer.Volume = isMuted ? 0 : _volume;
            }
            catch { }
        }
    }

    /// <summary>
    /// Checks whether this player currently owns or is loaded with the specified file.
    /// </summary>
    public bool OwnsFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(_currentFilePath)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(_currentFilePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(filePath, _currentFilePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Asynchronously stops playback, detaches media, disposes the underlying Media object,
    /// and waits for the OS file lock to be genuinely released before returning.
    /// </summary>
    public async Task YieldAsync(string filePath)
    {
        if (_isDisposed) return;
        if (!OwnsFile(filePath)) return;

        try
        {
            if (_mediaPlayer != null)
            {
                var state = _mediaPlayer.State;
                if (state == VLCState.Playing || state == VLCState.Paused || state == VLCState.Buffering || state == VLCState.Opening)
                {
                    var stoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnStopped(object? s, EventArgs e) => stoppedTcs.TrySetResult(true);
                    void OnError(object? s, EventArgs e) => stoppedTcs.TrySetResult(false);

                    _mediaPlayer.Stopped += OnStopped;
                    _mediaPlayer.EncounteredError += OnError;
                    try
                    {
                        _mediaPlayer.Stop();
                        await Task.WhenAny(stoppedTcs.Task, Task.Delay(500)).ConfigureAwait(false);
                    }
                    finally
                    {
                        _mediaPlayer.Stopped -= OnStopped;
                        _mediaPlayer.EncounteredError -= OnError;
                    }
                }
                else
                {
                    _mediaPlayer.Stop();
                }

                _mediaPlayer.Media = null;
            }

            _currentMedia?.Dispose();
            _currentMedia = null;
        }
        catch { }
        finally
        {
            Duration = TimeSpan.Zero;
            _currentFilePath = null;
        }

        // Verify the file handle is genuinely released by the OS before returning
        if (File.Exists(filePath))
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    // Fall back to Read with FileShare.None if file is read-only
                    try
                    {
                        using var fsRead = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
                        break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(10).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
                catch (IOException)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    public void Close()
    {
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Media = null;
            }
            _currentMedia?.Dispose();
            _currentMedia = null;
        }
        catch { }

        Duration = TimeSpan.Zero;
        _currentFilePath = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Close();
    }
}
