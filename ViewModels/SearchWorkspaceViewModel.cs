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
            CancelSearch();
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
            CancelSearch();
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

        // 3. Reset UI state for the new search
        Results.Clear();
        TotalResultCount = 0;
        FoldersSkippedCount = 0;
        IsSearching = true;
        StatusText = "Searching...";

        var request = new SearchRequest(query, Scope, CurrentFolderPath);

        // 4. Execute search on background thread with progressive batch streaming
        Task.Run(async () =>
        {
            var progress = new Progress<SearchProgressReport>(report =>
            {
                if (generation != _searchGeneration || _isDisposed) return;
                FoldersSkippedCount = report.FoldersSkipped;
            });

            var buffer = new List<SearchResultItem>();
            long lastFlush = Stopwatch.GetTimestamp();

            try
            {
                await foreach (var item in _searchService.SearchAsync(request, progress, token).ConfigureAwait(false))
                {
                    if (token.IsCancellationRequested || generation != _searchGeneration || _isDisposed)
                    {
                        return;
                    }

                    buffer.Add(item);

                    // Batch dispatch: flush every ~50 items or ~60ms
                    long now = Stopwatch.GetTimestamp();
                    double elapsedMs = (now - lastFlush) * 1000.0 / Stopwatch.Frequency;

                    if (buffer.Count >= 50 || elapsedMs >= 60)
                    {
                        FlushBuffer(buffer, generation, token);
                        lastFlush = now;
                    }
                }

                // Flush remaining items
                if (buffer.Count > 0)
                {
                    FlushBuffer(buffer, generation, token);
                }

                // Final status update on UI thread
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _searchGeneration || _isDisposed) return;

                    IsSearching = false;
                    if (token.IsCancellationRequested)
                    {
                        StatusText = TotalResultCount > 0
                            ? $"Search cancelled ({TotalResultCount:N0} results)"
                            : "Search cancelled";
                    }
                    else
                    {
                        StatusText = FoldersSkippedCount > 0
                            ? $"{TotalResultCount:N0} results (completed with {FoldersSkippedCount:N0} inaccessible folders skipped)"
                            : (TotalResultCount == 0 ? "No results found" : $"{TotalResultCount:N0} results");
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _searchGeneration || _isDisposed) return;
                    IsSearching = false;
                    StatusText = TotalResultCount > 0
                        ? $"Search cancelled ({TotalResultCount:N0} results)"
                        : "Search cancelled";
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _searchGeneration || _isDisposed) return;
                    IsSearching = false;
                    StatusText = $"Search error: {ex.Message}";
                });
            }
        });
    }

    private void FlushBuffer(List<SearchResultItem> buffer, long generation, CancellationToken token)
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
        CancelSearch();
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
        _debounceCts?.Cancel();
        _searchCts?.Cancel();
    }
}
