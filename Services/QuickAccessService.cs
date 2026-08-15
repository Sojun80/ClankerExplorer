using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public class QuickAccessService
{
    public static QuickAccessService Instance { get; } = new();

    private readonly string _filePath;
    private readonly string _portableFilePath;
    private readonly bool _populateDefaultsWhenEmpty;
    private readonly List<QuickAccessItem> _items = new();

    public event Action? QuickAccessChanged;

    public IReadOnlyList<QuickAccessItem> Items
    {
        get
        {
            lock (_items)
            {
                return _items.ToList();
            }
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public string FilePath => File.Exists(_portableFilePath) ? _portableFilePath : _filePath;

    public QuickAccessService(string? dataDirectory = null, bool populateDefaultsWhenEmpty = true)
    {
        _populateDefaultsWhenEmpty = populateDefaultsWhenEmpty;
        var configDir = AppStoragePaths.GetDataDirectory(dataDirectory);
        _filePath = Path.Combine(configDir, "quickaccess.json");

        _portableFilePath = AppStoragePaths.GetPortableFilePath("quickaccess.json", dataDirectory);

        Load();
    }

    public void Load()
    {
        lock (_items)
        {
            _items.Clear();

            string targetPath = File.Exists(_portableFilePath) ? _portableFilePath : _filePath;

            if (File.Exists(targetPath))
            {
                try
                {
                    var json = File.ReadAllText(targetPath);
                    var list = JsonSerializer.Deserialize<List<QuickAccessDto>>(json);
                    if (list != null && list.Count > 0)
                    {
                        foreach (var dto in list)
                        {
                            if (!string.IsNullOrWhiteSpace(dto.Path))
                            {
                                _items.Add(new QuickAccessItem(dto.Path, dto.DisplayName ?? Path.GetFileName(dto.Path), dto.IconSymbol ?? "📁"));
                            }
                        }
                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading Quick Access: {ex.Message}");
                }
            }

            if (_populateDefaultsWhenEmpty)
            {
                PopulateDefaults();
                Save();
            }
        }
    }

    private void PopulateDefaults()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var downloads = Path.Combine(home, "Downloads");
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        if (Directory.Exists(home)) _items.Add(new QuickAccessItem(home, "Home", "🏠"));
        if (Directory.Exists(desktop)) _items.Add(new QuickAccessItem(desktop, "Desktop", "🖥️"));
        if (Directory.Exists(downloads)) _items.Add(new QuickAccessItem(downloads, "Downloads", "📥"));
        if (Directory.Exists(docs)) _items.Add(new QuickAccessItem(docs, "Documents", "📄"));
        if (Directory.Exists(pics)) _items.Add(new QuickAccessItem(pics, "Pictures", "🖼️"));
        if (Directory.Exists(music)) _items.Add(new QuickAccessItem(music, "Music", "🎵"));
        if (Directory.Exists(videos)) _items.Add(new QuickAccessItem(videos, "Videos", "🎬"));
    }

    public void Save()
    {
        lock (_items)
        {
            try
            {
                var dtoList = _items.Select(i => new QuickAccessDto
                {
                    Path = i.Path,
                    DisplayName = i.DisplayName,
                    IconSymbol = i.IconSymbol
                }).ToList();

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(dtoList, options);

                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving Quick Access: {ex.Message}");
            }
        }

        QuickAccessChanged?.Invoke();
    }

    public bool IsPinned(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = Normalize(path);
        lock (_items)
        {
            return _items.Any(i => Normalize(i.Path).Equals(normalized, PathComparison));
        }
    }

    public void PinFolder(string path, string? customName = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Normalize(path);

        lock (_items)
        {
            if (_items.Any(i => Normalize(i.Path).Equals(normalized, PathComparison)))
            {
                return; // Already pinned
            }

            var name = !string.IsNullOrWhiteSpace(customName)
                ? customName
                : (Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

            if (string.IsNullOrWhiteSpace(name)) name = path;

            _items.Add(new QuickAccessItem(path, name, "📁"));
        }

        Save();
    }

    public void UnpinFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Normalize(path);

        bool changed = false;
        lock (_items)
        {
            var target = _items.FirstOrDefault(i => Normalize(i.Path).Equals(normalized, PathComparison));
            if (target != null)
            {
                _items.Remove(target);
                changed = true;
            }
        }

        if (changed)
        {
            Save();
        }
    }

    public void MoveItem(int fromIndex, int toIndex)
    {
        lock (_items)
        {
            if (fromIndex >= 0 && fromIndex < _items.Count && toIndex >= 0 && toIndex < _items.Count && fromIndex != toIndex)
            {
                var item = _items[fromIndex];
                _items.RemoveAt(fromIndex);
                _items.Insert(toIndex, item);
            }
        }

        Save();
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private class QuickAccessDto
    {
        public string? Path { get; set; }
        public string? DisplayName { get; set; }
        public string? IconSymbol { get; set; }
    }
}
