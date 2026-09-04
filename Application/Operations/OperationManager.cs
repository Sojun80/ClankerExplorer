using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClankerExplorer.AppLayer;

namespace ClankerExplorer.AppLayer.Operations;

public sealed class OperationManager : IOperationManager
{
    private static readonly Lazy<OperationManager> _defaultInstance = new(() => new OperationManager());
    public static OperationManager Instance => _defaultInstance.Value;

    private readonly object _syncRoot = new();
    private readonly List<OperationJob> _activeJobs = new();
    private readonly List<OperationJob> _historyJobs = new();
    private readonly Channel<OperationJob> _queueChannel;
    private readonly CancellationTokenSource _managerCts = new();
    private readonly TransferEngine _transferEngine;
    private readonly Task _workerTask;
    private bool _isDisposed;

    public IReadOnlyList<OperationJob> ActiveJobs
    {
        get { lock (_syncRoot) return _activeJobs.ToList(); }
    }

    public IReadOnlyList<OperationJob> HistoryJobs
    {
        get { lock (_syncRoot) return _historyJobs.ToList(); }
    }

    public int RunningCount
    {
        get { lock (_syncRoot) return _activeJobs.Count(j => j.State == OperationState.Running); }
    }

    public int NeedsAttentionCount
    {
        get { lock (_syncRoot) return _activeJobs.Count(j => j.State == OperationState.NeedsAttention); }
    }

    public int QueuedCount
    {
        get { lock (_syncRoot) return _activeJobs.Count(j => j.State == OperationState.Queued); }
    }

    public bool HasActiveOperations
    {
        get { lock (_syncRoot) return _activeJobs.Count > 0; }
    }

    public bool HasAttention
    {
        get { lock (_syncRoot) return NeedsAttentionCount > 0; }
    }

    public double OverallProgressPercentage
    {
        get
        {
            lock (_syncRoot)
            {
                var active = _activeJobs.Where(j => j.State is OperationState.Running or OperationState.Paused or OperationState.NeedsAttention).ToList();
                if (active.Count == 0) return 0;
                return active.Average(j => j.Progress.Percentage);
            }
        }
    }

    public string SummaryStatusText
    {
        get
        {
            lock (_syncRoot)
            {
                var attention = NeedsAttentionCount;
                if (attention > 0)
                {
                    return $"⚠ {attention}";
                }

                var running = RunningCount;
                if (running == 1)
                {
                    var percent = (int)OverallProgressPercentage;
                    return $"⚡ {percent}%";
                }
                else if (running > 1)
                {
                    return $"⚡ {running} running";
                }

                var queued = QueuedCount;
                if (queued > 0)
                {
                    return $"⚡ {queued} queued";
                }

                return "⚡ Operations";
            }
        }
    }

    public event Action? OperationsChanged;

    public OperationManager(TransferEngine? transferEngine = null)
    {
        _transferEngine = transferEngine ?? new TransferEngine();

        _queueChannel = Channel.CreateUnbounded<OperationJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _workerTask = Task.Run(ProcessQueueLoopAsync);
    }

    public OperationJob EnqueueTransfer(FileTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var opType = request.Mode == FileTransferMode.Move ? OperationType.Move : OperationType.Copy;
        var job = new OperationJob(opType, request.SourcePaths, request.DestinationDirectory, request.ConflictPolicy);

        lock (_syncRoot)
        {
            _activeJobs.Add(job);
        }

        job.JobChanged += OnJobChanged;
        NotifyChanged();

        _ = _queueChannel.Writer.WriteAsync(job);
        return job;
    }

    private void OnJobChanged(OperationJob job)
    {
        NotifyChanged();
    }

    private async Task ProcessQueueLoopAsync()
    {
        var reader = _queueChannel.Reader;

        while (!_managerCts.IsCancellationRequested)
        {
            OperationJob? job = null;
            try
            {
                job = await reader.ReadAsync(_managerCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (job == null) continue;

            if (job.State == OperationState.Cancelled || job.CancellationToken.IsCancellationRequested)
            {
                job.SetState(OperationState.Cancelled);
                job.CompletionSource.TrySetCanceled(job.CancellationToken);
                MoveToHistory(job);
                continue;
            }

            try
            {
                await job.WaitIfPausedAsync(_managerCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (job.CancellationToken.IsCancellationRequested || job.State == OperationState.Cancelled)
                {
                    job.SetState(OperationState.Cancelled);
                    job.CompletionSource.TrySetCanceled(job.CancellationToken);
                    MoveToHistory(job);
                    continue;
                }
                break;
            }

            if (job.State == OperationState.Cancelled || job.CancellationToken.IsCancellationRequested)
            {
                job.SetState(OperationState.Cancelled);
                job.CompletionSource.TrySetCanceled(job.CancellationToken);
                MoveToHistory(job);
                continue;
            }

            job.SetState(OperationState.Running);
            NotifyChanged();

            try
            {
                var request = new FileTransferRequest(
                    job.SourcePaths,
                    job.DestinationDirectory,
                    job.Type == OperationType.Move ? FileTransferMode.Move : FileTransferMode.Copy,
                    job.ConflictPolicy);

                await _transferEngine.ExecuteTransferAsync(job, request, _managerCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                job.SetState(OperationState.Cancelled);
                job.CompletionSource.TrySetCanceled(job.CancellationToken);
            }
            catch (Exception ex)
            {
                job.Fail(ex);
            }
            finally
            {
                MoveToHistory(job);
            }
        }
    }

    private void MoveToHistory(OperationJob job)
    {
        lock (_syncRoot)
        {
            _activeJobs.Remove(job);
            _historyJobs.Insert(0, job); // newest completed on top
        }
        NotifyChanged();
    }

    public void PauseJob(string jobId)
    {
        OperationJob? job;
        lock (_syncRoot)
        {
            job = _activeJobs.FirstOrDefault(j => j.Id == jobId);
        }
        job?.RequestPause();
    }

    public void ResumeJob(string jobId)
    {
        OperationJob? job;
        lock (_syncRoot)
        {
            job = _activeJobs.FirstOrDefault(j => j.Id == jobId);
        }
        job?.RequestResume();
    }

    public void CancelJob(string jobId)
    {
        OperationJob? job;
        lock (_syncRoot)
        {
            job = _activeJobs.FirstOrDefault(j => j.Id == jobId) ?? _historyJobs.FirstOrDefault(j => j.Id == jobId);
        }
        job?.RequestCancel();
    }

    public void ResolveConflict(string jobId, ConflictResolution resolution)
    {
        OperationJob? job;
        lock (_syncRoot)
        {
            job = _activeJobs.FirstOrDefault(j => j.Id == jobId);
        }
        job?.ResolveConflict(resolution);
    }

    public void ClearCompleted()
    {
        lock (_syncRoot)
        {
            _historyJobs.Clear();
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        OperationsChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _managerCts.Cancel();
            _queueChannel.Writer.TryComplete();
        }
        catch { }

        lock (_syncRoot)
        {
            foreach (var job in _activeJobs)
            {
                job.JobChanged -= OnJobChanged;
            }
        }
    }
}
