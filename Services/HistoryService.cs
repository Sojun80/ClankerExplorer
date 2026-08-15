using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _historyFilePath;
    private readonly string _portableHistoryFilePath;
    private readonly Dictionary<string, FolderHistoryEntry> _history;
    private readonly object _lock = new();

    private FolderHistoryEntry? _lastDeletedEntry;
    private CancellationTokenSource? _saveDebounceCts;

    public bool CanUndo => _lastDeletedEntry != null;
    public string LastDeletedName => _lastDeletedEntry != null ? FormatCompactPath(_lastDeletedEntry.Path) : string.Empty;

    public HistoryService()
    {
        _history = new Dictionary<string, FolderHistoryEntry>(PathComparer);

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
        lock (_lock)
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
                                var key = item.Path.TrimEnd('\\', '/');
                                _history[key] = item;
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
    }

    public void SaveHistory()
    {
        lock (_lock)
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
    }

    public void ScheduleSaveHistory()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts = new CancellationTokenSource();
        var token = _saveDebounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                if (!token.IsCancellationRequested)
                {
                    SaveHistory();
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public void RecordFolderVisit(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.Trim();

        // Do not record root paths like C:\, /, or drive roots
        if (path == "/" || path == "\\" || (path.Length <= 3 && path.Contains(':'))) return;

        path = path.TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (_lock)
        {
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
        }

        ScheduleSaveHistory();
    }

    public List<FolderHistoryEntry> GetAllHistoryEntries()
    {
        lock (_lock)
        {
            return _history.Values
                .Select(e => new FolderHistoryEntry
                {
                    Path = e.Path,
                    VisitCount = e.VisitCount,
                    LastVisited = e.LastVisited
                })
                .ToList();
        }
    }

    public void ImportHistoryEntries(IEnumerable<FolderHistoryEntry> entries)
    {
        if (entries == null) return;
        lock (_lock)
        {
            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.Path)) continue;
                var key = e.Path.TrimEnd('\\', '/');
                if (_history.TryGetValue(key, out var existing))
                {
                    existing.VisitCount = Math.Max(existing.VisitCount, e.VisitCount);
                    if (e.LastVisited > existing.LastVisited)
                    {
                        existing.LastVisited = e.LastVisited;
                    }
                }
                else
                {
                    _history[key] = new FolderHistoryEntry
                    {
                        Path = e.Path,
                        VisitCount = e.VisitCount,
                        LastVisited = e.LastVisited
                    };
                }
            }
        }
        SaveHistory();
    }

    public List<FrequentFolderItem> GetFrequentFolders(IEnumerable<string>? excludePaths = null, int max = 5)
    {
        var excludeSet = BuildExcludeSet(excludePaths);

        lock (_lock)
        {
            return _history.Values
                .Where(e => !string.IsNullOrWhiteSpace(e.Path) && !excludeSet.Contains(e.Path.TrimEnd('\\', '/')))
                .OrderByDescending(e => e.VisitCount)
                .ThenByDescending(e => e.LastVisited)
                .Take(max)
                .Select(CreateItem)
                .ToList();
        }
    }

    public List<FrequentFolderItem> GetRecentFolders(IEnumerable<string>? excludePaths = null, int max = 5)
    {
        var excludeSet = BuildExcludeSet(excludePaths);

        lock (_lock)
        {
            return _history.Values
                .Where(e => !string.IsNullOrWhiteSpace(e.Path) && !excludeSet.Contains(e.Path.TrimEnd('\\', '/')))
                .OrderByDescending(e => e.LastVisited)
                .Take(max)
                .Select(CreateItem)
                .ToList();
        }
    }

    public void ResetFolderHistory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.TrimEnd('\\', '/');

        lock (_lock)
        {
            if (_history.TryGetValue(path, out var entry))
            {
                _lastDeletedEntry = new FolderHistoryEntry
                {
                    Path = entry.Path,
                    VisitCount = entry.VisitCount,
                    LastVisited = entry.LastVisited
                };
                _history.Remove(path);
            }
        }
        SaveHistory();
    }

    public bool UndoReset()
    {
        lock (_lock)
        {
            if (_lastDeletedEntry == null) return false;
            var p = _lastDeletedEntry.Path.TrimEnd('\\', '/');
            _history[p] = _lastDeletedEntry;
            _lastDeletedEntry = null;
        }
        SaveHistory();
        return true;
    }

    private HashSet<string> BuildExcludeSet(IEnumerable<string>? excludePaths)
    {
        var excludeSet = new HashSet<string>(PathComparer);
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

        bool startsWithSlash = path.StartsWith('/');
        path = path.TrimEnd('\\', '/');

        if (path.Length <= maxLength) return startsWithSlash && !path.StartsWith('/') ? "/" + path : path;

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

        // Standard Path: C:\Users\5900x\Downloads\FD -> C:\...\FD or /home/user/code/FD -> /home/.../FD
        var segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3)
        {
            string root = segments[0]; // e.g. "C:" or "home"
            string leaf = segments[^1]; // e.g. "FD"
            var sep = OperatingSystem.IsWindows() ? '\\' : '/';
            string prefix = startsWithSlash ? "/" : "";
            return $"{prefix}{root}{sep}...{sep}{leaf}";
        }

        return startsWithSlash && !path.StartsWith('/') ? "/" + path : path;
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
