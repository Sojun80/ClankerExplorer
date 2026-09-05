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
using ClankerExplorer.Services.Watcher;
using ClankerExplorer.AppLayer.Operations;

namespace ClankerExplorer.ViewModels;

public partial class ExplorerTabViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _filterDebounceCts;
    private CancellationTokenSource? _filterExecutionCts;
    private readonly IDirectoryWatcher _watcher;
    private readonly DirectoryChangeReconciler _reconciler;
    private long _loadGeneration = 0;
    private long _filterGeneration = 0;
    // Keep the selection anchor by item identity rather than index.
    // Sorting may move an item without changing the logical anchor, while
    // refresh/filter replacement naturally invalidates a stale item.
    private FileItem? _selectionAnchorItem;
    private bool _isDisposed;
    private DirectoryReadOptions _directoryReadOptions = DirectoryReadOptions.FromSettings(SettingsService.Instance.CurrentSettings);
    private SortedSourceCache? _sortedCache;
    private long _itemsVersion;

    private sealed class SortedSourceCache
    {
        public long ItemsVersion { get; }
        public string SortColumn { get; }
        public bool SortAscending { get; }
        public List<FileItem> Items { get; }

        public SortedSourceCache(long itemsVersion, string sortColumn, bool sortAscending, List<FileItem> items)
        {
            ItemsVersion = itemsVersion;
            SortColumn = sortColumn;
            SortAscending = sortAscending;
            Items = items;
        }
    }

    public void InvalidateSortedCache()
    {
        Interlocked.Increment(ref _itemsVersion);
        _sortedCache = null;
    }

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

    partial void OnSortColumnChanged(string value) => InvalidateSortedCache();

    [ObservableProperty]
    private bool _sortAscending = true;

    partial void OnSortAscendingChanged(bool value) => InvalidateSortedCache();

    [ObservableProperty]
    private FileItem? _selectedItem;

    partial void OnSelectedItemChanged(FileItem? value)
    {
        if (value != null && !string.IsNullOrEmpty(value.FullPath))
        {
            ThumbnailService.Instance.ClearYieldGuard(value.FullPath);
        }
    }

    [ObservableProperty]
    private ObservableCollection<FileItem> _items = new();

    partial void OnItemsChanged(ObservableCollection<FileItem> value) => InvalidateSortedCache();

    [ObservableProperty]
    private ObservableCollection<FileItem> _filteredItems = new();

    [ObservableProperty]
    private ObservableCollection<FileItem> _selectedItems = new();

    public List<string> History { get; } = new();
    public int HistoryIndex { get; private set; } = -1;

    public bool CanGoBack => HistoryIndex > 0;
    public bool CanGoForward => HistoryIndex < History.Count - 1;

    public IDirectoryWatcher Watcher => _watcher;
    public DirectoryChangeReconciler Reconciler => _reconciler;

    public ExplorerTabViewModel(string? initialPath = null, IDirectoryWatcher? watcher = null)
    {
        _watcher = watcher ?? new DirectoryWatcher();
        _reconciler = new DirectoryChangeReconciler(this);
        _watcher.BatchReady += OnWatcherBatchReady;
        _watcher.ErrorOccurred += OnWatcherError;

        initialPath ??= FileSystemService.DefaultRootPath;
        ClipboardFileService.ClipboardChanged += UpdateCutStatus;
        NavigateTo(initialPath);
    }

    public event Action? RequestThumbnailViewportUpdate;

    public void TriggerThumbnailViewportUpdate()
    {
        RequestThumbnailViewportUpdate?.Invoke();
    }

    public void NotifyFilteredItemsChanged()
    {
        OnPropertyChanged(nameof(FilteredItems));
    }

    public void UpdateCutStatus()
    {
        if (Items == null || _isDisposed) return;
        var cutSet = ClipboardFileService.GetCutPathsSnapshot();
        if (cutSet == null || cutSet.Count == 0)
        {
            foreach (var item in Items)
            {
                item.IsCut = false;
            }
        }
        else
        {
            foreach (var item in Items)
            {
                item.IsCut = !string.IsNullOrEmpty(item.FullPath) &&
                             cutSet.Contains(item.FullPath.TrimEnd('\\', '/'));
            }
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

        _reconciler.Reset();

        if (HistoryIndex < History.Count - 1)
        {
            History.RemoveRange(HistoryIndex + 1, History.Count - (HistoryIndex + 1));
        }

        History.Add(path);
        HistoryIndex = History.Count - 1;

        CurrentPath = path;
        UpdateTitle(path);
        _watcher.Start(path);
        _ = RefreshAsync();

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoBack()
    {
        if (!CanGoBack || _isDisposed) return;
        _reconciler.Reset();
        // Remember the folder we're leaving so we can re-select it after loading
        PendingSelectPath = History[HistoryIndex];
        HistoryIndex--;
        CurrentPath = History[HistoryIndex];
        UpdateTitle(CurrentPath);
        _watcher.Start(CurrentPath);
        _ = RefreshAsync();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoForward()
    {
        if (!CanGoForward || _isDisposed) return;
        _reconciler.Reset();
        HistoryIndex++;
        CurrentPath = History[HistoryIndex];
        UpdateTitle(CurrentPath);
        _watcher.Start(CurrentPath);
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

        _reconciler.Reset();
        long stagingToken = _reconciler.BeginStaging();
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
            if (token.IsCancellationRequested || generation != _loadGeneration || _isDisposed)
            {
                _reconciler.CancelStaging(stagingToken);
                return;
            }

            if (error != null)
            {
                StatusMessage = error;
            }
            else
            {
                StatusMessage = string.Empty;
                HistoryService.Instance.RecordFolderVisit(CurrentPath);
            }

            _watcher.Start(CurrentPath);

            var deduplicatedList = list
                .GroupBy(i => i.FullPath, comparer)
                .Select(g => g.First())
                .Where(i => !TransferEngine.IsActiveTempFile(i.FullPath))
                .ToList();

            var cutSet = ClipboardFileService.GetCutPathsSnapshot();
            if (cutSet == null || cutSet.Count == 0)
            {
                foreach (var item in deduplicatedList)
                {
                    item.IsCut = false;
                }
            }
            else
            {
                foreach (var item in deduplicatedList)
                {
                    item.IsCut = !string.IsNullOrEmpty(item.FullPath) &&
                                 cutSet.Contains(item.FullPath.TrimEnd('\\', '/'));
                }
            }

            Items = new ObservableCollection<FileItem>(deduplicatedList);
            await ApplyFilterAsync(token);

            _reconciler.EndStagingAndReplay(stagingToken);

            // Determine selection targets: explicit navigation/paste vs ordinary refresh continuity
            var pendingPaths = PendingSelectPaths;
            PendingSelectPaths = null;
            bool isExplicitNavigationSelect = (pendingPaths != null && pendingPaths.Count > 0);
            var targetSelectPaths = isExplicitNavigationSelect ? pendingPaths! : previousSelectedPaths;

            if (targetSelectPaths != null && targetSelectPaths.Count > 0)
            {
                var (matches, focusedMatch) = MatchSelectedItems(FilteredItems, targetSelectPaths, previousFocusedPath, comparer);

                if (matches.Count > 0)
                {
                    ClearThumbnailSelection();
                    SelectedItems.Clear();
                    foreach (var m in matches)
                    {
                        m.IsThumbnailSelected = true;
                        SelectedItems.Add(m);
                    }

                    SelectedItem = focusedMatch ?? matches.Last();
                    _selectionAnchorItem = matches[0];

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
        catch (OperationCanceledException)
        {
            _reconciler.CancelStaging(stagingToken);
        }
        catch (Exception)
        {
            _reconciler.CancelStaging(stagingToken);
            throw;
        }
        finally
        {
            if (generation == _loadGeneration && !_isDisposed)
            {
                IsLoading = false;
            }
        }
    }

    public void SelectPaths(IEnumerable<string> paths, bool scrollIntoView = false)
    {
        if (paths == null || _isDisposed) return;
        var pathList = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (pathList.Count == 0) return;

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var (matches, _) = MatchSelectedItems(FilteredItems, pathList, null, comparer);

        if (matches.Count > 0)
        {
            ClearThumbnailSelection();
            SelectedItems.Clear();
            foreach (var m in matches)
            {
                m.IsThumbnailSelected = true;
                SelectedItems.Add(m);
            }

            SelectedItem = matches.Last();
            _selectionAnchorItem = matches[0];

            if (scrollIntoView)
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
            PendingSelectPaths = pathList;
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

        var result = BuildFilteredItems(Items, FilterText, IsFilterRegex, SortColumn, SortAscending, CancellationToken.None);
        var newFiltered = new ObservableCollection<FileItem>(result);
        FilteredItems = newFiltered;

        if (selectedPaths.Count > 0)
        {
            var (matches, focusedMatch) = MatchSelectedItems(newFiltered, selectedPaths, focusedPath, comparer);

            if (matches.Count > 0)
            {
                ClearThumbnailSelection();
                SelectedItems.Clear();
                foreach (var m in matches)
                {
                    m.IsThumbnailSelected = true;
                    SelectedItems.Add(m);
                }

                SelectedItem = focusedMatch ?? matches.Last();
                _selectionAnchorItem = matches[0];
                SelectionRestored?.Invoke();
            }
        }
    }

    public async Task ApplyFilterAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || Items == null) return;

        // Cancel previous filter computation promptly
        try
        {
            _filterExecutionCts?.Cancel();
            _filterExecutionCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        _filterExecutionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var executionToken = _filterExecutionCts.Token;

        long generation = Interlocked.Increment(ref _filterGeneration);
        var snapshot = Items.ToArray();
        string filterText = FilterText;
        bool isRegex = IsFilterRegex;
        string sortColumn = SortColumn;
        bool sortAscending = SortAscending;

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

        List<FileItem> result;
        try
        {
            result = await Task.Run(
                () => BuildFilteredItems(snapshot, filterText, isRegex, sortColumn, sortAscending, executionToken),
                executionToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_isDisposed && !executionToken.IsCancellationRequested && generation == _filterGeneration)
        {
            var newFiltered = new ObservableCollection<FileItem>(result);
            FilteredItems = newFiltered;

            if (selectedPaths.Count > 0)
            {
                var (matches, focusedMatch) = MatchSelectedItems(newFiltered, selectedPaths, focusedPath, comparer);

                if (matches.Count > 0)
                {
                    ClearThumbnailSelection();
                    SelectedItems.Clear();
                    foreach (var m in matches)
                    {
                        m.IsThumbnailSelected = true;
                        SelectedItems.Add(m);
                    }

                    SelectedItem = focusedMatch ?? matches.Last();
                    _selectionAnchorItem = matches[0];
                    SelectionRestored?.Invoke();
                }
            }
        }
    }

    private static (List<FileItem> Matches, FileItem? FocusedMatch) MatchSelectedItems(
        IEnumerable<FileItem> items,
        IEnumerable<string> targetPaths,
        string? preferredFocusedPath,
        StringComparer comparer)
    {
        var lookup = new Dictionary<string, FileItem>(comparer);
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.FullPath))
            {
                lookup.TryAdd(item.FullPath, item);
            }
        }

        var matches = new List<FileItem>();
        var seen = new HashSet<FileItem>();
        foreach (var p in targetPaths)
        {
            if (!string.IsNullOrEmpty(p) && lookup.TryGetValue(p, out var match) && seen.Add(match))
            {
                matches.Add(match);
            }
        }

        FileItem? focusedMatch = null;
        if (!string.IsNullOrEmpty(preferredFocusedPath))
        {
            lookup.TryGetValue(preferredFocusedPath, out focusedMatch);
        }

        return (matches, focusedMatch);
    }

    private List<FileItem> BuildFilteredItems(
        IReadOnlyList<FileItem> source,
        string filterText,
        bool isFilterRegex,
        string sortColumn,
        bool sortAscending,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sortedSource = GetOrBuildSortedSource(source, sortColumn, sortAscending, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(filterText))
        {
            return new List<FileItem>(sortedSource);
        }

        Func<FileItem, bool> filterPredicate;
        if (isFilterRegex)
        {
            try
            {
                var regex = new Regex(filterText, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
                filterPredicate = i => regex.IsMatch(i.Name) || regex.IsMatch(i.Extension);
            }
            catch
            {
                filterPredicate = i => i.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase);
            }
        }
        else if (filterText.Contains('*') || filterText.Contains('?'))
        {
            var glob = "^" + Regex.Escape(filterText).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try
            {
                var regex = new Regex(glob, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
                filterPredicate = i => regex.IsMatch(i.Name);
            }
            catch
            {
                filterPredicate = i => i.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase);
            }
        }
        else
        {
            filterPredicate = i => i.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                                  i.Extension.Contains(filterText, StringComparison.OrdinalIgnoreCase);
        }

        var result = new List<FileItem>();
        int count = 0;
        foreach (var item in sortedSource)
        {
            if ((++count & 511) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (item != null && filterPredicate(item))
            {
                result.Add(item);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private List<FileItem> GetOrBuildSortedSource(
        IReadOnlyList<FileItem> source,
        string sortColumn,
        bool sortAscending,
        CancellationToken cancellationToken)
    {
        long currentVersion = Volatile.Read(ref _itemsVersion);
        var cache = _sortedCache;

        if (cache != null &&
            cache.ItemsVersion == currentVersion &&
            string.Equals(cache.SortColumn, sortColumn, StringComparison.OrdinalIgnoreCase) &&
            cache.SortAscending == sortAscending)
        {
            return cache.Items;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var sorted = SortItems(source, sortColumn, sortAscending, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (currentVersion == Volatile.Read(ref _itemsVersion))
        {
            _sortedCache = new SortedSourceCache(currentVersion, sortColumn, sortAscending, sorted);
        }

        return sorted;
    }

    private static List<FileItem> SortItems(
        IReadOnlyList<FileItem> source,
        string sortColumn,
        bool sortAscending,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Items is already guaranteed deduplicated by RefreshAsync and DirectoryChangeReconciler
        IEnumerable<FileItem> query = source.Where(i => i != null);

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

        cancellationToken.ThrowIfCancellationRequested();
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

    public void SetSelectionAnchor(FileItem? item)
    {
        _selectionAnchorItem =
            item != null && FilteredItems.Contains(item)
                ? item
                : null;
    }

    public IReadOnlyList<FileItem> GetSelectionRange(FileItem item)
    {
        int itemIndex = FilteredItems.IndexOf(item);
        if (itemIndex < 0)
            return Array.Empty<FileItem>();

        int anchorIndex = _selectionAnchorItem != null
            ? FilteredItems.IndexOf(_selectionAnchorItem)
            : -1;

        // If the old anchor vanished because of a refresh/filter, use the
        // active selection when possible, otherwise start a new anchor here.
        if (anchorIndex < 0 && SelectedItem != null)
            anchorIndex = FilteredItems.IndexOf(SelectedItem);

        if (anchorIndex < 0)
        {
            anchorIndex = itemIndex;
            _selectionAnchorItem = item;
        }

        int start = Math.Min(anchorIndex, itemIndex);
        int end = Math.Max(anchorIndex, itemIndex);
        var range = new List<FileItem>(end - start + 1);

        for (int index = start; index <= end; index++)
            range.Add(FilteredItems[index]);

        return range;
    }

    public void SelectThumbnailItem(FileItem item, bool control, bool shift)
    {
        int itemIndex = FilteredItems.IndexOf(item);
        if (itemIndex < 0) return;

        if (shift)
        {
            var range = GetSelectionRange(item);
            if (!control) ClearThumbnailSelection();
            foreach (var rangeItem in range)
                AddThumbnailSelection(rangeItem);
        }
        else if (control)
        {
            if (item.IsThumbnailSelected) RemoveThumbnailSelection(item);
            else AddThumbnailSelection(item);
            _selectionAnchorItem = item;
        }
        else
        {
            if (SelectedItems.Count == 1 && SelectedItems[0] == item && item.IsThumbnailSelected)
            {
                _selectionAnchorItem = item;
                if (SelectedItem != item) SelectedItem = item;
                return;
            }

            ClearThumbnailSelection();
            AddThumbnailSelection(item);
            _selectionAnchorItem = item;
        }

        SelectedItem = item.IsThumbnailSelected ? item : SelectedItems.LastOrDefault();
    }

    public void ClearThumbnailSelection()
    {
        foreach (var selected in SelectedItems) selected.IsThumbnailSelected = false;
        SelectedItems.Clear();
        SelectedItem = null;
    }

    public void SelectAll(bool isThumbnailView)
    {
        if (isThumbnailView)
        {
            SelectAllThumbnails();
        }
        else
        {
            SelectedItems.Clear();
            foreach (var item in FilteredItems)
            {
                SelectedItems.Add(item);
            }
            SelectedItem = FilteredItems.LastOrDefault();
            _selectionAnchorItem = SelectedItem;
        }
    }

    public void SelectAllThumbnails()
    {
        foreach (var item in FilteredItems) item.IsThumbnailSelected = true;
        SelectedItems = new ObservableCollection<FileItem>(FilteredItems);
        SelectedItem = SelectedItems.LastOrDefault();
        _selectionAnchorItem = SelectedItem;
    }

    public void AddThumbnailSelection(FileItem item)
    {
        if (item.IsThumbnailSelected) return;
        item.IsThumbnailSelected = true;
        SelectedItems.Add(item);
    }

    public void RemoveThumbnailSelection(FileItem item)
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

    private void OnWatcherBatchReady(object? sender, DirectoryChangeBatch batch)
    {
        if (_isDisposed) return;
        _reconciler.HandleBatch(batch);
    }

    private void OnWatcherError(object? sender, Exception ex)
    {
        // DirectoryWatcher handles recovery via overflow batch
    }

    public void ReconcileItemCreatedOrChanged(string fullPath)
    {
        if (_isDisposed) return;
        _reconciler.ReconcileCreatedOrChangedSync(fullPath);
    }

    public void ReconcileItemDeleted(string fullPath)
    {
        if (_isDisposed) return;
        _reconciler.ReconcileDeletedSync(fullPath);
    }

    public void ReconcileItemRenamed(string oldFullPath, string newFullPath)
    {
        if (_isDisposed) return;
        _reconciler.ReconcileRenamedSync(oldFullPath, newFullPath);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        ClipboardFileService.ClipboardChanged -= UpdateCutStatus;

        try
        {
            _watcher.BatchReady -= OnWatcherBatchReady;
            _watcher.ErrorOccurred -= OnWatcherError;
            _watcher.Dispose();
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

        try
        {
            _filterExecutionCts?.Cancel();
            _filterExecutionCts?.Dispose();
        }
        catch { }
    }
}
