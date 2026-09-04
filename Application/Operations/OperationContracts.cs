using System;
using System.Collections.Generic;
using ClankerExplorer.AppLayer;

namespace ClankerExplorer.AppLayer.Operations;

public enum OperationState
{
    Queued,
    Running,
    Paused,
    NeedsAttention,
    Completed,
    Failed,
    Cancelled
}

public enum OperationType
{
    Copy,
    Move,
    Delete
}

public enum ConflictAction
{
    Replace,
    KeepBoth,
    Skip,
    Rename
}

public enum OperationLogLevel
{
    Info,
    Warning,
    Error
}

public sealed record ConflictResolution(
    ConflictAction Action,
    bool ApplyToAllRemaining = false,
    string? CustomNewName = null);

public sealed record OperationConflict(
    string SourcePath,
    string DestinationPath,
    string SuggestedRenamePath,
    bool IsDirectory);

public sealed record OperationError(
    string FilePath,
    string Message,
    DateTimeOffset Timestamp,
    bool IsFatal = false);

public sealed record OperationLogEntry(
    DateTimeOffset Timestamp,
    string Message,
    OperationLogLevel Level = OperationLogLevel.Info);

public sealed record OperationSummary(
    long TotalFiles,
    long TotalBytes,
    TimeSpan Duration,
    int SucceededCount,
    int SkippedCount,
    int RenamedCount,
    int FailedCount,
    int WarningCount = 0);

public sealed record OperationProgress(
    string OperationType,
    string CurrentItem,
    long TotalItems,
    long ProcessedItems,
    long TotalBytes,
    long TransferredBytes,
    double Percentage,
    double BytesPerSecond,
    TimeSpan ElapsedTime,
    TimeSpan? EstimatedRemainingTime,
    OperationState State,
    int ErrorCount,
    int ConflictCount)
{
    public static OperationProgress Empty => new(
        string.Empty,
        string.Empty,
        0,
        0,
        0,
        0,
        0,
        0,
        TimeSpan.Zero,
        null,
        OperationState.Queued,
        0,
        0);

    public string FormattedSpeed => FormatSpeed(BytesPerSecond);

    public string FormattedRemaining => EstimatedRemainingTime.HasValue
        ? FormatRemainingTime(EstimatedRemainingTime.Value)
        : string.Empty;

    public string FormattedBytes => TotalBytes > 0
        ? $"{FormatBytes(TransferredBytes)} / {FormatBytes(TotalBytes)}"
        : FormatBytes(TransferredBytes);

    public string FormattedItems => TotalItems > 0
        ? $"{ProcessedItems:N0} / {TotalItems:N0} items"
        : $"{ProcessedItems:N0} items";

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:0.##} {units[unitIndex]}";
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        int unitIndex = 0;
        double speed = bytesPerSec;
        while (speed >= 1024 && unitIndex < units.Length - 1)
        {
            speed /= 1024;
            unitIndex++;
        }
        return $"{speed:0.##} {units[unitIndex]}";
    }

    public static string FormatRemainingTime(TimeSpan remaining)
    {
        if (remaining.TotalSeconds < 0) return string.Empty;
        if (remaining.TotalHours >= 1)
            return $"{Math.Ceiling(remaining.TotalHours):0.#}h remaining";
        if (remaining.TotalMinutes >= 1)
            return $"{Math.Ceiling(remaining.TotalMinutes):0.#}m remaining";
        return $"{Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0}s remaining";
    }
}

public interface IOperationManager : IDisposable
{
    IReadOnlyList<OperationJob> ActiveJobs { get; }
    IReadOnlyList<OperationJob> HistoryJobs { get; }

    int RunningCount { get; }
    int NeedsAttentionCount { get; }
    int QueuedCount { get; }
    double OverallProgressPercentage { get; }
    string SummaryStatusText { get; }
    bool HasActiveOperations { get; }
    bool HasAttention { get; }

    event Action? OperationsChanged;

    OperationJob EnqueueTransfer(FileTransferRequest request);
    void PauseJob(string jobId);
    void ResumeJob(string jobId);
    void CancelJob(string jobId);
    void ResolveConflict(string jobId, ConflictResolution resolution);
    void ClearCompleted();
}
