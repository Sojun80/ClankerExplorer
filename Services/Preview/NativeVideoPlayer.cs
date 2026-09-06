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
            lock (_lock) return _mediaPlayer;
        }
    }

    public bool IsInitialized
    {
        get
        {
            lock (_lock) return _mediaPlayer != null;
        }
    }

    private TimeSpan _duration = TimeSpan.Zero;
    public TimeSpan Duration
    {
        get { lock (_lock) return _duration; }
        private set { lock (_lock) _duration = value; }
    }

    private string? _lastError;
    public string? LastError
    {
        get { lock (_lock) return _lastError; }
        private set { lock (_lock) _lastError = value; }
    }

    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<TimeSpan>? TimeChanged;

    private void EnsurePlayer()
    {
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
                    bool notify = false;
                    lock (_lock)
                    {
                        if (_isDisposed) return;
                        if (e.Length > 0)
                        {
                            _duration = TimeSpan.FromMilliseconds(e.Length);
                            notify = true;
                        }
                    }
                    if (notify)
                    {
                        MediaOpened?.Invoke();
                    }
                };

                _mediaPlayer.TimeChanged += (s, e) =>
                {
                    lock (_lock)
                    {
                        if (_isDisposed) return;
                    }
                    if (e.Time >= 0)
                    {
                        TimeChanged?.Invoke(TimeSpan.FromMilliseconds(e.Time));
                    }
                };

                _mediaPlayer.EndReached += (s, e) =>
                {
                    lock (_lock)
                    {
                        if (_isDisposed) return;
                    }
                    MediaEnded?.Invoke();
                };

                _mediaPlayer.EncounteredError += (s, e) =>
                {
                    lock (_lock)
                    {
                        if (_isDisposed) return;
                        _lastError = "Error occurred during video playback.";
                    }
                };
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
        }
    }

    public bool Open(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            lock (_lock)
            {
                _lastError = "File not found.";
            }
            return false;
        }

        EnsurePlayer();
        var vlc = VlcVideoService.Instance.LibVLC;
        if (vlc == null)
        {
            lock (_lock)
            {
                _lastError = "LibVLC engine unavailable.";
            }
            return false;
        }

        Media? newMedia = null;
        try
        {
            newMedia = new Media(vlc, filePath, FromType.FromPath);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _lastError = ex.Message;
            }
            return false;
        }

        Media? oldMedia = null;
        lock (_lock)
        {
            if (_isDisposed || _mediaPlayer == null)
            {
                newMedia.Dispose();
                return false;
            }

            try
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Media = null;
            }
            catch { }

            oldMedia = _currentMedia;
            _currentMedia = newMedia;
            _currentFilePath = filePath;
            _duration = TimeSpan.Zero;

            try
            {
                _mediaPlayer.Media = _currentMedia;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                try { _mediaPlayer.Media = null; } catch { }
                _currentMedia = null;
                _currentFilePath = null;
                newMedia.Dispose();
                oldMedia?.Dispose();
                return false;
            }
        }

        try
        {
            oldMedia?.Dispose();
        }
        catch { }

        return true;
    }

    public void Play()
    {
        lock (_lock)
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
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            try { _mediaPlayer?.Pause(); } catch { }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            try
            {
                _mediaPlayer?.Stop();
            }
            catch { }
        }
    }

    public void SetPosition(TimeSpan position)
    {
        lock (_lock)
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
    }

    public TimeSpan GetPosition()
    {
        lock (_lock)
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
    }

    public void SetVolume(double volume)
    {
        lock (_lock)
        {
            _volume = (int)Math.Clamp(volume * 100.0, 0, 100);
            if (_mediaPlayer != null && !_isDisposed)
            {
                try { _mediaPlayer.Volume = _isMuted ? 0 : _volume; } catch { }
            }
        }
    }

    public void SetMute(bool isMuted)
    {
        lock (_lock)
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
    }

    /// <summary>
    /// Checks whether this player currently owns or is loaded with the specified file.
    /// </summary>
    public bool OwnsFile(string? filePath)
    {
        lock (_lock)
        {
            return OwnsFile_Locked(filePath);
        }
    }

    private bool OwnsFile_Locked(string? filePath)
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
        Media? mediaToDispose = null;
        lock (_lock)
        {
            if (_isDisposed) return;
            if (!OwnsFile_Locked(filePath)) return;

            if (_mediaPlayer != null)
            {
                try { _mediaPlayer.Stop(); } catch { }
                try { _mediaPlayer.Media = null; } catch { }
            }

            mediaToDispose = _currentMedia;
            _currentMedia = null;
            _currentFilePath = null;
            _duration = TimeSpan.Zero;
        }

        try
        {
            mediaToDispose?.Dispose();
        }
        catch { }

        // Verify the file handle is genuinely released by the OS before returning.
        // Runs off-thread to avoid blocking Avalonia UI thread.
        await Task.Run(async () =>
        {
            if (!File.Exists(filePath)) return;

            for (int i = 0; i < 20; i++)
            {
                bool released = false;
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                    released = true;
                }
                catch (UnauthorizedAccessException)
                {
                    try
                    {
                        using var fsRead = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
                        released = true;
                    }
                    catch (IOException) { }
                    catch { released = true; }
                }
                catch (IOException) { }
                catch { released = true; }

                if (released) break;
                if (i < 19)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
    }

    public void Close()
    {
        Media? oldMedia = null;
        lock (_lock)
        {
            if (_mediaPlayer != null)
            {
                try
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Media = null;
                }
                catch { }
            }
            oldMedia = _currentMedia;
            _currentMedia = null;
            _currentFilePath = null;
            _duration = TimeSpan.Zero;
        }

        try
        {
            oldMedia?.Dispose();
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }
        Close();
    }
}
