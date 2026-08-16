using System;
using System.IO;
using System.Text.Json;

namespace ClankerExplorer.Services;

/// <summary>
/// Persists video playback preferences (volume, mute state) across sessions.
/// Stored as a small JSON file alongside other app data.
/// </summary>
public class VideoPreferencesService
{
    public static VideoPreferencesService Instance { get; } = new();

    private readonly string _filePath;
    private VideoPreferences _current;

    public VideoPreferencesService(string? dataDirectory = null)
    {
        var dir = AppStoragePaths.GetDataDirectory(dataDirectory);
        _filePath = Path.Combine(dir, "video-preferences.json");
        _current = Load();
    }

    public double Volume => _current.Volume;
    public bool IsMuted => _current.IsMuted;

    public void SetVolume(double volume)
    {
        _current.Volume = Math.Clamp(volume, 0.0, 1.0);
        Save();
    }

    public void SetMuted(bool isMuted)
    {
        _current.IsMuted = isMuted;
        Save();
    }

    private VideoPreferences Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<VideoPreferences>(json);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load video preferences: {ex.Message}");
        }
        return new VideoPreferences();
    }

    private void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_current, options);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save video preferences: {ex.Message}");
        }
    }

    private class VideoPreferences
    {
        public double Volume { get; set; } = 0.8;
        public bool IsMuted { get; set; } = false;
    }
}
