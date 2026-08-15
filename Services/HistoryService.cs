using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public class FolderHistoryEntry
{
    public string Path { get; set; } = string.Empty;
    public int VisitCount { get; set; } = 1;
    public DateTime LastVisited { get; set; } = DateTime.Now;
}

public class HistoryService
{
    public static HistoryService Instance { get; } = new();

    private readonly string _historyFilePath;
    private readonly string _portableHistoryFilePath;
    private readonly Dictionary<string, FolderHistoryEntry> _history = new(StringComparer.OrdinalIgnoreCase);

    private FolderHistoryEntry? _lastDeletedEntry;

    public bool CanUndo => _lastDeletedEntry != null;
    public string LastDeletedName => _lastDeletedEntry != null ? FormatCompactPath(_lastDeletedEntry.Path) : string.Empty;

    public HistoryService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = System.IO.Path.Combine(appData, "C-Explorer");
        Directory.CreateDirectory(dir);
        _historyFilePath = System.IO.Path.Combine(dir, "history.json");

        var appBase = AppDomain.CurrentDomain.BaseDirectory;
        _portableHistoryFilePath = System.IO.Path.Combine(appBase, "history.json");

        LoadHistory();
    }

    public void LoadHistory()
    {
        try
        {
            string targetPath = File.Exists(_portableHistoryFilePath) ? _portableHistoryFilePath : _historyFilePath;
            if (File.Exists(targetPath))
            {
                string json = File.ReadAllText(targetPath);
                var list = JsonSerializer.Deserialize<List<FolderHistoryEntry>>(json);
                if (list != null)
                {
                    _history.Clear();
                    foreach (var item in list)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Path))
                        {
                            _history[item.Path.TrimEnd('\\', '/')] = item;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load history: {ex.Message}");
        }
    }

    public void SaveHistory()
    {
        try
        {
            var list = _history.Values.ToList();
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(list, options);

            string targetPath = File.Exists(_portableHistoryFilePath) ? _portableHistoryFilePath : _historyFilePath;
            File.WriteAllText(targetPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save history: {ex.Message}");
        }
    }

    public void RecordFolderVisit(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.TrimEnd('\\', '/');

        // Do not record root drive letters like C: or Z:
        if (path.Length <= 3 && path.Contains(':')) return;

        if (_history.TryGetValue(path, out var entry))
        {
            entry.VisitCount++;
            entry.LastVisited = DateTime.Now;
        }
        else
        {
            _history[path] = new FolderHistoryEntry
            {
                Path = path,
                VisitCount = 1,
                LastVisited = DateTime.Now
            };
        }

        SaveHistory();
    }

    public List<FrequentFolderItem> GetFrequentFolders(IEnumerable<string>? excludePaths = null, int max = 5)
    {
        var excludeSet = BuildExcludeSet(excludePaths);

        return _history.Values
            .Where(e => !string.IsNullOrWhiteSpace(e.Path) && !excludeSet.Contains(e.Path.TrimEnd('\\', '/')))
            .OrderByDescending(e => e.VisitCount)
            .ThenByDescending(e => e.LastVisited)
            .Take(max)
            .Select(CreateItem)
            .ToList();
    }

    public List<FrequentFolderItem> GetRecentFolders(IEnumerable<string>? excludePaths = null, int max = 5)
    {
        var excludeSet = BuildExcludeSet(excludePaths);

        return _history.Values
            .Where(e => !string.IsNullOrWhiteSpace(e.Path) && !excludeSet.Contains(e.Path.TrimEnd('\\', '/')))
            .OrderByDescending(e => e.LastVisited)
            .Take(max)
            .Select(CreateItem)
            .ToList();
    }

    public void ResetFolderHistory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.TrimEnd('\\', '/');

        if (_history.TryGetValue(path, out var entry))
        {
            _lastDeletedEntry = new FolderHistoryEntry
            {
                Path = entry.Path,
                VisitCount = entry.VisitCount,
                LastVisited = entry.LastVisited
            };
            _history.Remove(path);
            SaveHistory();
        }
    }

    public bool UndoReset()
    {
        if (_lastDeletedEntry == null) return false;
        var p = _lastDeletedEntry.Path.TrimEnd('\\', '/');
        _history[p] = _lastDeletedEntry;
        _lastDeletedEntry = null;
        SaveHistory();
        return true;
    }

    private HashSet<string> BuildExcludeSet(IEnumerable<string>? excludePaths)
    {
        var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludePaths != null)
        {
            foreach (var p in excludePaths)
            {
                excludeSet.Add(p.TrimEnd('\\', '/'));
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        excludeSet.Add(userProfile);
        excludeSet.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop).TrimEnd('\\', '/'));
        excludeSet.Add(System.IO.Path.Combine(userProfile, "Downloads").TrimEnd('\\', '/'));
        excludeSet.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments).TrimEnd('\\', '/'));

        return excludeSet;
    }

    public static string FormatCompactPath(string path, int maxLength = 22)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        path = path.TrimEnd('\\', '/');

        if (path.Length <= maxLength) return path;

        // UNC Path: \\wsl$\Ubuntu\home -> \\wsl$\...\home
        if (path.StartsWith(@"\\"))
        {
            var parts = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                return $@"\\{parts[0]}\...\{parts[^1]}";
            }
            return path;
        }

        // Standard Path: C:\Users\5900x\Downloads\FD -> C:\...\FD
        var segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3)
        {
            string root = segments[0]; // e.g. "C:" or "Z:"
            string leaf = segments[^1]; // e.g. "FD"
            return $@"{root}\...\{leaf}";
        }

        return path;
    }

    private FrequentFolderItem CreateItem(FolderHistoryEntry e)
    {
        return new FrequentFolderItem
        {
            Path = e.Path,
            DisplayName = FormatCompactPath(e.Path),
            VisitCount = e.VisitCount,
            LastVisited = e.LastVisited
        };
    }
}
