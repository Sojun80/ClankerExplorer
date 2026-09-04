using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.AppLayer.Operations;

namespace ClankerExplorer.ViewModels;

public partial class OperationsViewModel : ObservableObject, IDisposable
{
    private readonly IOperationManager _operationManager;
    private readonly Action _operationsChangedHandler;
    private bool _isDisposed;

    [ObservableProperty]
    private ObservableCollection<OperationJobViewModel> _activeJobs = new();

    [ObservableProperty]
    private ObservableCollection<OperationJobViewModel> _historyJobs = new();

    [ObservableProperty]
    private int _runningCount;

    [ObservableProperty]
    private int _needsAttentionCount;

    [ObservableProperty]
    private int _queuedCount;

    [ObservableProperty]
    private double _overallProgressPercentage;

    [ObservableProperty]
    private string _summaryStatusText = "⚡ Operations";

    [ObservableProperty]
    private bool _hasActiveOperations;

    [ObservableProperty]
    private bool _hasAttention;

    [ObservableProperty]
    private bool _hasRunningOperations;

    [ObservableProperty]
    private bool _hasQueuedOperations;

    [ObservableProperty]
    private bool _hasHistoryOperations;

    [ObservableProperty]
    private string _selectedFilter = "All"; // "All", "Active", "Completed"

    public event Action? RequestClose;

    public OperationsViewModel(IOperationManager? operationManager = null)
    {
        _operationManager = operationManager ?? OperationManager.Instance;
        _operationsChangedHandler = RefreshFromManager;
        _operationManager.OperationsChanged += _operationsChangedHandler;
        RefreshFromManager();
    }

    private void RefreshFromManager()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed) return;

            RunningCount = _operationManager.RunningCount;
            NeedsAttentionCount = _operationManager.NeedsAttentionCount;
            QueuedCount = _operationManager.QueuedCount;
            OverallProgressPercentage = _operationManager.OverallProgressPercentage;
            SummaryStatusText = _operationManager.SummaryStatusText;
            HasActiveOperations = _operationManager.HasActiveOperations;
            HasAttention = NeedsAttentionCount > 0;
            HasRunningOperations = RunningCount > 0;
            HasQueuedOperations = QueuedCount > 0;
            HasHistoryOperations = _operationManager.HistoryJobs.Count > 0;

            // Synchronize ActiveJobs
            var activeSource = _operationManager.ActiveJobs;
            SyncCollection(ActiveJobs, activeSource);

            // Synchronize HistoryJobs
            var historySource = _operationManager.HistoryJobs;
            SyncCollection(HistoryJobs, historySource);
        });
    }

    private static void SyncCollection(ObservableCollection<OperationJobViewModel> target, System.Collections.Generic.IReadOnlyList<OperationJob> source)
    {
        var sourceIds = new System.Collections.Generic.HashSet<string>(source.Select(s => s.Id));

        // Remove stale items
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!sourceIds.Contains(target[i].Job.Id))
            {
                var removed = target[i];
                target.RemoveAt(i);
                removed.Dispose();
            }
        }

        // Add or update
        for (int i = 0; i < source.Count; i++)
        {
            var srcJob = source[i];
            var existing = target.FirstOrDefault(vm => vm.Job.Id == srcJob.Id);
            if (existing == null)
            {
                target.Insert(Math.Min(i, target.Count), new OperationJobViewModel(srcJob));
            }
            else
            {
                var curIndex = target.IndexOf(existing);
                if (curIndex != i && i < target.Count)
                {
                    target.Move(curIndex, i);
                }
            }
        }
    }

    [RelayCommand]
    public void PauseJob(OperationJobViewModel? jobVm)
    {
        if (jobVm == null) return;
        _operationManager.PauseJob(jobVm.Job.Id);
    }

    [RelayCommand]
    public void ResumeJob(OperationJobViewModel? jobVm)
    {
        if (jobVm == null) return;
        _operationManager.ResumeJob(jobVm.Job.Id);
    }

    [RelayCommand]
    public void CancelJob(OperationJobViewModel? jobVm)
    {
        if (jobVm == null) return;
        _operationManager.CancelJob(jobVm.Job.Id);
    }

    [RelayCommand]
    public void ResolveConflict(ConflictResolutionArgs? args)
    {
        if (args == null || string.IsNullOrEmpty(args.JobId)) return;
        _operationManager.ResolveConflict(args.JobId, new ConflictResolution(
            args.Action,
            args.ApplyToAllRemaining,
            args.CustomNewName));
    }

    [RelayCommand]
    public void ClearCompleted()
    {
        _operationManager.ClearCompleted();
    }

    [RelayCommand]
    public void CloseWorkspace()
    {
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _operationManager.OperationsChanged -= _operationsChangedHandler;
        foreach (var jobVm in ActiveJobs)
        {
            jobVm.Dispose();
        }
        ActiveJobs.Clear();
        foreach (var jobVm in HistoryJobs)
        {
            jobVm.Dispose();
        }
        HistoryJobs.Clear();
    }
}

