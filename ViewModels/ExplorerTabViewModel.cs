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
    private long _loadGeneration = 0;
    private long _filterGeneration = 0;
    private int _selectionAnchorIndex = -1;
    private bool _isDisposed;
    private DirectoryReadOptions _directoryReadOptions = DirectoryReadOptions.FromSettings(SettingsService.Instance.CurrentSettings);

    /// <summary>
    /// After a back/up navigation, holds the path of the folder we came from
    /// so it can be auto-selected once the directory listing loads.
    /// </summary>
    public string? PendingSelectPath { get; set; }

    /// <summary>
    /// Raised when a navigation-context item should be scrolled into view.
    /// </summary>
    public event Action<FileItem>? ScrollIntoViewRequested;

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
        // Remember the folder we're leaving so we can re-select it after loading
        PendingSelectPath = History[HistoryIndex];
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
            // Remember the folder we're leaving so we can re-select it after loading
            PendingSelectPath = CurrentPath;
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
            var (list, error) = await FileSystemService.Instance.ReadDirectoryAsync(CurrentPath, token, _directoryReadOptions);
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

            ClearThumbnailSelection();
            Items = new ObservableCollection<FileItem>(list);
            await ApplyFilterAsync(token);

            // After the filtered list is ready, auto-select the folder we navigated from (if any)
            var pendingPath = PendingSelectPath;
            PendingSelectPath = null;
            if (!string.IsNullOrEmpty(pendingPath))
            {
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                var match = FilteredItems.FirstOrDefault(f =>
                    string.Equals(f.FullPath, pendingPath, comparison));
                if (match != null)
                {
                    SelectedItem = match;
                    ScrollIntoViewRequested?.Invoke(match);
                }
            }
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
    partial void OnIsFilterRegexChanged(bool value) => _ = ApplyFilterAsync();
    partial void OnIsFilterWildcardChanged(bool value) => _ = ApplyFilterAsync();

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
                            _ = ApplyFilterAsync(token);
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
        var result = BuildFilteredItems(Items, FilterText, IsFilterRegex, SortColumn, SortAscending);
        FilteredItems = new ObservableCollection<FileItem>(result);
    }

    public async Task ApplyFilterAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || Items == null) return;
        long generation = Interlocked.Increment(ref _filterGeneration);
        var snapshot = Items.ToArray();
        string filterText = FilterText;
        bool isRegex = IsFilterRegex;
        string sortColumn = SortColumn;
        bool sortAscending = SortAscending;

        var result = await Task.Run(
            () => BuildFilteredItems(snapshot, filterText, isRegex, sortColumn, sortAscending),
            cancellationToken);
        if (!_isDisposed && !cancellationToken.IsCancellationRequested && generation == _filterGeneration)
        {
            FilteredItems = new ObservableCollection<FileItem>(result);
        }
    }

    private static List<FileItem> BuildFilteredItems(
        IEnumerable<FileItem> source,
        string filterText,
        bool isFilterRegex,
        string sortColumn,
        bool sortAscending)
    {
        IEnumerable<FileItem> query = source;

        if (!string.IsNullOrWhiteSpace(filterText))
        {
            if (isFilterRegex)
            {
                try
                {
                    var regex = new Regex(filterText, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
                    query = query.Where(i => regex.IsMatch(i.Name) || regex.IsMatch(i.Extension));
                }
                catch
                {
                    query = query.Where(i => i.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (filterText.Contains('*') || filterText.Contains('?'))
            {
                var glob = "^" + Regex.Escape(filterText).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                try
                {
                    var regex = new Regex(glob, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
                    query = query.Where(i => regex.IsMatch(i.Name));
                }
                catch
                {
                    query = query.Where(i => i.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                query = query.Where(i => i.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                                         i.Extension.Contains(filterText, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Sort: Folders always on top, then sort column
        IOrderedEnumerable<FileItem> sorted;
        if (sortAscending)
        {
            sorted = sortColumn switch
            {
                "Extension" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Size" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.SizeBytes).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Modified" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.ModifiedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Created" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.CreatedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Accessed" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.AccessedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Type" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Attributes" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.AttributesString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Permissions" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.PermissionsString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "OwnerGroup" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.OwnerGroupString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                _ => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase)
            };
        }
        else
        {
            sorted = sortColumn switch
            {
                "Extension" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Size" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.SizeBytes).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Modified" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.ModifiedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Created" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.CreatedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Accessed" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.AccessedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Type" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Attributes" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.AttributesString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Permissions" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.PermissionsString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "OwnerGroup" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.OwnerGroupString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                _ => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase)
            };
        }

        return sorted.ToList();
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
        _ = ApplyFilterAsync();
    }

    public bool SetDirectoryReadOptions(DirectoryReadOptions options)
    {
        if (_directoryReadOptions == options) return false;
        _directoryReadOptions = options;
        return true;
    }

    public void SelectThumbnailItem(FileItem item, bool control, bool shift)
    {
        int itemIndex = FilteredItems.IndexOf(item);
        if (itemIndex < 0) return;

        if (shift)
        {
            int anchor = _selectionAnchorIndex >= 0 ? _selectionAnchorIndex : itemIndex;
            if (!control) ClearThumbnailSelection();
            int start = Math.Min(anchor, itemIndex);
            int end = Math.Max(anchor, itemIndex);
            for (int index = start; index <= end; index++) AddThumbnailSelection(FilteredItems[index]);
        }
        else if (control)
        {
            if (item.IsThumbnailSelected) RemoveThumbnailSelection(item);
            else AddThumbnailSelection(item);
            _selectionAnchorIndex = itemIndex;
        }
        else
        {
            ClearThumbnailSelection();
            AddThumbnailSelection(item);
            _selectionAnchorIndex = itemIndex;
        }

        SelectedItem = item.IsThumbnailSelected ? item : SelectedItems.LastOrDefault();
    }

    public void ClearThumbnailSelection()
    {
        foreach (var selected in SelectedItems) selected.IsThumbnailSelected = false;
        SelectedItems.Clear();
        SelectedItem = null;
    }

    public void SelectAllThumbnails()
    {
        foreach (var item in FilteredItems) item.IsThumbnailSelected = true;
        SelectedItems = new ObservableCollection<FileItem>(FilteredItems);
        SelectedItem = SelectedItems.LastOrDefault();
        _selectionAnchorIndex = SelectedItems.Count > 0 ? SelectedItems.Count - 1 : -1;
    }

    private void AddThumbnailSelection(FileItem item)
    {
        if (item.IsThumbnailSelected) return;
        item.IsThumbnailSelected = true;
        SelectedItems.Add(item);
    }

    private void RemoveThumbnailSelection(FileItem item)
    {
        if (!item.IsThumbnailSelected) return;
        item.IsThumbnailSelected = false;
        SelectedItems.Remove(item);
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

    }
}
