using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ClankerExplorer.AppLayer;

namespace ClankerExplorer.AppLayer.Operations;

public partial class OperationJob : ObservableObject
{
    private readonly object _syncRoot = new();
    private readonly List<OperationError> _errors = new();
    private readonly List<OperationLogEntry> _events = new();
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<bool> _pauseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<ConflictResolution>? _conflictTcs;

    public string Id { get; }
    public OperationType Type { get; }
    public string DisplayName { get; }
    public IReadOnlyList<string> SourcePaths { get; }
    public string DestinationDirectory { get; }
    public FileConflictPolicy ConflictPolicy { get; }
    public DateTimeOffset CreatedTime { get; }
    public CancellationToken CancellationToken => _cts.Token;
    public TaskCompletionSource<FileTransferResult> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<FileTransferResult> CompletionTask => CompletionSource.Task;

    [ObservableProperty]
    private DateTimeOffset? _startedTime;

    [ObservableProperty]
    private DateTimeOffset? _finishedTime;

    [ObservableProperty]
    private OperationState _state = OperationState.Queued;

    [ObservableProperty]
    private OperationProgress _progress = OperationProgress.Empty;

    [ObservableProperty]
    private OperationConflict? _currentConflict;

    [ObservableProperty]
    private OperationSummary? _summary;

    [ObservableProperty]
    private bool _isExpanded;

    public IReadOnlyList<OperationError> Errors
    {
        get { lock (_syncRoot) return _errors.ToList(); }
    }

    public IReadOnlyList<OperationLogEntry> Events
    {
        get { lock (_syncRoot) return _events.ToList(); }
    }

    public event Action<OperationJob>? JobChanged;

    public OperationJob(
        OperationType type,
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        FileConflictPolicy conflictPolicy = FileConflictPolicy.Prompt,
        string? displayName = null,
        string? id = null)
    {
        Id = id ?? $"op_{Guid.NewGuid():N}"[..11];
        Type = type;
        SourcePaths = sourcePaths ?? Array.Empty<string>();
        DestinationDirectory = destinationDirectory ?? string.Empty;
        ConflictPolicy = conflictPolicy;
        CreatedTime = DateTimeOffset.Now;

        DisplayName = displayName ?? GenerateDefaultDisplayName(type, SourcePaths, DestinationDirectory);
        _pauseTcs.SetResult(true); // Initially not paused
    }

    private static string GenerateDefaultDisplayName(OperationType type, IReadOnlyList<string> sources, string destination)
    {
        var count = sources.Count;
        var destName = string.IsNullOrWhiteSpace(destination) ? string.Empty : System.IO.Path.GetFileName(destination.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(destName)) destName = destination;

        var typeStr = type switch
        {
            OperationType.Copy => "Copy",
            OperationType.Move => "Move",
            OperationType.Delete => "Delete",
            _ => "Operation"
        };

        if (count == 0) return $"{typeStr} to {destName}";
        if (count == 1)
        {
            var sourceName = System.IO.Path.GetFileName(sources[0].TrimEnd('\\', '/'));
            return $"{typeStr} '{sourceName}' to {destName}";
        }

        return $"{typeStr} {count:N0} items to {destName}";
    }

    public void AddLog(string message, OperationLogLevel level = OperationLogLevel.Info)
    {
        lock (_syncRoot)
        {
            _events.Add(new OperationLogEntry(DateTimeOffset.Now, message, level));
        }
        NotifyStateChanged();
    }

    public void AddError(string filePath, string message, bool isFatal = false)
    {
        lock (_syncRoot)
        {
            _errors.Add(new OperationError(filePath, message, DateTimeOffset.Now, isFatal));
            _events.Add(new OperationLogEntry(DateTimeOffset.Now, $"Error: {message} ({filePath})", OperationLogLevel.Error));
        }
        NotifyStateChanged();
    }

    public void UpdateProgress(OperationProgress newProgress)
    {
        Progress = newProgress;
        NotifyStateChanged();
    }

    public void SetState(OperationState newState)
    {
        if (State == newState) return;
        State = newState;
        if (newState == OperationState.Running && StartedTime == null)
        {
            StartedTime = DateTimeOffset.Now;
        }
        else if (newState is OperationState.Completed or OperationState.Failed or OperationState.Cancelled)
        {
            FinishedTime = DateTimeOffset.Now;
        }
        NotifyStateChanged();
    }

    public void RequestPause()
    {
        lock (_syncRoot)
        {
            if (State != OperationState.Running) return;
            _pauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetState(OperationState.Paused);
            AddLog("Operation paused by user.");
        }
    }

    public void RequestResume()
    {
        lock (_syncRoot)
        {
            if (State != OperationState.Paused) return;
            _pauseTcs.TrySetResult(true);
            SetState(OperationState.Running);
            AddLog("Operation resumed.");
        }
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task pauseTask;
        lock (_syncRoot)
        {
            pauseTask = _pauseTcs.Task;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken);
        var completed = await Task.WhenAny(pauseTask, Task.Delay(Timeout.Infinite, linkedCts.Token)).ConfigureAwait(false);
        if (completed != pauseTask)
        {
            linkedCts.Token.ThrowIfCancellationRequested();
        }
    }

    public void RequestCancel()
    {
        lock (_syncRoot)
        {
            if (State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled) return;
            try { _cts.Cancel(); } catch { }
            _pauseTcs.TrySetResult(false);
            _conflictTcs?.TrySetCanceled();
            SetState(OperationState.Cancelled);
            AddLog("Operation cancelled by user.", OperationLogLevel.Warning);
        }
    }

    public Task<ConflictResolution> PromptConflictAsync(OperationConflict conflict, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            CurrentConflict = conflict;
            SetState(OperationState.NeedsAttention);
            AddLog($"Conflict detected: destination already contains '{System.IO.Path.GetFileName(conflict.DestinationPath)}'", OperationLogLevel.Warning);

            _conflictTcs = new TaskCompletionSource<ConflictResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var tcs = _conflictTcs;
        cancellationToken.Register(() => tcs.TrySetCanceled());
        CancellationToken.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    public void ResolveConflict(ConflictResolution resolution)
    {
        lock (_syncRoot)
        {
            if (_conflictTcs != null && !_conflictTcs.Task.IsCompleted)
            {
                CurrentConflict = null;
                _conflictTcs.TrySetResult(resolution);
                SetState(OperationState.Running);
                AddLog($"Conflict resolved with action: {resolution.Action}" + (resolution.ApplyToAllRemaining ? " (Applied to all remaining)" : ""));
            }
        }
    }

    public void Complete(FileTransferResult result, OperationSummary summary)
    {
        Summary = summary;
        var hasFailures = summary.FailedCount > 0 && summary.SucceededCount == 0;
        SetState(hasFailures ? OperationState.Failed : OperationState.Completed);
        CompletionSource.TrySetResult(result);
        AddLog($"Operation finished. {summary.SucceededCount} succeeded, {summary.SkippedCount} skipped, {summary.FailedCount} failed.");
    }

    public void Fail(Exception ex)
    {
        AddError(string.Empty, ex.Message, isFatal: true);
        SetState(OperationState.Failed);
        CompletionSource.TrySetException(ex);
        AddLog($"Operation failed: {ex.Message}", OperationLogLevel.Error);
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(Events));
        JobChanged?.Invoke(this);
    }
}