public partial class OperationJobViewModel : ObservableObject, IDisposable
{
    public OperationJob Job { get; }
    private string? _lastConflictPath;
    private readonly Action<OperationJob> _jobChangedHandler;
    private bool _isDisposed;

    [ObservableProperty]
    private string _inlineNewName = string.Empty;

    [ObservableProperty]
    private bool _applyToRemaining;

    public string DetailsToggleText => Job.IsExpanded ? "Hide Details" : "Show Details";

    public bool IsNeedsAttention => Job.State == OperationState.NeedsAttention || Job.CurrentConflict != null;

    public bool HasWarnings => Job.Summary?.WarningCount > 0;

    public OperationJobViewModel(OperationJob job)
    {
        Job = job;
        _jobChangedHandler = _ => NotifyProperties();
        Job.JobChanged += _jobChangedHandler;
        if (Job.CurrentConflict != null)
        {
            _lastConflictPath = Job.CurrentConflict.SourcePath;
            InlineNewName = System.IO.Path.GetFileName(Job.CurrentConflict.SuggestedRenamePath);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Job.JobChanged -= _jobChangedHandler;
    }

    private void NotifyProperties()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed) return;
            OnPropertyChanged(nameof(Job));
            OnPropertyChanged(nameof(DetailsToggleText));
            OnPropertyChanged(nameof(IsNeedsAttention));
            OnPropertyChanged(nameof(HasWarnings));
            if (Job.CurrentConflict != null)
            {
                var conflictPath = Job.CurrentConflict.SourcePath;
                if (!string.Equals(_lastConflictPath, conflictPath, StringComparison.OrdinalIgnoreCase))
                {
                    _lastConflictPath = conflictPath;
                    InlineNewName = System.IO.Path.GetFileName(Job.CurrentConflict.SuggestedRenamePath);
                }
            }
            else
            {
                _lastConflictPath = null;
            }
        });
    }

    [RelayCommand]
    public void ToggleExpanded()
    {
        Job.IsExpanded = !Job.IsExpanded;
        OnPropertyChanged(nameof(DetailsToggleText));
    }

    [RelayCommand]
    public void ResolveReplace()
    {
        Job.ResolveConflict(new ConflictResolution(ConflictAction.Replace, ApplyToRemaining));
    }

    [RelayCommand]
    public void ResolveKeepBoth()
    {
        Job.ResolveConflict(new ConflictResolution(ConflictAction.KeepBoth, ApplyToRemaining));
    }

    [RelayCommand]
    public void ResolveSkip()
    {
        Job.ResolveConflict(new ConflictResolution(ConflictAction.Skip, ApplyToRemaining));
    }

    [RelayCommand]
    public void ResolveRename()
    {
        var name = string.IsNullOrWhiteSpace(InlineNewName)
            ? (Job.CurrentConflict != null ? System.IO.Path.GetFileName(Job.CurrentConflict.SuggestedRenamePath) : null)
            : InlineNewName.Trim();

        Job.ResolveConflict(new ConflictResolution(ConflictAction.Rename, ApplyToRemaining, name));
    }
}

public sealed record ConflictResolutionArgs(
    string JobId,
    ConflictAction Action,
    bool ApplyToAllRemaining,
    string? CustomNewName);
