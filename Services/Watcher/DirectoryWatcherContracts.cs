using System;
using System.Collections.Generic;

namespace ClankerExplorer.Services.Watcher;

public enum DirectoryChangeKind
{
    Created,
    Deleted,
    Changed,
    Renamed
}

public sealed record FileChangeEvent(
    DirectoryChangeKind Kind,
    string FullPath,
    string? OldFullPath = null,
    bool IsDirectory = false,
    DateTime Timestamp = default)
{
    public DateTime Timestamp { get; init; } = Timestamp == default ? DateTime.UtcNow : Timestamp;
}

public sealed record DirectoryChangeBatch(
    string DirectoryPath,
    IReadOnlyList<FileChangeEvent> Changes,
    bool IsOverflow = false);

public interface IDirectoryWatcher : IDisposable
{
    string? WatchedPath { get; }
    bool IsRunning { get; }
    int DebounceMilliseconds { get; set; }

    event EventHandler<DirectoryChangeBatch>? BatchReady;
    event EventHandler<Exception>? ErrorOccurred;

    void Start(string directoryPath);
    void Stop();
}
