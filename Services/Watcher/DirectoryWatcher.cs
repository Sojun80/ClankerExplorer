using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const int MaxConsecutiveFailures = 2;

    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly object _gate = new();
    private readonly StringComparer _pathComparer = PathComparer;
    private readonly Dictionary<string, FileChangeEvent> _pendingChanges;
    private readonly Dictionary<string, int> _failureCountPerPath;
    private readonly HashSet<string> _trippedPaths;

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
        _failureCountPerPath = new Dictionary<string, int>(_pathComparer);
        _trippedPaths = new HashSet<string>(_pathComparer);
        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public static string NormalizeDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            string full = Path.GetFullPath(path.Trim());
            string root = Path.GetPathRoot(full) ?? string.Empty;
            if (PathComparer.Equals(full, root))
            {
                return root.TrimEnd('\\', '/');
            }
            return full.TrimEnd('\\', '/');
        }
        catch
        {
            return (path ?? string.Empty).Trim().TrimEnd('\\', '/');
        }
    }

    public static bool PathEquals(string? pathA, string? pathB)
    {
        if (pathA == null && pathB == null) return true;
        if (pathA == null || pathB == null) return false;
        return PathComparer.Equals(
            NormalizeDirectoryPath(pathA),
            NormalizeDirectoryPath(pathB));
    }

    public static bool IsDirectChild(string? parentDirectory, string? childPath)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(childPath))
            return false;

        try
        {
            string cleanChild = childPath.Trim().TrimEnd('\\', '/');
            string? childDir = Path.GetDirectoryName(cleanChild);
            if (string.IsNullOrEmpty(childDir)) return false;

            return PathEquals(parentDirectory, childDir);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsWslPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string p = path.Trim();
        return p.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase) ||
               p.Equals(@"\\wsl$", StringComparison.OrdinalIgnoreCase) ||
               p.Equals(@"\\wsl.localhost", StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith("//wsl$/", StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase) ||
               p.Equals("//wsl$", StringComparison.OrdinalIgnoreCase) ||
               p.Equals("//wsl.localhost", StringComparison.OrdinalIgnoreCase);
    }

    public void ResetCircuitBreaker(string? path = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _trippedPaths.Clear();
                _failureCountPerPath.Clear();
            }
            else
            {
                string cleanPath = path.Trim();
                _trippedPaths.Remove(cleanPath);
                _failureCountPerPath.Remove(cleanPath);
                try
                {
                    string fullPath = Path.GetFullPath(cleanPath);
                    _trippedPaths.Remove(fullPath);
                    _failureCountPerPath.Remove(fullPath);
                }
                catch { }
            }
        }
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

        // WSL filesystems do not reliably support Win32 FileSystemWatcher notifications
        // and frequently throw or cause rapid error/restart loops. WSL paths are browsed
        // without live watchers (refreshed via manual F5 or after Clanker operations).
        if (IsWslPath(directoryPath) || IsWslPath(fullPath))
        {
            Stop();
            lock (_gate)
            {
                WatchedPath = fullPath;
                IsRunning = false;
            }
            return;
        }

        lock (_gate)
        {
            // If this path has tripped the circuit breaker due to repeated errors, do not start
            if (_trippedPaths.Contains(fullPath))
            {
                Stop();
                WatchedPath = fullPath;
                IsRunning = false;
                return;
            }

            if (IsRunning && _watcher != null && _watcher.EnableRaisingEvents && _pathComparer.Equals(WatchedPath, fullPath))
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
                Stop();
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

            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_watcher, watcher))
                    {
                        _watcher = null;
                        IsRunning = false;
                    }
                    _failureCountPerPath.TryGetValue(fullPath, out int count);
                    count++;
                    _failureCountPerPath[fullPath] = count;
                    if (count >= MaxConsecutiveFailures)
                    {
                        _trippedPaths.Add(fullPath);
                    }
                }
                watcher.Dispose();
                ErrorOccurred?.Invoke(this, ex);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _watcher?.Dispose();
                _watcher = null;
                IsRunning = false;
                _failureCountPerPath.TryGetValue(fullPath, out int count);
                count++;
                _failureCountPerPath[fullPath] = count;
                if (count >= MaxConsecutiveFailures)
                {
                    _trippedPaths.Add(fullPath);
                }
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

    private bool IsWatcherCurrent(object? sender)
    {
        lock (_gate)
        {
            return IsRunning && _watcher != null && ReferenceEquals(sender, _watcher);
        }
    }

    private void OnWatcherCreated(object sender, FileSystemEventArgs e)
    {
        if (!IsWatcherCurrent(sender)) return;
        EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Created, e.FullPath));
    }

    private void OnWatcherDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsWatcherCurrent(sender)) return;
        EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Deleted, e.FullPath));
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsWatcherCurrent(sender)) return;
        EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Changed, e.FullPath));
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsWatcherCurrent(sender)) return;
        EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Renamed, e.FullPath, e.OldFullPath));
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_isDisposed) return;
        var ex = e.GetException();
        ErrorOccurred?.Invoke(this, ex);

        FileSystemWatcher? failedWatcher;
        string currentWatched;
        bool shouldEmitOverflow = false;

        lock (_gate)
        {
            if (_watcher != null && !ReferenceEquals(sender, _watcher) && !ReferenceEquals(sender, this))
            {
                return;
            }

            currentWatched = WatchedPath ?? string.Empty;
            failedWatcher = _watcher;
            _watcher = null;
            IsRunning = false;
            _pendingChanges.Clear();
            _firstEventUtcTicks = 0;

            if (!string.IsNullOrEmpty(currentWatched))
            {
                _failureCountPerPath.TryGetValue(currentWatched, out int count);
                count++;
                _failureCountPerPath[currentWatched] = count;
                if (count >= MaxConsecutiveFailures)
                {
                    _trippedPaths.Add(currentWatched);
                    shouldEmitOverflow = false;
                }
                else
                {
                    shouldEmitOverflow = true;
                }
            }
        }

        _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (failedWatcher != null)
        {
            try
            {
                failedWatcher.EnableRaisingEvents = false;
                failedWatcher.Created -= OnWatcherCreated;
                failedWatcher.Deleted -= OnWatcherDeleted;
                failedWatcher.Changed -= OnWatcherChanged;
                failedWatcher.Renamed -= OnWatcherRenamed;
                failedWatcher.Error -= OnWatcherError;
                failedWatcher.Dispose();
            }
            catch { }
        }

        if (shouldEmitOverflow && !string.IsNullOrEmpty(currentWatched))
        {
            // Trigger an overflow batch to request a safe full refresh and watcher recreation
            RaiseBatchReady(new DirectoryChangeBatch(currentWatched, Array.Empty<FileChangeEvent>(), IsOverflow: true));
        }
    }

    public void RaiseErrorForTesting(Exception ex)
    {
        OnWatcherError(this, new ErrorEventArgs(ex));
    }

    public void EnqueueChange(FileChangeEvent change)
    {
        if (_isDisposed) return;

        lock (_gate)
        {
            if (!IsRunning || WatchedPath == null) return;

            // Strict child filtering: DirectoryWatcher monitors ONLY immediate children of WatchedPath.
            // On network shares (SMB/CIFS), Win32 FileSystemWatcher can receive notifications for files
            // in subdirectories. Those must be discarded so subfolder files never bleed into the parent directory.
            if (change.Kind == DirectoryChangeKind.Renamed)
            {
                bool newIsDirect = IsDirectChild(WatchedPath, change.FullPath);
                bool oldIsDirect = !string.IsNullOrEmpty(change.OldFullPath) && IsDirectChild(WatchedPath, change.OldFullPath);

                if (newIsDirect && oldIsDirect)
                {
                    // Regular rename within the watched directory
                }
                else if (newIsDirect)
                {
                    // Moved from outside into the watched directory -> Created
                    change = new FileChangeEvent(DirectoryChangeKind.Created, change.FullPath);
                }
                else if (oldIsDirect)
                {
                    // Moved out of the watched directory -> Deleted
                    change = new FileChangeEvent(DirectoryChangeKind.Deleted, change.OldFullPath!);
                }
                else
                {
                    // Neither path is an immediate child of WatchedPath -> ignore
                    return;
                }
            }
            else
            {
                if (!IsDirectChild(WatchedPath, change.FullPath))
                {
                    return;
                }
            }

            if (_failureCountPerPath.ContainsKey(WatchedPath))
            {
                _failureCountPerPath.Remove(WatchedPath);
            }

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

            case DirectoryChangeKind.Deleted:
                if (_pendingChanges.TryGetValue(change.FullPath, out var existingDeleted))
                {
                    if (existingDeleted.Kind == DirectoryChangeKind.Created)
                    {
                        // Created and deleted within same window: cancel out completely
                        _pendingChanges.Remove(change.FullPath);
                    }
                    else if (existingDeleted.Kind == DirectoryChangeKind.Renamed &&
                             !string.IsNullOrEmpty(existingDeleted.OldFullPath))
                    {
                        // File was Renamed(A -> B), now Deleted(B)
                        // This must ultimately remove A (what ClankerExplorer currently knows it as)
                        _pendingChanges.Remove(change.FullPath);
                        string origPath = existingDeleted.OldFullPath;
                        if (_pendingChanges.TryGetValue(origPath, out var atOrig) && atOrig.Kind == DirectoryChangeKind.Created)
                        {
                            // Created(A) -> Renamed(A -> B) -> Deleted(B): cancel out completely
                            _pendingChanges.Remove(origPath);
                        }
                        else
                        {
                            _pendingChanges[origPath] = new FileChangeEvent(DirectoryChangeKind.Deleted, origPath);
                        }
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
                    if (existingChanged.Kind == DirectoryChangeKind.Created ||
                        existingChanged.Kind == DirectoryChangeKind.Renamed)
                    {
                        // Created + Changed -> stays Created
                        // Renamed + Changed -> stays Renamed (do NOT overwrite pending rename with Changed!)
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
                        string originalSource = existingOld.OldFullPath ?? change.OldFullPath;
                        if (_pathComparer.Equals(change.FullPath, originalSource))
                        {
                            // Renamed A -> B -> A: returned to original name, treat as Changed
                            _pendingChanges[change.FullPath] = new FileChangeEvent(DirectoryChangeKind.Changed, change.FullPath);
                        }
                        else
                        {
                            _pendingChanges[change.FullPath] = change with { OldFullPath = originalSource };
                        }
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
            if (!IsRunning || _pendingChanges.Count == 0 || WatchedPath == null) return;

            batchList = _pendingChanges.Values.ToList();
            dirPath = WatchedPath;
            _pendingChanges.Clear();
            _firstEventUtcTicks = 0;
        }

        RaiseBatchReady(new DirectoryChangeBatch(dirPath, batchList));
    }

    private void RaiseBatchReady(DirectoryChangeBatch batch)
    {
        var handlers = BatchReady;
        if (handlers != null)
        {
            foreach (EventHandler<DirectoryChangeBatch> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, batch);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DirectoryWatcher subscriber error: {ex}");
                }
            }
        }
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
