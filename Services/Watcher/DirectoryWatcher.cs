using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ClankerExplorer.Services.Watcher;

/// <summary>
/// Robust, disposable directory watcher that wraps FileSystemWatcher with
/// event coalescing, debouncing, and fault tolerance.
/// </summary>
public sealed class DirectoryWatcher : IDirectoryWatcher
{
    private const int DefaultDebounceMs = 100;
    private const int MaxDebounceMs = 400;
    private const int BufferSize = 65536; // 64 KB

    private readonly object _gate = new();
    private readonly StringComparer _pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly Dictionary<string, FileChangeEvent> _pendingChanges;

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private long _firstEventUtcTicks;
    private bool _isDisposed;

    public string? WatchedPath { get; private set; }
    public bool IsRunning { get; private set; }
    public int DebounceMilliseconds { get; set; } = DefaultDebounceMs;

    public event EventHandler<DirectoryChangeBatch>? BatchReady;
    public event EventHandler<Exception>? ErrorOccurred;

    public DirectoryWatcher(int debounceMilliseconds = DefaultDebounceMs)
    {
        DebounceMilliseconds = Math.Max(10, debounceMilliseconds);
        _pendingChanges = new Dictionary<string, FileChangeEvent>(_pathComparer);
        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start(string directoryPath)
    {
        if (_isDisposed) return;
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            Stop();
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(directoryPath);
        }
        catch
        {
            Stop();
            return;
        }

        lock (_gate)
        {
            if (IsRunning && _pathComparer.Equals(WatchedPath, fullPath))
            {
                return;
            }
        }

        Stop();

        lock (_gate)
        {
            WatchedPath = fullPath;
        }

        try
        {
            if (!Directory.Exists(fullPath))
            {
                return;
            }

            var watcher = new FileSystemWatcher(fullPath)
            {
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.Size |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Attributes,
                IncludeSubdirectories = false,
                InternalBufferSize = BufferSize
            };

            watcher.Created += OnWatcherCreated;
            watcher.Deleted += OnWatcherDeleted;
            watcher.Changed += OnWatcherChanged;
            watcher.Renamed += OnWatcherRenamed;
            watcher.Error += OnWatcherError;

            watcher.EnableRaisingEvents = true;

            lock (_gate)
            {
                if (_isDisposed)
                {
                    watcher.Dispose();
                    return;
                }
                _watcher = watcher;
                IsRunning = true;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _watcher?.Dispose();
                _watcher = null;
                IsRunning = false;
            }
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    public void Stop()
    {
        FileSystemWatcher? oldWatcher = null;
        lock (_gate)
        {
            oldWatcher = _watcher;
            _watcher = null;
            IsRunning = false;
            WatchedPath = null;
            _pendingChanges.Clear();
            _firstEventUtcTicks = 0;
        }

        _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (oldWatcher != null)
        {
            try
            {
                oldWatcher.EnableRaisingEvents = false;
                oldWatcher.Created -= OnWatcherCreated;
                oldWatcher.Deleted -= OnWatcherDeleted;
                oldWatcher.Changed -= OnWatcherChanged;
                oldWatcher.Renamed -= OnWatcherRenamed;
                oldWatcher.Error -= OnWatcherError;
                oldWatcher.Dispose();
            }
            catch { }
        }
    }

    private void OnWatcherCreated(object sender, FileSystemEventArgs e) =>
        EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Created, e.FullPath));

