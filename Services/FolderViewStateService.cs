using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public sealed class FolderViewStateService : IDisposable
{
    private const int MaximumSavedFolders = 2000;
    private static readonly Lazy<FolderViewStateService> LazyInstance = new(() => new FolderViewStateService());
    public static FolderViewStateService Instance => LazyInstance.Value;

    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly string _stateFilePath;
    private readonly Timer _saveTimer;
    private Dictionary<string, FolderViewState> _states = new(StringComparer.Ordinal);
    private bool _disposed;
    private int _generation;

    public FolderViewStateService(string? dataDirectory = null)
    {
        _stateFilePath = Path.Combine(AppStoragePaths.GetDataDirectory(dataDirectory), "folder-view-states.json");
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    public bool TryGet(string path, out FolderViewState state)
    {
        string key = NormalizePath(path);
        lock (_gate)
        {
            if (_states.TryGetValue(key, out var stored))
            {
                stored.LastUsedUtc = DateTime.UtcNow;
                state = stored.Clone();
                return true;
            }
        }

        state = new FolderViewState();
        return false;
    }

    public void Set(string path, FolderViewState state)
    {
        if (string.IsNullOrWhiteSpace(path) || _disposed) return;
        string key = NormalizePath(path);
        var copy = state.Clone();
        copy.LastUsedUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _states[key] = copy;
            if (_states.Count > MaximumSavedFolders)
            {
                foreach (var oldest in _states.OrderBy(pair => pair.Value.LastUsedUtc)
                             .Take(_states.Count - MaximumSavedFolders).Select(pair => pair.Key).ToArray())
                {
                    _states.Remove(oldest);
                }
            }
            _saveTimer.Change(500, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        if (_disposed) return;
        Dictionary<string, FolderViewState> snapshot;
        int generation;
        lock (_gate)
        {
            snapshot = _states.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            generation = _generation;
        }

        lock (_writeGate)
        {
            lock (_gate)
            {
                if (generation != _generation) return;
            }
            string tempPath = _stateFilePath + ".tmp";
            try
            {
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _stateFilePath, true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            _states.Clear();
            _generation++;
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        lock (_writeGate)
        {
            try { if (File.Exists(_stateFilePath)) File.Delete(_stateFilePath); } catch { }
        }
    }

    public static string NormalizePath(string path)
    {
        string normalized;
        try { normalized = Path.GetFullPath(path); }
        catch { normalized = path; }
        normalized = normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string root = Path.GetPathRoot(normalized) ?? string.Empty;
        if (!string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
            normalized = normalized.TrimEnd(Path.DirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, FolderViewState>>(File.ReadAllText(_stateFilePath));
            if (loaded != null) _states = new Dictionary<string, FolderViewState>(loaded, StringComparer.Ordinal);
        }
        catch
        {
            _states = new Dictionary<string, FolderViewState>(StringComparer.Ordinal);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        _saveTimer.Dispose();
    }
}
