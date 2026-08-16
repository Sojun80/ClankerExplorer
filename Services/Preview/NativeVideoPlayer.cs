using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace ClankerExplorer.Services.Preview;

public class NativeVideoPlayer : IDisposable
{
    private MediaPlayer? _mediaPlayer;
    private bool _isDisposed;
    private string? _currentFilePath;
    private double _volume = 0.8;
    private bool _isMuted;

    public bool IsInitialized => _mediaPlayer != null;
    public TimeSpan Duration { get; private set; } = TimeSpan.Zero;
    public string? LastError { get; private set; }

    public event Action? MediaOpened;
    public event Action? MediaEnded;

    public async Task<bool> OpenAsync(string filePath)
    {
        Close();
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            LastError = "File not found.";
            return false;
        }

        try
        {
            _currentFilePath = filePath;
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            _mediaPlayer = new MediaPlayer
            {
                AutoPlay = false,
                Volume = _isMuted ? 0.0 : _volume,
                IsMuted = _isMuted
            };

            var tcs = new TaskCompletionSource<bool>();
            _mediaPlayer.MediaOpened += (s, e) =>
            {
                Duration = _mediaPlayer.PlaybackSession.NaturalDuration;
                tcs.TrySetResult(true);
                MediaOpened?.Invoke();
            };

            _mediaPlayer.MediaFailed += (s, e) =>
            {
                LastError = $"{e.Error}: {e.ErrorMessage}";
                tcs.TrySetResult(false);
            };

            _mediaPlayer.MediaEnded += (s, e) =>
            {
                MediaEnded?.Invoke();
            };

            _mediaPlayer.Source = MediaSource.CreateFromStorageFile(file);
            bool ok = await Task.WhenAny(tcs.Task, Task.Delay(3000)) == tcs.Task && await tcs.Task;
            return ok;
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
        try { _mediaPlayer?.Play(); } catch { }
    }

    public void Pause()
    {
        try { _mediaPlayer?.Pause(); } catch { }
    }

    public void Stop()
    {
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Pause();
                _mediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
            }
        }
        catch { }
    }

    public void SetPosition(TimeSpan position)
    {
        try
        {
            if (_mediaPlayer != null && position >= TimeSpan.Zero)
            {
                _mediaPlayer.PlaybackSession.Position = position;
            }
        }
        catch { }
    }

    public TimeSpan GetPosition()
    {
        try
        {
            return _mediaPlayer?.PlaybackSession.Position ?? TimeSpan.Zero;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0.0, 1.0);
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Volume = _isMuted ? 0.0 : _volume;
        }
    }

    public void SetMute(bool isMuted)
    {
        _isMuted = isMuted;
        if (_mediaPlayer != null)
        {
            _mediaPlayer.IsMuted = isMuted;
            _mediaPlayer.Volume = isMuted ? 0.0 : _volume;
        }
    }

    public void Close()
    {
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
                _mediaPlayer.Dispose();
            }
        }
        catch { }

        _mediaPlayer = null;
        Duration = TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Close();
    }
}