    private void OnWatcherDeleted(object sender, FileSystemEventArgs e) =>
        EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Deleted, e.FullPath));

    private void OnWatcherChanged(object sender, FileSystemEventArgs e) =>
        EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Changed, e.FullPath));

    private void OnWatcherRenamed(object sender, RenamedEventArgs e) =>
        EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Renamed, e.FullPath, e.OldFullPath));

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_isDisposed) return;
        var ex = e.GetException();
        ErrorOccurred?.Invoke(this, ex);

        string currentWatched;
        lock (_gate)
        {
            currentWatched = WatchedPath ?? string.Empty;
            _pendingChanges.Clear();
            _firstEventUtcTicks = 0;
        }

        _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (!string.IsNullOrEmpty(currentWatched))
        {
            // Trigger an overflow batch to request a safe full refresh
            BatchReady?.Invoke(this, new DirectoryChangeBatch(currentWatched, Array.Empty<FileChangeEvent>(), IsOverflow: true));
        }
    }

    public void EnqueueChange(FileChangeEvent change)
    {
        if (_isDisposed) return;

        lock (_gate)
        {
            if (!IsRunning || WatchedPath == null) return;

            CoalesceLocked(change);

            if (_firstEventUtcTicks == 0)
            {
                _firstEventUtcTicks = DateTime.UtcNow.Ticks;
            }

            long elapsedMs = (DateTime.UtcNow.Ticks - _firstEventUtcTicks) / TimeSpan.TicksPerMillisecond;
            int delay = (int)Math.Min(DebounceMilliseconds, Math.Max(10, MaxDebounceMs - elapsedMs));

            try
            {
                _debounceTimer?.Change(delay, Timeout.Infinite);
            }
            catch (ObjectDisposedException) { }
        }
    }

    private void CoalesceLocked(FileChangeEvent change)
    {
        switch (change.Kind)
        {
            case DirectoryChangeKind.Created:
                if (_pendingChanges.TryGetValue(change.FullPath, out var existingCreated))
                {
                    if (existingCreated.Kind == DirectoryChangeKind.Deleted)
                    {
                        // File was deleted then created: treat as changed
                        _pendingChanges[change.FullPath] = change with { Kind = DirectoryChangeKind.Changed };
                    }
                }
                else
                {
                    _pendingChanges[change.FullPath] = change;
                }
                break;

            case DirectoryChangeKind.Deleted:
                if (_pendingChanges.TryGetValue(change.FullPath, out var existingDeleted))
                {
                    if (existingDeleted.Kind == DirectoryChangeKind.Created)
                    {
                        // Created and deleted within same window: cancel out completely
                        _pendingChanges.Remove(change.FullPath);
                    }
                    else
                    {
                        _pendingChanges[change.FullPath] = change;
                    }
                }
                else
                {
                    _pendingChanges[change.FullPath] = change;
                }
                break;

            case DirectoryChangeKind.Changed:
                if (_pendingChanges.TryGetValue(change.FullPath, out var existingChanged))
                {
                    if (existingChanged.Kind == DirectoryChangeKind.Created)
                    {
                        // Created + Changed -> stays Created
                    }
                    else
                    {
                        _pendingChanges[change.FullPath] = change;
                    }
                }
                else
                {
                    _pendingChanges[change.FullPath] = change;
                }
                break;

            case DirectoryChangeKind.Renamed:
                if (change.OldFullPath != null && _pendingChanges.TryGetValue(change.OldFullPath, out var existingOld))
                {
                    _pendingChanges.Remove(change.OldFullPath);
                    if (existingOld.Kind == DirectoryChangeKind.Created)
                    {
                        // Created + Renamed -> Created at new path
                        _pendingChanges[change.FullPath] = change with { Kind = DirectoryChangeKind.Created, OldFullPath = null };
                    }
                    else if (existingOld.Kind == DirectoryChangeKind.Renamed)
                    {
                        // Renamed(A->B) + Renamed(B->C) -> Renamed(A->C)
                        _pendingChanges[change.FullPath] = change with { OldFullPath = existingOld.OldFullPath };
                    }
                    else
                    {
                        _pendingChanges[change.FullPath] = change;
                    }
                }
                else
                {
                    _pendingChanges[change.FullPath] = change;
                }
                break;
        }
    }

    private void OnDebounceTimerElapsed(object? state)
    {
        if (_isDisposed) return;

        List<FileChangeEvent> batchList;
        string dirPath;

        lock (_gate)
        {
            if (_pendingChanges.Count == 0 || WatchedPath == null) return;

            batchList = _pendingChanges.Values.ToList();
            dirPath = WatchedPath;
            _pendingChanges.Clear();
            _firstEventUtcTicks = 0;
        }

        BatchReady?.Invoke(this, new DirectoryChangeBatch(dirPath, batchList));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Stop();

        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }
}
