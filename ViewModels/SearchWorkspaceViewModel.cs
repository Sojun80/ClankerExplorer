using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Services.Search;

namespace ClankerExplorer.ViewModels;

public sealed record SearchScopeOption(SearchScope Scope, string DisplayName);

/// <summary>
/// ViewModel managing the dedicated Search workspace.
/// Provides debounced asynchronous search, cancellation of obsolete searches,
/// generation isolation to prevent stale result pollution, progressive batch streaming,
/// sorting, and decoupled navigation/open actions.
/// </summary>
public partial class SearchWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly SearchService _searchService;
    private readonly Func<string>? _getCurrentFolder;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _debounceCts;
    private long _searchGeneration = 0;
    private bool _isDisposed;

    private string? _lastSearchedFolder;
    private readonly object _bufferLock = new();

    public IReadOnlyList<SearchScopeOption> ScopeOptions { get; } = new[]
    {
        new SearchScopeOption(SearchScope.CurrentFolderAndSubfolders, "Subtree (Folder + Subfolders)"),
        new SearchScopeOption(SearchScope.CurrentFolder, "Current Folder Only"),
        new SearchScopeOption(SearchScope.Everywhere, "Everywhere (All Drives)")
    };

    [ObservableProperty]
    private SearchScopeOption _selectedScopeOption;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _currentFolderPath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SearchResultItem> _results = new();

    [ObservableProperty]
    private SearchResultItem? _selectedResult;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private int _totalResultCount;

    [ObservableProperty]
    private int _foldersSkippedCount;

    [ObservableProperty]
    private string _sortColumn = "Name";

    [ObservableProperty]
    private bool _sortAscending = true;

    public SearchScope Scope => SelectedScopeOption?.Scope ?? SearchScope.CurrentFolderAndSubfolders;

    public event Action? RequestClose;
    public event Action? RequestFocusSearchBox;
    public event Action<string, string?>? RequestNavigate; // (targetFolder, optionalSelectItemPath)
    public event Action<string>? RequestOpenFile;          // (targetFilePath)
    public event Action<string>? RequestSetClipboardText;

    public SearchWorkspaceViewModel(SearchService? searchService = null, Func<string>? getCurrentFolder = null)
    {
        _searchService = searchService ?? SearchService.Instance;
        _getCurrentFolder = getCurrentFolder;
        _selectedScopeOption = ScopeOptions[0];

        RefreshCurrentFolderContext();
    }

    public void RefreshCurrentFolderContext()
    {
        var path = _getCurrentFolder?.Invoke();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            path = FileSystemService.DefaultRootPath;
        }
        CurrentFolderPath = path;
    }

    public void OnWorkspaceOpened()
    {
        RefreshCurrentFolderContext();
        var newFolder = CurrentFolderPath;

        bool folderChanged = _lastSearchedFolder != null &&
            !string.Equals(_lastSearchedFolder, newFolder, SearchPathHelper.GetPathStringComparison(newFolder));

        if (folderChanged && Scope != SearchScope.Everywhere)
        {
            if (!string.IsNullOrWhiteSpace(Query))
            {
                ScheduleDebouncedSearch(delayMs: 0);
            }
            else
            {
                CancelAndInvalidateCurrentSearch();
                Results.Clear();
                TotalResultCount = 0;
                FoldersSkippedCount = 0;
                StatusText = "Enter a query to search";
                IsSearching = false;
            }
        }

        RequestFocusSearchBox?.Invoke();
    }

    public void OnWorkspaceHidden()
    {
        CancelAndInvalidateCurrentSearch();
        IsSearching = false;
    }

    private void CancelAndInvalidateCurrentSearch()
    {
        _debounceCts?.Cancel();
        _searchCts?.Cancel();
        Interlocked.Increment(ref _searchGeneration);
    }

    partial void OnSelectedScopeOptionChanged(SearchScopeOption value)
    {
        OnPropertyChanged(nameof(Scope));
        ScheduleDebouncedSearch(delayMs: 0);
    }

    partial void OnQueryChanged(string value)
    {
        ScheduleDebouncedSearch(delayMs: 200);
    }

    public void ScheduleDebouncedSearch(int delayMs = 200)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var debounceToken = _debounceCts.Token;

        if (string.IsNullOrWhiteSpace(Query))
        {
            CancelAndInvalidateCurrentSearch();
            Results.Clear();
            TotalResultCount = 0;
            FoldersSkippedCount = 0;
            StatusText = "Enter a query to search";
            IsSearching = false;
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, debounceToken).ConfigureAwait(false);
                }

                if (!debounceToken.IsCancellationRequested && !_isDisposed)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (!debounceToken.IsCancellationRequested && !_isDisposed)
                        {
                            StartSearchInternal();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Debounce superseded
            }
        });
    }

    [RelayCommand]
    public void SubmitSearch()
    {
        _debounceCts?.Cancel();
        StartSearchInternal();
    }

    private void StartSearchInternal()
    {
        string query = Query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            CancelAndInvalidateCurrentSearch();
            Results.Clear();
            TotalResultCount = 0;
            FoldersSkippedCount = 0;
            StatusText = "Enter a query to search";
            IsSearching = false;
            return;
        }

        // 1. Immediately cancel any obsolete in-progress search
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        // 2. Increment request generation to protect against stale results
        long generation = Interlocked.Increment(ref _searchGeneration);

        // 3. Track search parameters and reset UI state for the new search
        _lastSearchedFolder = CurrentFolderPath;
        Results.Clear();
        TotalResultCount = 0;
        FoldersSkippedCount = 0;
        IsSearching = true;
        StatusText = "Searching...";

        var request = new SearchRequest(query, Scope, CurrentFolderPath);

        // 4. Execute search on background thread with progressive batch streaming
        Task.Run(async () =>
        {
            int workerFoldersSkipped = 0;
            bool isTruncated = false;
            var progress = new DirectProgress<SearchProgressReport>(report =>
            {
                workerFoldersSkipped = report.FoldersSkipped;
                if (report.IsTruncated)
                {
                    isTruncated = true;
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _searchGeneration || _isDisposed) return;
                    FoldersSkippedCount = report.FoldersSkipped;
                });
            });

            var buffer = new List<SearchResultItem>();

            void FlushBufferLocked()
            {
                if (buffer.Count == 0) return;
                var batch = buffer.ToArray();
                buffer.Clear();

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _searchGeneration || token.IsCancellationRequested || _isDisposed) return;

                    foreach (var item in batch)
                    {
                        Results.Add(item);
                    }

                    TotalResultCount = Results.Count;
                    StatusText = $"Searching... ({TotalResultCount:N0} results)";
                });
            }

            using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var flushToken = flushCts.Token;
            var flushTask = Task.Run(async () =>
            {
                while (!flushToken.IsCancellationRequested && generation == _searchGeneration && !_isDisposed)
                {
                    try
                    {
                        await Task.Delay(50, flushToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    lock (_bufferLock)
                    {
                        FlushBufferLocked();
                    }
                }
            });

            try
            {
                await foreach (var item in _searchService.SearchAsync(request, progress, token).ConfigureAwait(false))
                {
                    if (token.IsCancellationRequested || generation != _searchGeneration || _isDisposed)
                    {
                        return;
                    }

                    lock (_bufferLock)
                    {
                        buffer.Add(item);
                        // Flush immediately if buffer reaches high throughput batch size (e.g. Everything provider)
                        if (buffer.Count >= 100)
                        {
                            FlushBufferLocked();
                        }
                    }
                }

                // Stop periodic flush task
                flushCts.Cancel();
                try { await flushTask.ConfigureAwait(false); } catch { }

                // Flush remaining items
                lock (_bufferLock)
                {
                    FlushBufferLocked();
                }

                // Final status update on UI thread with atomic final skipped count
                int finalSkipped = workerFoldersSkipped;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _searchGeneration || _isDisposed) return;

                    FoldersSkippedCount = finalSkipped;
                    IsSearching = false;
                    if (token.IsCancellationRequested)
                    {
                        StatusText = TotalResultCount > 0
                            ? $"Search stopped ({TotalResultCount:N0} results)"
                            : "Search stopped";
                    }
                    else if (isTruncated)
                    {
                        StatusText = $"Showing first {TotalResultCount:N0} results (result limit reached)";
                    }
                    else
                    {
                        StatusText = finalSkipped > 0
                            ? $"{TotalResultCount:N0} results (completed with {finalSkipped:N0} inaccessible folders skipped)"
                            : (TotalResultCount == 0 ? "No results found" : $"{TotalResultCount:N0} results");
                    }
                });
            }
            catch (OperationCanceledException)
            {
                flushCts.Cancel();
                try { await flushTask.ConfigureAwait(false); } catch { }

                int finalSkipped = workerFoldersSkipped;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _searchGeneration || _isDisposed) return;
                    FoldersSkippedCount = finalSkipped;
                    IsSearching = false;
                    StatusText = TotalResultCount > 0
                        ? $"Search stopped ({TotalResultCount:N0} results)"
                        : "Search stopped";
                });
            }
            catch (Exception ex)
            {
                flushCts.Cancel();
                try { await flushTask.ConfigureAwait(false); } catch { }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _searchGeneration || _isDisposed) return;
                    IsSearching = false;
                    StatusText = $"Search error: {ex.Message}";
                });
            }
        });
    }

    [RelayCommand]
    public void CancelSearch()
    {
        _searchCts?.Cancel();
    }

    [RelayCommand]
    public void ClearQuery()
    {
        Query = string.Empty;
    }

    [RelayCommand]
    public void CloseWorkspace()
    {
        CancelAndInvalidateCurrentSearch();
        IsSearching = false;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    public void OpenItem(SearchResultItem? item = null)
    {
        var target = item ?? SelectedResult;
        if (target == null) return;

        if (target.IsDirectory)
        {
            RequestNavigate?.Invoke(target.FullPath, null);
        }
        else
        {
            RequestOpenFile?.Invoke(target.FullPath);
        }
    }

    [RelayCommand]
    public void OpenContainingFolder(SearchResultItem? item = null)
    {
        var target = item ?? SelectedResult;
        if (target == null) return;

        var parent = target.ParentPath;
        if (string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(target.FullPath))
        {
            parent = Path.GetDirectoryName(target.FullPath);
        }

        if (!string.IsNullOrEmpty(parent))
        {
            RequestNavigate?.Invoke(parent, target.FullPath);
        }
    }

    [RelayCommand]
    public void CopyPath(SearchResultItem? item = null)
    {
        var target = item ?? SelectedResult;
        if (target != null && !string.IsNullOrEmpty(target.FullPath))
        {
            RequestSetClipboardText?.Invoke(target.FullPath);
        }
    }

    [RelayCommand]
    public void CopyName(SearchResultItem? item = null)
    {
        var target = item ?? SelectedResult;
        if (target != null && !string.IsNullOrEmpty(target.Name))
        {
            RequestSetClipboardText?.Invoke(target.Name);
        }
    }

    [RelayCommand]
    public void Sort(string column) => Sort(column, null);

    public void Sort(string column, bool? ascending)
    {
        if (ascending.HasValue)
        {
            SortAscending = ascending.Value;
        }
        else if (string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase))
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortAscending = true;
        }

        SortColumn = column;
        ApplySort();
    }

    public void ApplySort()
    {
        if (Results.Count <= 1) return;

        var list = Results.ToList();
        IOrderedEnumerable<SearchResultItem> sorted = SortAscending
            ? SortColumn switch
            {
                "Path" => list.OrderBy(i => i.ParentPath, NaturalStringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Size" => list.OrderBy(i => i.SizeBytes).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Modified" => list.OrderBy(i => i.ModifiedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Type" => list.OrderBy(i => i.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                _ => list.OrderBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase)
            }
            : SortColumn switch
            {
                "Path" => list.OrderByDescending(i => i.ParentPath, NaturalStringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Size" => list.OrderByDescending(i => i.SizeBytes).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Modified" => list.OrderByDescending(i => i.ModifiedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Type" => list.OrderByDescending(i => i.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                _ => list.OrderByDescending(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase)
            };

        Results = new ObservableCollection<SearchResultItem>(sorted);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CancelAndInvalidateCurrentSearch();
    }

    private sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public DirectProgress(Action<T> handler) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        public void Report(T value) => _handler(value);
    }
}
