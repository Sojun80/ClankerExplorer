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
    private FileSystemWatcher? _watcher;
    private long _loadGeneration = 0;
    private long _filterGeneration = 0;
    private int _selectionAnchorIndex = -1;
    private bool _isDisposed;
    private DirectoryReadOptions _directoryReadOptions = DirectoryReadOptions.FromSettings(SettingsService.Instance.CurrentSettings);

    /// <summary>
    /// Holds the paths of items to be auto-selected once the directory listing loads.
    /// Used for navigation-context restoration and paste selection continuity.
    /// </summary>
    public List<string>? PendingSelectPaths { get; set; }

    public string? PendingSelectPath
    {
        get => PendingSelectPaths?.FirstOrDefault();
        set => PendingSelectPaths = value != null ? new List<string> { value } : null;
    }

    /// <summary>
    /// Raised when a navigation-context item should be scrolled into view.
    /// </summary>
    public event Action<FileItem>? ScrollIntoViewRequested;

    /// <summary>
    /// Raised when selection has been restored/synchronized across items after refresh/filter.
    /// </summary>
    public event Action? SelectionRestored;

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

        // Capture previous selection & focus paths before reloading to preserve continuity
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        var previousSelectedPaths = SelectedItems
            .Select(i => i.FullPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(comparer)
            .ToList();

        if (previousSelectedPaths.Count == 0 && SelectedItem != null && !string.IsNullOrEmpty(SelectedItem.FullPath))
        {
            previousSelectedPaths.Add(SelectedItem.FullPath);
        }

        string? previousFocusedPath = SelectedItem?.FullPath;

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

            SetupWatcher(CurrentPath);

            var deduplicatedList = list
                .GroupBy(i => i.FullPath, comparer)
                .Select(g => g.First())
                .ToList();

            foreach (var item in deduplicatedList)
            {
                item.IsCut = ClipboardFileService.IsPathCut(item.FullPath);
            }

            Items = new ObservableCollection<FileItem>(deduplicatedList);
            await ApplyFilterAsync(token);

            // Determine selection targets: explicit navigation/paste vs ordinary refresh continuity
            var pendingPaths = PendingSelectPaths;
            PendingSelectPaths = null;
            bool isExplicitNavigationSelect = (pendingPaths != null && pendingPaths.Count > 0);
            var targetSelectPaths = isExplicitNavigationSelect ? pendingPaths! : previousSelectedPaths;

            if (targetSelectPaths != null && targetSelectPaths.Count > 0)
            {
                var matches = new List<FileItem>();
                foreach (var p in targetSelectPaths)
                {
                    var match = FilteredItems.FirstOrDefault(f => string.Equals(f.FullPath, p, comparison));
                    if (match != null && !matches.Contains(match))
                    {
                        matches.Add(match);
                    }
                }

                if (matches.Count > 0)
                {
                    ClearThumbnailSelection();
                    SelectedItems.Clear();
                    foreach (var m in matches)
                    {
                        m.IsThumbnailSelected = true;
                        SelectedItems.Add(m);
                    }

                    var focusedMatch = !string.IsNullOrEmpty(previousFocusedPath)
                        ? matches.FirstOrDefault(m => string.Equals(m.FullPath, previousFocusedPath, comparison))
                        : null;

                    SelectedItem = focusedMatch ?? matches.Last();
                    var firstMatchIndex = FilteredItems.IndexOf(matches[0]);
                    _selectionAnchorIndex = firstMatchIndex >= 0 ? firstMatchIndex : 0;

                    if (isExplicitNavigationSelect)
                    {
                        ScrollIntoViewRequested?.Invoke(matches[0]);
                    }
                    else
                    {
                        SelectionRestored?.Invoke();
                    }
                }
                else
                {
                    ClearThumbnailSelection();
                    SelectedItems.Clear();
                    SelectedItem = null;
                    SelectionRestored?.Invoke();
                }
            }
            else
            {
                ClearThumbnailSelection();
                SelectedItems.Clear();
                SelectedItem = null;
                SelectionRestored?.Invoke();
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

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        var selectedPaths = SelectedItems
            .Select(i => i.FullPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(comparer)
            .ToList();

        if (selectedPaths.Count == 0 && SelectedItem != null && !string.IsNullOrEmpty(SelectedItem.FullPath))
        {
            selectedPaths.Add(SelectedItem.FullPath);
        }

        string? focusedPath = SelectedItem?.FullPath;

        var result = BuildFilteredItems(Items, FilterText, IsFilterRegex, SortColumn, SortAscending);
        var newFiltered = new ObservableCollection<FileItem>(result);
        FilteredItems = newFiltered;

        if (selectedPaths.Count > 0)
        {
            var matches = new List<FileItem>();
            foreach (var p in selectedPaths)
            {
                var match = newFiltered.FirstOrDefault(f => string.Equals(f.FullPath, p, comparison));
                if (match != null && !matches.Contains(match))
                {
                    matches.Add(match);
                }
            }

            if (matches.Count > 0)
            {
                ClearThumbnailSelection();
                SelectedItems.Clear();
                foreach (var m in matches)
                {
                    m.IsThumbnailSelected = true;
                    SelectedItems.Add(m);
                }

                var focusedMatch = !string.IsNullOrEmpty(focusedPath)
                    ? matches.FirstOrDefault(m => string.Equals(m.FullPath, focusedPath, comparison))
                    : null;

                SelectedItem = focusedMatch ?? matches.Last();
                var firstMatchIndex = newFiltered.IndexOf(matches[0]);
                _selectionAnchorIndex = firstMatchIndex >= 0 ? firstMatchIndex : 0;
                SelectionRestored?.Invoke();
            }
        }
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

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        var selectedPaths = SelectedItems
            .Select(i => i.FullPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(comparer)
            .ToList();

        if (selectedPaths.Count == 0 && SelectedItem != null && !string.IsNullOrEmpty(SelectedItem.FullPath))
        {
            selectedPaths.Add(SelectedItem.FullPath);
        }

        string? focusedPath = SelectedItem?.FullPath;

        var result = await Task.Run(
            () => BuildFilteredItems(snapshot, filterText, isRegex, sortColumn, sortAscending),
            cancellationToken);

        if (!_isDisposed && !cancellationToken.IsCancellationRequested && generation == _filterGeneration)
        {
            var newFiltered = new ObservableCollection<FileItem>(result);
            FilteredItems = newFiltered;

            if (selectedPaths.Count > 0)
            {
                var matches = new List<FileItem>();
                foreach (var p in selectedPaths)
                {
                    var match = newFiltered.FirstOrDefault(f => string.Equals(f.FullPath, p, comparison));
                    if (match != null && !matches.Contains(match))
                    {
                        matches.Add(match);
                    }
                }

                if (matches.Count > 0)
                {
                    ClearThumbnailSelection();
                    SelectedItems.Clear();
                    foreach (var m in matches)
                    {
                        m.IsThumbnailSelected = true;
                        SelectedItems.Add(m);
                    }

                    var focusedMatch = !string.IsNullOrEmpty(focusedPath)
                        ? matches.FirstOrDefault(m => string.Equals(m.FullPath, focusedPath, comparison))
                        : null;

                    SelectedItem = focusedMatch ?? matches.Last();
                    var firstMatchIndex = newFiltered.IndexOf(matches[0]);
                    _selectionAnchorIndex = firstMatchIndex >= 0 ? firstMatchIndex : 0;
                    SelectionRestored?.Invoke();
                }
            }
        }
    }

    private static List<FileItem> BuildFilteredItems(
        IEnumerable<FileItem> source,
        string filterText,
        bool isFilterRegex,
        string sortColumn,
        bool sortAscending)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        IEnumerable<FileItem> query = source
            .Where(i => i != null)
            .GroupBy(i => !string.IsNullOrEmpty(i.FullPath) ? i.FullPath : (i.Name ?? string.Empty), comparer)
            .Select(g => g.First());

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

        // Sort: files and folders are treated as equals when sorting
        IOrderedEnumerable<FileItem> sorted;
        if (sortAscending)
        {
            sorted = sortColumn switch
            {
                "Extension" => query.OrderBy(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Size" => query.OrderBy(i => i.SizeBytes).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Modified" => query.OrderBy(i => i.ModifiedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Created" => query.OrderBy(i => i.CreatedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Accessed" => query.OrderBy(i => i.AccessedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Type" => query.OrderBy(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Attributes" => query.OrderBy(i => i.AttributesString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Permissions" => query.OrderBy(i => i.PermissionsString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "OwnerGroup" => query.OrderBy(i => i.OwnerGroupString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                _ => query.OrderBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase)
            };
        }
        else
        {
            sorted = sortColumn switch
            {
                "Extension" => query.OrderByDescending(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Size" => query.OrderByDescending(i => i.SizeBytes).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Modified" => query.OrderByDescending(i => i.ModifiedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Created" => query.OrderByDescending(i => i.CreatedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Accessed" => query.OrderByDescending(i => i.AccessedTime).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Type" => query.OrderByDescending(i => i.Extension).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Attributes" => query.OrderByDescending(i => i.AttributesString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "Permissions" => query.OrderByDescending(i => i.PermissionsString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                "OwnerGroup" => query.OrderByDescending(i => i.OwnerGroupString).ThenBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase),
                _ => query.OrderByDescending(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase)
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
            if (SelectedItems.Count == 1 && SelectedItems[0] == item && item.IsThumbnailSelected)
            {
                _selectionAnchorIndex = itemIndex;
                if (SelectedItem != item) SelectedItem = item;
                return;
            }

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

    private void SetupWatcher(string path)
    {
        try
        {
            _watcher?.Dispose();
            _watcher = null;

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            _watcher.Created += OnWatcherCreated;
            _watcher.Deleted += OnWatcherDeleted;
            _watcher.Renamed += OnWatcherRenamed;
            _watcher.Changed += OnWatcherChanged;
        }
        catch
        {
            // Restricted directories, unformatted drives, or unsupported schemes
        }
    }

    private void OnWatcherCreated(object sender, FileSystemEventArgs e)
    {
        if (_isDisposed) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed) return;
            ReconcileItemCreatedOrChanged(e.FullPath);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnWatcherDeleted(object sender, FileSystemEventArgs e)
    {
        if (_isDisposed) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed) return;
            ReconcileItemDeleted(e.FullPath);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        if (_isDisposed) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed) return;
            ReconcileItemRenamed(e.OldFullPath, e.FullPath);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (_isDisposed) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed) return;
            ReconcileItemCreatedOrChanged(e.FullPath);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void ReconcileItemCreatedOrChanged(string fullPath)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(fullPath) || Items == null) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        bool isDir = Directory.Exists(fullPath);
        bool isFile = File.Exists(fullPath);
        if (!isDir && !isFile) return;

        var existing = Items.FirstOrDefault(i => string.Equals(i.FullPath, fullPath, comparison));
        if (existing != null)
        {
            try
            {
                if (isDir)
                {
                    var di = new DirectoryInfo(fullPath);
                    existing.ModifiedTime = di.LastWriteTime;
                }
                else
                {
                    var fi = new FileInfo(fullPath);
                    existing.SizeBytes = fi.Length;
                    existing.ModifiedTime = fi.LastWriteTime;
                }
            }
            catch { }
            return;
        }

        try
        {
            FileItem newItem;
            if (isDir)
            {
                var di = new DirectoryInfo(fullPath);
                newItem = new FileItem
                {
                    Name = di.Name,
                    FullPath = di.FullName,
                    IsDirectory = true,
                    Extension = string.Empty,
                    ModifiedTime = di.LastWriteTime,
                    CreatedTime = di.CreationTime,
                    AccessedTime = di.LastAccessTime,
                    AttributesString = di.Attributes.ToString(),
                    SizeBytes = 0
                };
            }
            else
            {
                var fi = new FileInfo(fullPath);
                newItem = new FileItem
                {
                    Name = fi.Name,
                    FullPath = fi.FullName,
                    IsDirectory = false,
                    Extension = fi.Extension,
                    ModifiedTime = fi.LastWriteTime,
                    CreatedTime = fi.CreationTime,
                    AccessedTime = fi.LastAccessTime,
                    AttributesString = fi.Attributes.ToString(),
                    SizeBytes = fi.Length
                };
            }

            newItem.IsCut = ClipboardFileService.IsPathCut(newItem.FullPath);
            Items.Add(newItem);
            ApplyFilter();
        }
        catch { }
    }

    public void ReconcileItemDeleted(string fullPath)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(fullPath) || Items == null) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var existing = Items.FirstOrDefault(i => string.Equals(i.FullPath, fullPath, comparison));
        if (existing != null)
        {
            Items.Remove(existing);
            SelectedItems.Remove(existing);
            if (SelectedItem == existing)
            {
                SelectedItem = SelectedItems.LastOrDefault();
            }
            ApplyFilter();
        }
    }

    public void ReconcileItemRenamed(string oldFullPath, string newFullPath)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(newFullPath) || Items == null) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        bool wasSelected = false;
        bool wasFocused = false;

        var oldItem = Items.FirstOrDefault(i => string.Equals(i.FullPath, oldFullPath, comparison));
        if (oldItem != null)
        {
            wasSelected = SelectedItems.Contains(oldItem) || oldItem.IsThumbnailSelected;
            wasFocused = SelectedItem == oldItem;
            Items.Remove(oldItem);
            SelectedItems.Remove(oldItem);
        }

        var existingNew = Items.FirstOrDefault(i => string.Equals(i.FullPath, newFullPath, comparison));
        if (existingNew != null)
        {
            Items.Remove(existingNew);
            SelectedItems.Remove(existingNew);
        }

        bool isDir = Directory.Exists(newFullPath);
        bool isFile = File.Exists(newFullPath);
        if (!isDir && !isFile)
        {
            ApplyFilter();
            return;
        }

        try
        {
            FileItem newItem;
            if (isDir)
            {
                var di = new DirectoryInfo(newFullPath);
                newItem = new FileItem
                {
                    Name = di.Name,
                    FullPath = di.FullName,
                    IsDirectory = true,
                    Extension = string.Empty,
                    ModifiedTime = di.LastWriteTime,
                    CreatedTime = di.CreationTime,
                    AccessedTime = di.LastAccessTime,
                    AttributesString = di.Attributes.ToString(),
                    SizeBytes = 0
                };
            }
            else
            {
                var fi = new FileInfo(newFullPath);
                newItem = new FileItem
                {
                    Name = fi.Name,
                    FullPath = fi.FullName,
                    IsDirectory = false,
                    Extension = fi.Extension,
                    ModifiedTime = fi.LastWriteTime,
                    CreatedTime = fi.CreationTime,
                    AccessedTime = fi.LastAccessTime,
                    AttributesString = fi.Attributes.ToString(),
                    SizeBytes = fi.Length
                };
            }

            newItem.IsCut = ClipboardFileService.IsPathCut(newItem.FullPath);
            Items.Add(newItem);
            ApplyFilter();

            if (wasSelected)
            {
                var matched = FilteredItems.FirstOrDefault(i => string.Equals(i.FullPath, newFullPath, comparison));
                if (matched != null)
                {
                    matched.IsThumbnailSelected = true;
                    if (!SelectedItems.Contains(matched)) SelectedItems.Add(matched);
                    if (wasFocused) SelectedItem = matched;
                }
            }
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
            _watcher?.Dispose();
            _watcher = null;
        }
        catch { }

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
