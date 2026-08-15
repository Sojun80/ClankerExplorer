using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class ExplorerTabViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _filterDebounceCts;
    private CancellationTokenSource? _thumbnailCts;
    private long _loadGeneration = 0;
    private bool _isDisposed;

    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _title = "Root";

    [ObservableProperty]
    private string _currentPath = FileSystemService.DefaultRootPath;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private DateTime _lastActiveTime = DateTime.Now;

    [ObservableProperty]
    private bool _isBeingDragged;

    [ObservableProperty]
    private bool _isDropTargetLeft;

    [ObservableProperty]
    private bool _isDropTargetRight;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _isFilterRegex;

    [ObservableProperty]
    private bool _isFilterWildcard;

    [ObservableProperty]
    private bool _isFilterBarOpen;

    [ObservableProperty]
    private string _sortColumn = "Name";

    [ObservableProperty]
    private bool _sortAscending = true;

    [ObservableProperty]
    private FileItem? _selectedItem;

    [ObservableProperty]
    private ObservableCollection<FileItem> _items = new();

    [ObservableProperty]
    private ObservableCollection<FileItem> _filteredItems = new();

    [ObservableProperty]
    private ObservableCollection<FileItem> _selectedItems = new();

    public List<string> History { get; } = new();
    public int HistoryIndex { get; private set; } = -1;

    public bool CanGoBack => HistoryIndex > 0;
    public bool CanGoForward => HistoryIndex < History.Count - 1;

    public ExplorerTabViewModel(string? initialPath = null)
    {
        initialPath ??= FileSystemService.DefaultRootPath;
        ClipboardFileService.ClipboardChanged += UpdateCutStatus;
        NavigateTo(initialPath);
    }

    public void UpdateCutStatus()
    {
        if (Items == null || _isDisposed) return;
        foreach (var item in Items)
        {
            item.IsCut = ClipboardFileService.IsPathCut(item.FullPath);
        }
    }

    public void NavigateTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _isDisposed) return;

        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            // Invalid path format
        }

        if (HistoryIndex < History.Count - 1)
        {
            History.RemoveRange(HistoryIndex + 1, History.Count - (HistoryIndex + 1));
        }

        History.Add(path);
        HistoryIndex = History.Count - 1;

        CurrentPath = path;
        UpdateTitle(path);
        _ = RefreshAsync();

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoBack()
    {
        if (!CanGoBack || _isDisposed) return;
        HistoryIndex--;
        CurrentPath = History[HistoryIndex];
        UpdateTitle(CurrentPath);
        _ = RefreshAsync();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoForward()
    {
        if (!CanGoForward || _isDisposed) return;
        HistoryIndex++;
        CurrentPath = History[HistoryIndex];
        UpdateTitle(CurrentPath);
        _ = RefreshAsync();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoUp()
    {
        if (_isDisposed) return;
        var parent = Directory.GetParent(CurrentPath);
        if (parent != null)
        {
            NavigateTo(parent.FullName);
        }
    }

    private void UpdateTitle(string path)
    {
        var dirName = Path.GetFileName(path.TrimEnd('\\', '/'));
        Title = string.IsNullOrEmpty(dirName) ? path : dirName;
    }

    public void Refresh()
    {
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_isDisposed) return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        long generation = Interlocked.Increment(ref _loadGeneration);

        IsLoading = true;
        StatusMessage = "Loading...";

        try
        {
            var (list, error) = await FileSystemService.Instance.ReadDirectoryAsync(CurrentPath, token);
            if (token.IsCancellationRequested || generation != _loadGeneration || _isDisposed) return;

            if (error != null)
            {
                StatusMessage = error;
            }
            else
            {
                StatusMessage = string.Empty;
                HistoryService.Instance.RecordFolderVisit(CurrentPath);
            }

            foreach (var item in list)
            {
                item.IsCut = ClipboardFileService.IsPathCut(item.FullPath);
            }

            Items = new ObservableCollection<FileItem>(list);
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (generation == _loadGeneration && !_isDisposed)
            {
                IsLoading = false;
            }
        }
    }

    partial void OnFilterTextChanged(string value) => ScheduleDebouncedFilter();
    partial void OnIsFilterRegexChanged(bool value) => ApplyFilter();
    partial void OnIsFilterWildcardChanged(bool value) => ApplyFilter();

    private void ScheduleDebouncedFilter()
    {
        _filterDebounceCts?.Cancel();
        _filterDebounceCts = new CancellationTokenSource();
        var token = _filterDebounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, token);
                if (!token.IsCancellationRequested && !_isDisposed)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested && !_isDisposed)
                        {
                            ApplyFilter();
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public void ApplyFilter()
    {
        if (_isDisposed || Items == null) return;
        IEnumerable<FileItem> query = Items;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            if (IsFilterRegex)
            {
                try
                {
                    var regex = new Regex(FilterText, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
                    query = query.Where(i => regex.IsMatch(i.Name) || regex.IsMatch(i.Extension));
                }
                catch
                {
                    query = query.Where(i => i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (FilterText.Contains('*') || FilterText.Contains('?'))
            {
                var glob = "^" + Regex.Escape(FilterText).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                try
                {
                    var regex = new Regex(glob, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
                    query = query.Where(i => regex.IsMatch(i.Name));
                }
                catch
                {
                    query = query.Where(i => i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                query = query.Where(i => i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                                         i.Extension.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Sort: Folders always on top, then sort column
        IOrderedEnumerable<FileItem> sorted;
        if (SortAscending)
        {
            sorted = SortColumn switch
            {
                "Extension" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Size" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.SizeBytes).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Modified" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.ModifiedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Attributes" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.AttributesString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                _ => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase)
            };
        }
        else
        {
            sorted = SortColumn switch
            {
                "Extension" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Size" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.SizeBytes).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Modified" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.ModifiedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Attributes" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.AttributesString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                _ => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase)
            };
        }

        FilteredItems = new ObservableCollection<FileItem>(sorted);
    }

    public void SortBy(string column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            SortAscending = true;
        }
        ApplyFilter();
    }

    public ExplorerTabViewModel CloneTab()
    {
        var cloned = new ExplorerTabViewModel(CurrentPath)
        {
            Title = Title,
            IsPinned = IsPinned,
            FilterText = FilterText,
            IsFilterRegex = IsFilterRegex,
            IsFilterWildcard = IsFilterWildcard,
            IsFilterBarOpen = IsFilterBarOpen,
            SortColumn = SortColumn,
            SortAscending = SortAscending,
            LastActiveTime = DateTime.Now
        };

        cloned.History.Clear();
        foreach (var h in History)
        {
            cloned.History.Add(h);
        }
        cloned.HistoryIndex = HistoryIndex;

        return cloned;
    }

    public void LoadThumbnails(int targetSize)
    {
        if (_isDisposed || FilteredItems == null || FilteredItems.Count == 0) return;

        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var token = _thumbnailCts.Token;

        _ = ThumbnailService.Instance.LoadThumbnailsAsync(FilteredItems, targetSize, token);
    }

    public void CancelThumbnailLoading()
    {
        try
        {
            _thumbnailCts?.Cancel();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        ClipboardFileService.ClipboardChanged -= UpdateCutStatus;

        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
        catch { }

        try
        {
            _filterDebounceCts?.Cancel();
            _filterDebounceCts?.Dispose();
        }
        catch { }

        try
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts?.Dispose();
        }
        catch { }
    }
}
