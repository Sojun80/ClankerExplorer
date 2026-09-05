using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class ExplorerPaneView : UserControl
{
    private readonly DispatcherTimer _autoScrollTimer;
    private readonly DispatcherTimer _middleScrollTimer;
    private readonly DispatcherTimer _thumbnailDebounceTimer;
    private readonly DispatcherTimer _folderScrollSaveTimer;
    private CancellationTokenSource? _thumbnailViewportCts;
    private HashSet<FileItem> _retainedThumbnailItems = new();
    private bool _thumbnailViewportInitialized;
    private ScrollViewer? _detailsScrollViewer;
    private ScrollViewer? _thumbnailScrollViewer;
    private bool _restoringFolderViewState;
    private bool _detailsScrollSubscribed;
    private bool _thumbnailScrollSubscribed;
    private bool _isMouseDownForMarquee;
    private bool _isMarqueeActive;
    private Point _marqueeStartPos;
    private Point _lastMarqueePos;
    private HashSet<FileItem> _marqueeBaseSelection = new();
    private double _autoScrollVelocity;

    // Middle-Mouse Free-Scroll / Autoscroll State
    private bool _isMiddleAutoScrolling;
    private Point _autoScrollAnchorPos;
    private Point _currentPointerPos;
    private bool _hasMovedDuringMiddleScroll;
    private ScrollViewer? _activeMiddleScrollViewer;

    // Drag-and-Drop State
    private Point _dragStartPoint;
    private FileItem? _dragCandidateItem;
    private bool _isDragActive;
    private bool _dragOccurredForCurrentPress;
    private FileItem? _pendingPlainClickItem;
    private bool _isApplyingDetailsSelection;
    private FileItem? _hoveredDragTarget;

    public ExplorerPaneView()
    {
        InitializeComponent();

        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScrollTimer.Tick += OnAutoScrollTick;
        _middleScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _middleScrollTimer.Tick += OnMiddleScrollTick;
        _thumbnailDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _thumbnailDebounceTimer.Tick += OnThumbnailDebounceTick;
        _folderScrollSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _folderScrollSaveTimer.Tick += (_, _) =>
        {
            _folderScrollSaveTimer.Stop();
            SaveFolderScrollState(persist: true);
        };

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Loaded += (s, e) =>
        {
            if (DataContext is ExplorerPaneViewModel vm)
            {
                vm.RequestSetClipboardText += async text =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel?.Clipboard != null && !string.IsNullOrEmpty(text))
                    {
                        await topLevel.Clipboard.SetTextAsync(text);
                    }
                };

                vm.RequestCopyFiles += async paths =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    await ClipboardFileService.CopyToSystemClipboardAsync(topLevel?.Clipboard, topLevel?.StorageProvider, paths);
                };

                vm.RequestCutFiles += async paths =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    await ClipboardFileService.CutToSystemClipboardAsync(topLevel?.Clipboard, topLevel?.StorageProvider, paths);
                };

                vm.RequestEnqueuePaste += async destDir =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    return await ClipboardFileService.EnqueuePasteFromSystemClipboardAsync(topLevel?.Clipboard, destDir);
                };

                vm.Tabs.CollectionChanged += (s2, e2) =>
                {
                    Dispatcher.UIThread.Post(UpdateTabScrollButtonsVisibility, DispatcherPriority.Loaded);
                };

                vm.PropertyChanged += (s2, e2) =>
                {
                    if (e2.PropertyName == nameof(ExplorerPaneViewModel.TabWidth))
                    {
                        Dispatcher.UIThread.Post(UpdateTabScrollButtonsVisibility, DispatcherPriority.Loaded);
                    }
                };
            }

            if (TabsScrollViewer != null)
            {
                TabsScrollViewer.SizeChanged += (s2, e2) => UpdateTabScrollButtonsVisibility();
                TabsScrollViewer.LayoutUpdated += (s2, e2) => UpdateTabScrollButtonsVisibility();
            }

            if (FileDataGrid != null)
            {
                FileDataGrid.AddHandler(PointerPressedEvent, OnDataGridPointerPressedTunnel, RoutingStrategies.Tunnel);
                FileDataGrid.AddHandler(PointerMovedEvent, OnPointerMovedTunnel, RoutingStrategies.Tunnel);
                FileDataGrid.AddHandler(PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel);
                FileDataGrid.AddHandler(PointerReleasedEvent, (sender, args) =>
                {
                    SaveCurrentColumnLayout();
                    CaptureFolderViewportAnchors();
                }, RoutingStrategies.Bubble);
                FileDataGrid.AddHandler(PointerWheelChangedEvent, (_, _) =>
                    Dispatcher.UIThread.Post(CaptureFolderViewportAnchors, DispatcherPriority.Background),
                    RoutingStrategies.Bubble, handledEventsToo: true);
                FileDataGrid.KeyUp += (_, _) =>
                    Dispatcher.UIThread.Post(CaptureFolderViewportAnchors, DispatcherPriority.Background);
                FileDataGrid.ColumnReordered += (sender, args) => SaveCurrentColumnLayout();
                FileDataGrid.Sorting += OnDataGridSorting;
                FileDataGrid.AddHandler(KeyDownEvent, OnDataGridKeyDownTunnel, RoutingStrategies.Tunnel);
            }

            if (ThumbnailListBox != null)
            {
                ThumbnailListBox.AddHandler(PointerPressedEvent, OnThumbnailListBoxPointerPressedTunnel, RoutingStrategies.Tunnel);
                ThumbnailListBox.AddHandler(PointerMovedEvent, OnPointerMovedTunnel, RoutingStrategies.Tunnel);
                ThumbnailListBox.AddHandler(PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel);
            }

            if (FileGridContainer != null)
            {
                FileGridContainer.AddHandler(PointerPressedEvent, OnFileGridPointerPressedTunnel, RoutingStrategies.Tunnel);
                FileGridContainer.AddHandler(PointerMovedEvent, OnPointerMovedTunnel, RoutingStrategies.Tunnel);
                FileGridContainer.AddHandler(PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel);
            }
            AddHandler(KeyDownEvent, OnPaneKeyDownTunnel, RoutingStrategies.Tunnel);

            InitializeThumbnailViewport();
            InitializeFolderViewRestoration();

            if (GridContextMenu != null)
            {
                GridContextMenu.Opening += (sender, args) =>
                {
                    if (DataContext is ExplorerPaneViewModel vm)
                    {
                        vm.NotifyContextMenuProperties();
                    }
                };
            }

            if (ThumbnailContextMenu != null)
            {
                ThumbnailContextMenu.Opening += (sender, args) =>
                {
                    if (DataContext is ExplorerPaneViewModel vm)
                    {
                        vm.NotifyContextMenuProperties();
                    }
                };
            }

            Dispatcher.UIThread.Post(UpdateTabScrollButtonsVisibility, DispatcherPriority.Loaded);
        };

        Unloaded += (_, _) =>
        {
            _thumbnailDebounceTimer.Stop();
            _folderScrollSaveTimer.Stop();
            _thumbnailViewportCts?.Cancel();
            ThumbnailService.Instance.CancelPendingRequests();
            _retainedThumbnailItems.Clear();
        };
    }

    private void InitializeThumbnailViewport()
    {
        if (_thumbnailViewportInitialized || ThumbnailListBox == null) return;
        _thumbnailViewportInitialized = true;

        ThumbnailListBox.SizeChanged += (_, _) =>
        {
            if (DataContext is ExplorerPaneViewModel vm)
            {
                vm.UpdateThumbnailViewportWidth(ThumbnailListBox.Bounds.Width);
                ScheduleThumbnailViewportUpdate();
            }
        };

        _thumbnailScrollViewer = ThumbnailListBox.FindDescendantOfType<ScrollViewer>();
        if (_thumbnailScrollViewer != null)
        {
            _thumbnailScrollSubscribed = true;
            _thumbnailScrollViewer.ScrollChanged += (_, _) =>
            {
                ScheduleThumbnailViewportUpdate();
                OnFolderScrollChanged();
            };
        }

        if (DataContext is ExplorerPaneViewModel pane)
        {
            pane.UpdateThumbnailViewportWidth(ThumbnailListBox.Bounds.Width);
            pane.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ExplorerPaneViewModel.ThumbnailRows)
                    or nameof(ExplorerPaneViewModel.IsThumbnailView)
                    or nameof(ExplorerPaneViewModel.ThumbnailSize)
                    or nameof(ExplorerPaneViewModel.SelectedTab))
                {
                    _thumbnailViewportCts?.Cancel();
                    ThumbnailService.Instance.CancelPendingRequests();
                    _retainedThumbnailItems.Clear();
                    Dispatcher.UIThread.Post(ScheduleThumbnailViewportUpdate, DispatcherPriority.Loaded);
                }
            };
        }

        Dispatcher.UIThread.Post(ScheduleThumbnailViewportUpdate, DispatcherPriority.Loaded);
    }

    private void InitializeFolderViewRestoration()
    {
        if (DataContext is not ExplorerPaneViewModel vm) return;
        EnsureFolderScrollViewers();
        vm.FolderViewStateRestored += RestoreFolderViewState;
        vm.RequestScrollItemIntoView += OnRequestScrollItemIntoView;
        vm.RequestSyncSelection += OnRequestSyncSelection;
        vm.RequestThumbnailViewportUpdate += ScheduleThumbnailViewportUpdate;
        RestoreFolderViewState();
    }

    private void EnsureFolderScrollViewers()
    {
        _detailsScrollViewer ??= FileDataGrid?.FindDescendantOfType<ScrollViewer>();
        if (_detailsScrollViewer != null && !_detailsScrollSubscribed)
        {
            _detailsScrollSubscribed = true;
            _detailsScrollViewer.ScrollChanged += (_, _) => OnFolderScrollChanged();
        }

        _thumbnailScrollViewer ??= ThumbnailListBox?.FindDescendantOfType<ScrollViewer>();
        if (_thumbnailScrollViewer != null && !_thumbnailScrollSubscribed)
        {
            _thumbnailScrollSubscribed = true;
            _thumbnailScrollViewer.ScrollChanged += (_, _) =>
            {
                ScheduleThumbnailViewportUpdate();
                OnFolderScrollChanged();
            };
        }
    }

    private void OnFolderScrollChanged()
    {
        if (_restoringFolderViewState) return;
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;

        // If the ScrollViewer is collapsing during a collection reload or transient layout update,
        // do not overwrite the valid non-zero scroll position or anchor with 0.
        if (!vm.IsThumbnailView && _detailsScrollViewer != null)
        {
            bool isCollapsing = _detailsScrollViewer.Extent.Height <= _detailsScrollViewer.Viewport.Height;
            if (isCollapsing && _detailsScrollViewer.Offset.Y == 0 && vm.DetailsVerticalOffset > 0 && vm.SelectedTab.FilteredItems.Count > 0)
            {
                return;
            }
        }
        else if (vm.IsThumbnailView && _thumbnailScrollViewer != null)
        {
            bool isCollapsing = _thumbnailScrollViewer.Extent.Height <= _thumbnailScrollViewer.Viewport.Height;
            if (isCollapsing && _thumbnailScrollViewer.Offset.Y == 0 && vm.ThumbnailVerticalOffset > 0 && vm.SelectedTab.FilteredItems.Count > 0)
            {
                return;
            }
        }

        SaveFolderScrollState(persist: false);
        _folderScrollSaveTimer.Stop();
        _folderScrollSaveTimer.Start();
    }

    private void SaveFolderScrollState(bool persist)
    {
        if (DataContext is not ExplorerPaneViewModel vm) return;
        CaptureFolderViewportAnchors();
        vm.UpdateFolderScrollState(
            _detailsScrollViewer?.Offset.X ?? vm.DetailsHorizontalOffset,
            _detailsScrollViewer?.Offset.Y ?? vm.DetailsVerticalOffset,
            _thumbnailScrollViewer?.Offset.Y ?? vm.ThumbnailVerticalOffset,
            persist);
    }

    private void CaptureFolderViewportAnchors()
    {
        if (_restoringFolderViewState || DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
        string? detailsPath = FileDataGrid?.GetVisualDescendants()
            .OfType<DataGridRow>()
            .Select(row => new { Row = row, Point = row.TranslatePoint(new Point(0, 0), FileDataGrid) })
            .Where(entry => entry.Point.HasValue && entry.Point.Value.Y + entry.Row.Bounds.Height >= 0)
            .OrderBy(entry => entry.Point!.Value.Y)
            .Select(entry => (entry.Row.DataContext as FileItem)?.FullPath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        string? thumbnailPath = null;
        var panel = ThumbnailListBox?.FindDescendantOfType<VirtualizingStackPanel>();
        if (panel != null && panel.FirstRealizedIndex >= 0)
        {
            int itemIndex = panel.FirstRealizedIndex * Math.Max(1, vm.ThumbnailColumnCount);
            if (itemIndex < vm.SelectedTab.FilteredItems.Count)
                thumbnailPath = vm.SelectedTab.FilteredItems[itemIndex].FullPath;
        }

        vm.UpdateFolderViewportAnchors(
            detailsPath ?? vm.DetailsTopItemPath,
            thumbnailPath ?? vm.ThumbnailTopItemPath);
    }

    private void RestoreFolderViewState()
    {
        if (DataContext is not ExplorerPaneViewModel vm) return;
        _restoringFolderViewState = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                EnsureFolderScrollViewers();
                RestoreColumnOrder(vm.CurrentColumnOrder);
                bool anchored = RestoreViewportAnchors(vm);
                if (!anchored)
                {
                    if (_detailsScrollViewer != null && vm.DetailsVerticalOffset > 0)
                        _detailsScrollViewer.Offset = new Vector(vm.DetailsHorizontalOffset, vm.DetailsVerticalOffset);
                    if (_thumbnailScrollViewer != null && vm.ThumbnailVerticalOffset > 0)
                        _thumbnailScrollViewer.Offset = new Vector(0, vm.ThumbnailVerticalOffset);
                }
                else
                {
                    if (_detailsScrollViewer != null && vm.DetailsHorizontalOffset > 0)
                        _detailsScrollViewer.Offset = new Vector(vm.DetailsHorizontalOffset, _detailsScrollViewer.Offset.Y);
                }
            }
            finally
            {
                _restoringFolderViewState = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private bool RestoreViewportAnchors(ExplorerPaneViewModel vm)
    {
        var tab = vm.SelectedTab;
        if (tab == null) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!vm.IsThumbnailView && !string.IsNullOrWhiteSpace(vm.DetailsTopItemPath))
        {
            var item = tab.FilteredItems.FirstOrDefault(candidate =>
                string.Equals(candidate.FullPath, vm.DetailsTopItemPath, comparison));
            if (item != null && FileDataGrid != null)
            {
                FileDataGrid.ScrollIntoView(item, null);
                return true;
            }
        }

        if (vm.IsThumbnailView && !string.IsNullOrWhiteSpace(vm.ThumbnailTopItemPath) && ThumbnailListBox != null)
        {
            int index = -1;
            for (int candidateIndex = 0; candidateIndex < tab.FilteredItems.Count; candidateIndex++)
            {
                if (string.Equals(tab.FilteredItems[candidateIndex].FullPath, vm.ThumbnailTopItemPath, comparison))
                {
                    index = candidateIndex;
                    break;
                }
            }
            if (index >= 0)
            {
                int rowIndex = index / Math.Max(1, vm.ThumbnailColumnCount);
                if (rowIndex >= 0 && rowIndex < vm.ThumbnailRows.Count)
                {
                    ThumbnailListBox.ScrollIntoView(vm.ThumbnailRows[rowIndex]);
                    return true;
                }
            }
        }

        return false;
    }

    private void OnRequestScrollItemIntoView(FileItem item)
    {
        // Dispatch on Loaded priority so the DataGrid/ListBox has finished laying out the new items
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
            var tab = vm.SelectedTab;

            // Details view: scroll the DataGrid and sync selection
            if (FileDataGrid != null && !vm.IsThumbnailView)
            {
                if (tab.SelectedItems.Count > 0)
                {
                    FileDataGrid.SelectedItems.Clear();
                    foreach (var selected in tab.SelectedItems)
                    {
                        FileDataGrid.SelectedItems.Add(selected);
                    }
                }
                else if (tab.SelectedItem != null)
                {
                    FileDataGrid.SelectedItems.Clear();
                    FileDataGrid.SelectedItems.Add(tab.SelectedItem);
                }

                FileDataGrid.ScrollIntoView(item, null);
            }

            // Thumbnail view: scroll the ListBox row containing this item
            if (ThumbnailListBox != null && vm.IsThumbnailView)
            {
                int index = tab.FilteredItems.IndexOf(item);
                int colCount = Math.Max(1, vm.ThumbnailColumnCount);
                int rowIndex = index < 0 ? -1 : index / colCount;
                if (rowIndex >= 0 && rowIndex < vm.ThumbnailRows.Count)
                {
                    ThumbnailListBox.ScrollIntoView(vm.ThumbnailRows[rowIndex]);
                }
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnRequestSyncSelection()
    {
        // Sync DataGrid selection without changing scroll position or jumping viewport
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
            var tab = vm.SelectedTab;

            if (FileDataGrid != null && !vm.IsThumbnailView)
            {
                FileDataGrid.SelectedItems.Clear();
                foreach (var selected in tab.SelectedItems)
                {
                    FileDataGrid.SelectedItems.Add(selected);
                }
                if (tab.SelectedItem != null && !FileDataGrid.SelectedItems.Contains(tab.SelectedItem))
                {
                    FileDataGrid.SelectedItems.Add(tab.SelectedItem);
                }
                FileDataGrid.SelectedItem = tab.SelectedItem;
            }
        }, DispatcherPriority.Loaded);
    }

    private void ScheduleThumbnailViewportUpdate()
    {
        _thumbnailViewportCts?.Cancel();
        if (DataContext is not ExplorerPaneViewModel vm || !vm.IsThumbnailView) return;

        ThumbnailService.Instance.NotifyScrollActivity();

        // Fast-path while scrolling: immediately assign any realized visible items already in memory cache
        if (ThumbnailListBox != null && vm.SelectedTab != null)
        {
            var panel = ThumbnailListBox.FindDescendantOfType<VirtualizingStackPanel>();
            int firstRow = panel?.FirstRealizedIndex ?? 0;
            int lastRow = panel?.LastRealizedIndex ?? Math.Min(vm.ThumbnailRows.Count - 1, 3);
            if (firstRow >= 0 && lastRow >= firstRow)
            {
                var items = vm.SelectedTab.FilteredItems;
                int columns = Math.Max(1, vm.ThumbnailColumnCount);
                int start = Math.Clamp(firstRow * columns, 0, items.Count);
                int end = Math.Clamp((lastRow + 1) * columns, start, items.Count);
                if (end > start)
                {
                    ThumbnailService.Instance.TryPopulateFromMemoryCache(items.Skip(start).Take(end - start), (int)vm.ThumbnailSize);
                }
            }
        }

        int configuredDelay = SettingsService.Instance.CurrentSettings.ThumbnailScrollDebounceMilliseconds;
        int delay = Math.Clamp(Math.Max(300, configuredDelay), 280, 350);
        _thumbnailDebounceTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _thumbnailDebounceTimer.Stop();
        _thumbnailDebounceTimer.Start();
    }

    private void OnThumbnailDebounceTick(object? sender, EventArgs e)
    {
        _thumbnailDebounceTimer.Stop();
        LoadRealizedThumbnailWindow();
    }

    private void LoadRealizedThumbnailWindow()
    {
        if (ThumbnailListBox == null || DataContext is not ExplorerPaneViewModel vm ||
            !vm.IsThumbnailView || vm.SelectedTab == null)
        {
            return;
        }

        var panel = ThumbnailListBox.FindDescendantOfType<VirtualizingStackPanel>();
        int firstRow = panel?.FirstRealizedIndex ?? 0;
        int lastRow = panel?.LastRealizedIndex ?? Math.Min(vm.ThumbnailRows.Count - 1, 3);
        if (firstRow < 0 || lastRow < firstRow) return;

        var items = vm.SelectedTab.FilteredItems;
        var window = ThumbnailViewportPlanner.Plan(
            items.Count,
            vm.ThumbnailColumnCount,
            firstRow,
            lastRow,
            SettingsService.Instance.CurrentSettings.ThumbnailPrefetchViewports);

        var visible = new List<FileItem>(window.VisibleEnd - window.VisibleStart);
        var prefetch = new List<FileItem>(Math.Max(0, window.RetainedEnd - window.RetainedStart - visible.Count));
        var retained = new HashSet<FileItem>();
        for (int index = window.RetainedStart; index < window.RetainedEnd; index++)
        {
            var item = items[index];
            retained.Add(item);
            if (index >= window.VisibleStart && index < window.VisibleEnd) visible.Add(item);
            else prefetch.Add(item);
        }

        foreach (var oldItem in _retainedThumbnailItems)
        {
            if (!retained.Contains(oldItem)) oldItem.ThumbnailImage = null;
        }
        _retainedThumbnailItems = retained;

        _thumbnailViewportCts?.Dispose();
        _thumbnailViewportCts = new CancellationTokenSource();
        _ = ThumbnailService.Instance.LoadViewportAsync(
            visible,
            prefetch,
            (int)vm.ThumbnailSize,
            _thumbnailViewportCts.Token);
    }

    private void OnThumbnailItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: FileItem item } ||
            DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        bool rightClick = point.Properties.IsRightButtonPressed;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var tab = vm.SelectedTab;

        if (rightClick)
        {
            if (item.IsThumbnailSelected || tab.SelectedItems.Contains(item))
            {
                // Preserve existing multi-selection if right-clicked item is already selected
                tab.SelectedItem = item;
            }
            else
            {
                // Select only this item before context menu
                tab.SelectThumbnailItem(item, control: false, shift: false);
            }
        }
        else
        {
            _dragStartPoint = e.GetPosition(this);
            _dragCandidateItem = item;
            _isDragActive = false;
            _dragOccurredForCurrentPress = false;

            // Explorer behavior:
            // Pressing an item that is already part of a multi-selection must
            // preserve the group long enough to allow drag-and-drop. If this
            // turns out to be a click rather than a drag, collapse on release.
            if (!ctrl && !shift &&
                item.IsThumbnailSelected &&
                tab.SelectedItems.Count > 1)
            {
                _pendingPlainClickItem = item;
            }
            else
            {
                _pendingPlainClickItem = null;
                tab.SelectThumbnailItem(item, ctrl, shift);
            }
        }

        vm.NotifyContextMenuProperties();
    }

    private async void OnThumbnailItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FileItem item } && DataContext is ExplorerPaneViewModel vm)
        {
            e.Handled = true;
            await vm.OpenItem(item);
        }
    }


    private void SaveCurrentColumnLayout()
    {
        if (FileDataGrid == null || DataContext is not ExplorerPaneViewModel vm) return;

        var s = SettingsService.Instance.CurrentSettings;
        bool changed = false;

        foreach (var col in FileDataGrid.Columns)
        {
            var header = BaseColumnHeader(col.Header?.ToString());
            double actualWidth = col.ActualWidth;
            if (actualWidth > 20)
            {
                switch (header)
                {
                    case "Name":
                        s.ColumnWidthName = actualWidth;
                        vm.ColumnWidthName = actualWidth;
                        changed = true;
                        break;
                    case "Ext":
                        s.ColumnWidthExt = actualWidth;
                        vm.ColumnWidthExt = actualWidth;
                        changed = true;
                        break;
                    case "Size":
                        s.ColumnWidthSize = actualWidth;
                        vm.ColumnWidthSize = actualWidth;
                        changed = true;
                        break;
                    case "Date Modified":
                        s.ColumnWidthDateModified = actualWidth;
                        vm.ColumnWidthDateModified = actualWidth;
                        changed = true;
                        break;
                    case "Date Created":
                        s.ColumnWidthDateCreated = actualWidth;
                        vm.ColumnWidthDateCreated = actualWidth;
                        changed = true;
                        break;
                    case "Date Accessed":
                        s.ColumnWidthDateAccessed = actualWidth;
                        vm.ColumnWidthDateAccessed = actualWidth;
                        changed = true;
                        break;
                    case "Type":
                        s.ColumnWidthItemType = actualWidth;
                        vm.ColumnWidthItemType = actualWidth;
                        changed = true;
                        break;
                    case "Attributes":
                        s.ColumnWidthAttributes = actualWidth;
                        vm.ColumnWidthAttributes = actualWidth;
                        changed = true;
                        break;
                    case "Permissions":
                        s.ColumnWidthPermissions = actualWidth;
                        vm.ColumnWidthPermissions = actualWidth;
                        changed = true;
                        break;
                    case "Owner:Group":
                        s.ColumnWidthOwnerGroup = actualWidth;
                        vm.ColumnWidthOwnerGroup = actualWidth;
                        changed = true;
                        break;
                }
            }
        }

        if (changed)
        {
            SettingsService.Instance.SaveSettings(s);
        }
        vm.SetCurrentColumnOrder(FileDataGrid.Columns
            .OrderBy(column => column.DisplayIndex)
            .Select(column => BaseColumnHeader(column.Header?.ToString())));
    }

    private void RestoreColumnOrder(IReadOnlyList<string> savedOrder)
    {
        if (FileDataGrid == null || savedOrder.Count == 0) return;
        var byHeader = FileDataGrid.Columns
            .Where(column => column.Header != null)
            .ToDictionary(column => BaseColumnHeader(column.Header!.ToString()), StringComparer.Ordinal);
        int displayIndex = 0;
        foreach (string header in savedOrder)
        {
            if (byHeader.Remove(header, out var column)) column.DisplayIndex = displayIndex++;
        }
        foreach (var column in byHeader.Values.OrderBy(column => column.DisplayIndex))
            column.DisplayIndex = displayIndex++;
    }

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
        string sortColumn = HeaderToSortColumn(e.Column.Header?.ToString());
        vm.SelectedTab.SortBy(sortColumn);
        vm.PersistCurrentFolderViewState();
        e.Handled = true;
        vm.NotifySortHeadersChanged();
    }

    private static string BaseColumnHeader(string? header) =>
        (header ?? string.Empty).TrimEnd(' ', '↑', '↓');

    private static string HeaderToSortColumn(string? header) => BaseColumnHeader(header) switch
    {
        "Ext" => "Extension",
        "Size" => "Size",
        "Date Modified" => "Modified",
        "Date Created" => "Created",
        "Date Accessed" => "Accessed",
        "Type" => "Type",
        "Attributes" => "Attributes",
        "Permissions" => "Permissions",
        "Owner:Group" => "OwnerGroup",
        _ => "Name"
    };

    private void OnDataGridPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (FileDataGrid == null || DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
        var tab = vm.SelectedTab;

        var rawSource = e.Source as Visual;
        var check = rawSource;
        while (check != null && check != FileDataGrid)
        {
            if (check is ScrollBar || check is Thumb || check is Track || check is DataGridColumnHeader || check is Button)
            {
                return; // Ignore scrollbar and column header clicks completely
            }
            check = check.GetVisualParent();
        }

        var source = rawSource;
        while (source != null && source is not DataGridRow && source.GetType().Name != "DataGridColumnHeader" && source != FileDataGrid)
        {
            source = source.GetVisualParent();
        }

        bool isRightButton = e.GetCurrentPoint(FileDataGrid).Properties.IsRightButtonPressed;
        bool isLeftButton = e.GetCurrentPoint(FileDataGrid).Properties.IsLeftButtonPressed;

        if (source is DataGridRow row && row.DataContext is FileItem item)
        {
            if (isLeftButton)
            {
                _dragStartPoint = e.GetPosition(this);
                _dragCandidateItem = item;
                _isDragActive = false;
                _dragOccurredForCurrentPress = false;

                bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                if (shift)
                {
                    _pendingPlainClickItem = null;
                    ApplyDetailsRangeSelection(vm, item, additive: ctrl);

                    // Do not allow DataGrid's separate internal anchor to apply
                    // another range after ours.
                    e.Handled = true;
                    return;
                }

                // Ctrl/plain clicks establish a new Shift anchor.
                tab.SetSelectionAnchor(item);

                // Preserve an existing multi-selection during pointer-down so
                // dragging any selected row drags the whole selection.
                if (!ctrl &&
                    FileDataGrid.SelectedItems.Contains(item) &&
                    FileDataGrid.SelectedItems.Count > 1)
                {
                    _pendingPlainClickItem = item;
                    FileDataGrid.Focus();
                    e.Handled = true;
                    return;
                }

                _pendingPlainClickItem = null;
            }
            else if (isRightButton)
            {
                _pendingPlainClickItem = null;

                if (FileDataGrid.SelectedItems.Contains(item) || tab.SelectedItems.Contains(item))
                {
                    // Right-clicking an item already part of a multi-selection preserves the multi-selection
                    tab.SelectedItem = item;
                }
                else
                {
                    // Right-clicking an unselected item selects that item before showing context menu
                    FileDataGrid.SelectedItems.Clear();
                    FileDataGrid.SelectedItems.Add(item);
                    tab.SelectedItems.Clear();
                    tab.SelectedItems.Add(item);
                    tab.SelectedItem = item;
                    tab.SetSelectionAnchor(item);
                }
                vm.NotifyContextMenuProperties();
            }
        }
        else if (source?.GetType().Name != "DataGridColumnHeader")
        {
            // Clicked on empty space (below rows or background area)
            if (isRightButton)
            {
                _pendingPlainClickItem = null;
                FileDataGrid.SelectedItems.Clear();
                tab.ClearThumbnailSelection();
                tab.SelectedItems.Clear();
                tab.SelectedItem = null;
                tab.SetSelectionAnchor(null);
                vm.NotifyContextMenuProperties();
                vm.TriggerPreviewForSelectedItem();
            }
            else if (isLeftButton && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _pendingPlainClickItem = null;
                FileDataGrid.SelectedItems.Clear();
                tab.ClearThumbnailSelection();
                tab.SelectedItems.Clear();
                tab.SelectedItem = null;
                tab.SetSelectionAnchor(null);
                vm.NotifyContextMenuProperties();
                vm.TriggerPreviewForSelectedItem();
            }
        }
    }

    private void OnThumbnailListBoxPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null || ThumbnailListBox == null) return;

        var rawSource = e.Source as Visual;
        var check = rawSource;
        while (check != null && check != ThumbnailListBox)
        {
            if (check is ScrollBar || check is Thumb || check is Track || check is Button)
            {
                return; // Ignore scrollbar clicks completely
            }
            check = check.GetVisualParent();
        }

        var source = rawSource;
        // Check if the click happened on a thumbnail card (FileItem DataContext)
        while (source != null && source != ThumbnailListBox)
        {
            if (source is Control c && c.DataContext is FileItem thumbItem)
            {
                if (e.GetCurrentPoint(ThumbnailListBox).Properties.IsLeftButtonPressed)
                {
                    _dragStartPoint = e.GetPosition(this);
                    _dragCandidateItem = thumbItem;
                    _isDragActive = false;
                }
                // Clicked on a specific thumbnail item - let OnThumbnailItemPointerPressed handle selection
                return;
            }
            source = source.GetVisualParent();
        }

        // The click occurred on empty space inside the Thumbnail view (e.g. to the right of cards, between rows, or below)!
        var point = e.GetCurrentPoint(ThumbnailListBox);
        var tab = vm.SelectedTab;

        if (point.Properties.IsLeftButtonPressed)
        {
            _isMouseDownForMarquee = true;
            _isMarqueeActive = false;
            _marqueeStartPos = e.GetPosition(FileGridContainer);
            _lastMarqueePos = _marqueeStartPos;
            _marqueeBaseSelection = e.KeyModifiers.HasFlag(KeyModifiers.Control) && tab != null
                ? tab.SelectedItems.ToHashSet()
                : new HashSet<FileItem>();

            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && tab != null)
            {
                tab.ClearThumbnailSelection();
                tab.SelectedItems.Clear();
                tab.SelectedItem = null;
                tab.SetSelectionAnchor(null);
                vm.NotifyContextMenuProperties();
                vm.TriggerPreviewForSelectedItem();
            }
        }
        else if (point.Properties.IsRightButtonPressed && tab != null)
        {
            tab.ClearThumbnailSelection();
            tab.SelectedItems.Clear();
            tab.SelectedItem = null;
            tab.SetSelectionAnchor(null);
            vm.NotifyContextMenuProperties();
            vm.TriggerPreviewForSelectedItem();
        }
    }

    private void OnPointerMovedTunnel(object? sender, PointerEventArgs e)
    {
        if (_isMiddleAutoScrolling && FileGridContainer != null)
        {
            _currentPointerPos = e.GetPosition(FileGridContainer);
            var delta = _currentPointerPos - _autoScrollAnchorPos;
            if (Math.Abs(delta.X) > 8 || Math.Abs(delta.Y) > 8)
            {
                _hasMovedDuringMiddleScroll = true;
            }
            return;
        }

        if (_dragCandidateItem != null && !_isDragActive)
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed)
            {
                var delta = e.GetPosition(this) - _dragStartPoint;
                if (Math.Abs(delta.X) >= 4 || Math.Abs(delta.Y) >= 4)
                {
                    _isDragActive = true;
                    _dragOccurredForCurrentPress = true;
                    _isMouseDownForMarquee = false;
                    _isMarqueeActive = false;
                    if (MarqueeBox != null) MarqueeBox.IsVisible = false;
                    StartDragAsync(e, _dragCandidateItem);
                    return;
                }
            }
        }

        if (!_isMouseDownForMarquee || FileGridContainer == null || DataContext is not ExplorerPaneViewModel vm) return;

        var cur = e.GetPosition(FileGridContainer);
        var deltaMarquee = cur - _marqueeStartPos;

        if (!_isMarqueeActive && PointerGestureClassifier.ExceedsDragThreshold(deltaMarquee.X, deltaMarquee.Y, 4))
        {
            _isMarqueeActive = true;
            vm.IsSuppressingPreview = true;
            e.Pointer.Capture(FileGridContainer);
            if (MarqueeBox != null) MarqueeBox.IsVisible = true;
            _autoScrollTimer.Start();
        }

        if (_isMarqueeActive)
        {
            vm.IsSuppressingPreview = true;
            _lastMarqueePos = cur;

            if (MarqueeBox != null)
            {
                double minX = Math.Min(_marqueeStartPos.X, cur.X);
                double minY = Math.Min(_marqueeStartPos.Y, cur.Y);
                double width = Math.Abs(cur.X - _marqueeStartPos.X);
                double height = Math.Abs(cur.Y - _marqueeStartPos.Y);

                Canvas.SetLeft(MarqueeBox, minX);
                Canvas.SetTop(MarqueeBox, minY);
                MarqueeBox.Width = width;
                MarqueeBox.Height = height;
            }

            UpdateMarqueeSelection(e.KeyModifiers.HasFlag(KeyModifiers.Control));

            // Velocity-based auto-scroll calculation
            if (cur.Y < 20)
            {
                _autoScrollVelocity = Math.Min(-2.0, (cur.Y - 20) * 0.8);
            }
            else if (cur.Y > FileGridContainer.Bounds.Height - 20)
            {
                _autoScrollVelocity = Math.Max(2.0, (cur.Y - (FileGridContainer.Bounds.Height - 20)) * 0.8);
            }
            else
            {
                _autoScrollVelocity = 0;
            }
        }
    }

    private void OnPointerReleasedTunnel(object? sender, PointerReleasedEventArgs e)
    {
        bool dragOccurred = _dragOccurredForCurrentPress;
        var pendingPlainClickItem = _pendingPlainClickItem;

        _pendingPlainClickItem = null;
        _dragOccurredForCurrentPress = false;
        _dragCandidateItem = null;
        _isDragActive = false;

        var vm = DataContext as ExplorerPaneViewModel;

        if (!dragOccurred &&
            pendingPlainClickItem != null &&
            vm != null)
        {
            CollapseSelectionToItem(vm, pendingPlainClickItem);
            e.Handled = true;
        }

        if (_isMouseDownForMarquee)
        {
            _isMouseDownForMarquee = false;
            if (_isMarqueeActive)
            {
                _isMarqueeActive = false;
                _autoScrollTimer.Stop();
                _autoScrollVelocity = 0;
                e.Pointer.Capture(null);
                if (MarqueeBox != null) MarqueeBox.IsVisible = false;
                if (vm != null)
                {
                    vm.IsSuppressingPreview = false;
                }
            }
        }
    }

    private void OnPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsXButton1Pressed)
            {
                vm.GoBack();
                e.Handled = true;
            }
            else if (props.IsXButton2Pressed)
            {
                vm.GoForward();
                e.Handled = true;
            }
        }
    }

    private void OnPanePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
    }

    private ExplorerTabViewModel? _pressedTab;
    private Point _tabPressStartPoint;
    private bool _isTabDragging;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Visual visual && visual.DataContext is ExplorerTabViewModel tab && DataContext is ExplorerPaneViewModel vm)
        {
            if (e.GetCurrentPoint(visual).Properties.IsLeftButtonPressed)
            {
                _pressedTab = tab;
                _tabPressStartPoint = e.GetPosition(this);
                _isTabDragging = false;
                vm.SelectedTab = tab;
            }
        }
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTab != null && DataContext is ExplorerPaneViewModel vm)
        {
            var cur = e.GetPosition(this);
            var delta = cur - _tabPressStartPoint;

            var topLevel = TopLevel.GetTopLevel(this);
            var windowPos = topLevel != null ? e.GetPosition(topLevel) : cur;

            if (!_isTabDragging && PointerGestureClassifier.ExceedsDragThreshold(delta.X, delta.Y, 6))
            {
                _isTabDragging = true;
                TabDragCoordinator.Instance.StartDrag(_pressedTab, vm, e.KeyModifiers.HasFlag(KeyModifiers.Control), windowPos);
            }
        }
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedTab = null;
        _isTabDragging = false;
    }

    private void OnTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _pressedTab = null;
        _isTabDragging = false;
    }

    private void UpdateTabScrollButtonsVisibility()
    {
        if (TabsScrollViewer != null && TabScrollButtonsPanel != null)
        {
            var content = TabsScrollViewer.Presenter?.Content as Control;
            double contentWidth = content != null && content.Bounds.Width > 0 ? content.Bounds.Width : TabsScrollViewer.Extent.Width;
            double viewportWidth = TabsScrollViewer.Viewport.Width;
            if (contentWidth <= viewportWidth + 4 && TabsScrollViewer.Offset.X > 0)
            {
                TabsScrollViewer.Offset = new Vector(0, TabsScrollViewer.Offset.Y);
            }
            bool canScroll = contentWidth > viewportWidth + 4;
            TabScrollButtonsPanel.IsVisible = canScroll;
        }
    }

    private void OnTabsPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (TabsScrollViewer != null)
        {
            double scrollDelta = e.Delta.Y != 0 ? -e.Delta.Y * 70 : -e.Delta.X * 70;
            double maxOffset = Math.Max(0, TabsScrollViewer.Extent.Width - TabsScrollViewer.Viewport.Width);
            double targetOffset = Math.Clamp(TabsScrollViewer.Offset.X + scrollDelta, 0, maxOffset);
            TabsScrollViewer.Offset = new Vector(targetOffset, TabsScrollViewer.Offset.Y);
            UpdateTabScrollButtonsVisibility();
            e.Handled = true;
        }
    }

    private void OnTabScrollLeftClicked(object? sender, RoutedEventArgs e)
    {
        if (TabsScrollViewer != null)
        {
            double targetOffset = Math.Max(0, TabsScrollViewer.Offset.X - 120);
            TabsScrollViewer.Offset = new Vector(targetOffset, TabsScrollViewer.Offset.Y);
            UpdateTabScrollButtonsVisibility();
        }
    }

    private void OnTabScrollRightClicked(object? sender, RoutedEventArgs e)
    {
        if (TabsScrollViewer != null)
        {
            double maxOffset = Math.Max(0, TabsScrollViewer.Extent.Width - TabsScrollViewer.Viewport.Width);
            double targetOffset = Math.Min(maxOffset, TabsScrollViewer.Offset.X + 120);
            TabsScrollViewer.Offset = new Vector(targetOffset, TabsScrollViewer.Offset.Y);
            UpdateTabScrollButtonsVisibility();
        }
    }

    private void OnFolderBackgroundStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm && vm.SelectedTab != null)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                if (FileDataGrid != null) FileDataGrid.SelectedItems.Clear();
                vm.SelectedTab.ClearThumbnailSelection();
                vm.SelectedTab.SelectedItems.Clear();
                vm.SelectedTab.SelectedItem = null;
                vm.NotifyContextMenuProperties();
                vm.TriggerPreviewForSelectedItem();
            }
            else if (props.IsLeftButtonPressed)
            {
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    if (FileDataGrid != null) FileDataGrid.SelectedItems.Clear();
                    vm.SelectedTab.ClearThumbnailSelection();
                    vm.SelectedTab.SelectedItems.Clear();
                    vm.SelectedTab.SelectedItem = null;
                    vm.NotifyContextMenuProperties();
                    vm.TriggerPreviewForSelectedItem();
                }

                if (vm.IsThumbnailView) return;

                if (FileGridContainer != null)
                {
                    _isMouseDownForMarquee = true;
                    _isMarqueeActive = false;
                    _marqueeStartPos = e.GetPosition(FileGridContainer);
                    _lastMarqueePos = _marqueeStartPos;
                    _marqueeBaseSelection = e.KeyModifiers.HasFlag(KeyModifiers.Control) && FileDataGrid != null
                        ? FileDataGrid.SelectedItems.Cast<FileItem>().ToHashSet()
                        : new HashSet<FileItem>();
                }
            }
        }
    }

    private void OnFileGridPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (_isMiddleAutoScrolling)
        {
            StopMiddleAutoScroll();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (FileGridContainer != null)
        {
            var props = e.GetCurrentPoint(FileGridContainer).Properties;
            if (props.IsMiddleButtonPressed)
            {
                StartMiddleAutoScroll(e);
                e.Handled = true;
            }
        }
    }

    private void StartMiddleAutoScroll(PointerPressedEventArgs e)
    {
        if (FileGridContainer == null) return;

        _isMiddleAutoScrolling = true;
        _hasMovedDuringMiddleScroll = false;
        _autoScrollAnchorPos = e.GetPosition(FileGridContainer);
        _currentPointerPos = _autoScrollAnchorPos;
        _activeMiddleScrollViewer = GetActiveMiddleScrollViewer();

        if (AutoScrollAnchor != null && AutoScrollCanvas != null)
        {
            double halfW = AutoScrollAnchor.Width > 0 ? AutoScrollAnchor.Width / 2 : 14;
            double halfH = AutoScrollAnchor.Height > 0 ? AutoScrollAnchor.Height / 2 : 14;
            Canvas.SetLeft(AutoScrollAnchor, _autoScrollAnchorPos.X - halfW);
            Canvas.SetTop(AutoScrollAnchor, _autoScrollAnchorPos.Y - halfH);
            AutoScrollCanvas.IsVisible = true;
        }

        e.Pointer.Capture(FileGridContainer);
        _middleScrollTimer.Start();
    }

    private void StopMiddleAutoScroll()
    {
        if (!_isMiddleAutoScrolling) return;

        _isMiddleAutoScrolling = false;
        _middleScrollTimer.Stop();
        _activeMiddleScrollViewer = null;

        if (AutoScrollCanvas != null)
        {
            AutoScrollCanvas.IsVisible = false;
        }
    }

    private ScrollViewer? GetActiveMiddleScrollViewer()
    {
        if (DataContext is ExplorerPaneViewModel vm)
        {
            if (vm.IsThumbnailView && ThumbnailListBox != null)
            {
                return ThumbnailListBox.FindDescendantOfType<ScrollViewer>();
            }
            else if (FileDataGrid != null)
            {
                return FileDataGrid.FindDescendantOfType<ScrollViewer>();
            }
        }
        return null;
    }

    private void OnPaneKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_isMiddleAutoScrolling && e.Key == Key.Escape)
        {
            StopMiddleAutoScroll();
            if (FileGridContainer != null)
            {
                // TopLevel capture release
            }
            e.Handled = true;
            return;
        }

        if (DataContext is not ExplorerPaneViewModel vm) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        KeyboardShortcutHandler.HandlePaneKeyDown(vm, e, focused);
    }

    private void OnMiddleScrollTick(object? sender, EventArgs e)
    {
        if (!_isMiddleAutoScrolling || _activeMiddleScrollViewer == null)
        {
            StopMiddleAutoScroll();
            return;
        }

        double dx = _currentPointerPos.X - _autoScrollAnchorPos.X;
        double dy = _currentPointerPos.Y - _autoScrollAnchorPos.Y;

        const double deadZone = 8.0;
        double vx = 0;
        double vy = 0;

        if (Math.Abs(dy) > deadZone)
        {
            double distY = Math.Abs(dy) - deadZone;
            double signY = Math.Sign(dy);
            vy = signY * Math.Pow(distY * 0.16, 1.4);
        }

        if (Math.Abs(dx) > deadZone && _activeMiddleScrollViewer.Extent.Width > _activeMiddleScrollViewer.Viewport.Width)
        {
            double distX = Math.Abs(dx) - deadZone;
            double signX = Math.Sign(dx);
            vx = signX * Math.Pow(distX * 0.16, 1.4);
        }

        if (vx != 0 || vy != 0)
        {
            double maxOffsetX = Math.Max(0, _activeMiddleScrollViewer.Extent.Width - _activeMiddleScrollViewer.Viewport.Width);
            double maxOffsetY = Math.Max(0, _activeMiddleScrollViewer.Extent.Height - _activeMiddleScrollViewer.Viewport.Height);

            double newX = Math.Clamp(_activeMiddleScrollViewer.Offset.X + vx, 0, maxOffsetX);
            double newY = Math.Clamp(_activeMiddleScrollViewer.Offset.Y + vy, 0, maxOffsetY);

            _activeMiddleScrollViewer.Offset = new Vector(newX, newY);
        }
    }

    private void OnFileGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (FileGridContainer == null || FileDataGrid == null || DataContext is not ExplorerPaneViewModel vm) return;

        var props = e.GetCurrentPoint(FileGridContainer).Properties;
        if (props.IsLeftButtonPressed)
        {
            var source = e.Source as Visual;
            if (vm.IsThumbnailView)
            {
                bool isThumbnailCard = false;
                FileItem? thumbItem = null;
                var curr = source;
                while (curr != null && curr != FileGridContainer)
                {
                    if (curr is ScrollBar || curr is Avalonia.Controls.Primitives.Thumb || curr is Avalonia.Controls.Primitives.Track ||
                        curr is Button || curr is GridSplitter)
                    {
                        return;
                    }
                    if (curr is Control { DataContext: FileItem fi })
                    {
                        thumbItem = fi;
                    }
                    if (curr is Border border && border.Classes.Contains("thumbnail-card"))
                    {
                        isThumbnailCard = true;
                        break;
                    }
                    curr = curr.GetVisualParent();
                }

                if (isThumbnailCard && thumbItem != null)
                {
                    _dragStartPoint = e.GetPosition(this);
                    _dragCandidateItem = thumbItem;
                    _isDragActive = false;
                }
                else
                {
                    // Clicked on empty space (the area on the right, between rows, or below cards)
                    _isMouseDownForMarquee = true;
                    _isMarqueeActive = false;
                    _marqueeStartPos = e.GetPosition(FileGridContainer);
                    _lastMarqueePos = _marqueeStartPos;
                    _marqueeBaseSelection = e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.SelectedTab != null
                        ? vm.SelectedTab.SelectedItems.ToHashSet()
                        : new HashSet<FileItem>();
                }
                return;
            }

            bool isInteractiveChrome = false;
            bool isRowOrCell = false;
            FileItem? rowItem = null;
            var currGrid = source;
            while (currGrid != null && currGrid != FileGridContainer)
            {
                if (currGrid is ScrollBar || currGrid is Avalonia.Controls.Primitives.Thumb || currGrid is Avalonia.Controls.Primitives.Track ||
                    currGrid is DataGridColumnHeader || currGrid is DataGridRowHeader || currGrid is Button || currGrid is GridSplitter)
                {
                    isInteractiveChrome = true;
                    break;
                }
                if (currGrid is Control { DataContext: FileItem fi })
                {
                    rowItem = fi;
                }
                if (currGrid is DataGridRow || currGrid is DataGridCell)
                {
                    isRowOrCell = true;
                    break;
                }
                currGrid = currGrid.GetVisualParent();
            }

            if (isInteractiveChrome)
            {
                // Clicking scrollbars, column headers, thumbs, or buttons must NOT trigger marquee, drag-drop, or selection clearing!
                _isMouseDownForMarquee = false;
                _isMarqueeActive = false;
                _dragCandidateItem = null;
                _isDragActive = false;
                return;
            }

            if (rowItem != null)
            {
                _dragStartPoint = e.GetPosition(this);
                _dragCandidateItem = rowItem;
                _isDragActive = false;
            }

            var interaction = PointerGestureClassifier.ClassifyPress(
                isRowOrCell ? PointerSurface.FileRow : PointerSurface.FileBackground);
            if (interaction == PointerInteraction.MarqueeSelection)
            {
                _isMouseDownForMarquee = true;
                _isMarqueeActive = false;
                _marqueeStartPos = e.GetPosition(FileGridContainer);
                _lastMarqueePos = _marqueeStartPos;
                _marqueeBaseSelection = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                    ? FileDataGrid.SelectedItems.Cast<FileItem>().ToHashSet()
                    : new HashSet<FileItem>();
            }
        }
    }

    private void OnFileGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isMiddleAutoScrolling)
        {
            _currentPointerPos = e.GetPosition(FileGridContainer);
            var delta = _currentPointerPos - _autoScrollAnchorPos;
            if (Math.Abs(delta.X) > 8 || Math.Abs(delta.Y) > 8)
            {
                _hasMovedDuringMiddleScroll = true;
            }
            return;
        }

        if (_dragCandidateItem != null && !_isDragActive && e.GetCurrentPoint(FileGridContainer).Properties.IsLeftButtonPressed)
        {
            var dragDelta = e.GetPosition(this) - _dragStartPoint;
            if (Math.Abs(dragDelta.X) >= 4 || Math.Abs(dragDelta.Y) >= 4)
            {
                _isDragActive = true;
                _dragOccurredForCurrentPress = true;
                _isMouseDownForMarquee = false;
                _isMarqueeActive = false;
                if (MarqueeBox != null) MarqueeBox.IsVisible = false;
                StartDragAsync(e, _dragCandidateItem);
                return;
            }
        }

        if (!_isMouseDownForMarquee || FileGridContainer == null || DataContext is not ExplorerPaneViewModel vm) return;

        var cur = e.GetPosition(FileGridContainer);
        var deltaMarquee = cur - _marqueeStartPos;

        if (!_isMarqueeActive && PointerGestureClassifier.ExceedsDragThreshold(deltaMarquee.X, deltaMarquee.Y, 4))
        {
            _isMarqueeActive = true;
            vm.IsSuppressingPreview = true;
            e.Pointer.Capture(FileGridContainer);
            if (MarqueeBox != null) MarqueeBox.IsVisible = true;
            _autoScrollTimer.Start();
        }

        if (_isMarqueeActive)
        {
            vm.IsSuppressingPreview = true;
            _lastMarqueePos = cur;

            if (MarqueeBox != null)
            {
                double minX = Math.Min(_marqueeStartPos.X, cur.X);
                double minY = Math.Min(_marqueeStartPos.Y, cur.Y);
                double width = Math.Abs(cur.X - _marqueeStartPos.X);
                double height = Math.Abs(cur.Y - _marqueeStartPos.Y);

                Canvas.SetLeft(MarqueeBox, minX);
                Canvas.SetTop(MarqueeBox, minY);
                MarqueeBox.Width = width;
                MarqueeBox.Height = height;
            }

            UpdateMarqueeSelection(e.KeyModifiers.HasFlag(KeyModifiers.Control));

            // Velocity-based auto-scroll calculation
            if (cur.Y < 20)
            {
                _autoScrollVelocity = Math.Min(-2.0, (cur.Y - 20) * 0.8);
            }
            else if (cur.Y > FileGridContainer.Bounds.Height - 20)
            {
                _autoScrollVelocity = Math.Max(2.0, (cur.Y - (FileGridContainer.Bounds.Height - 20)) * 0.8);
            }
            else
            {
                _autoScrollVelocity = 0;
            }
        }
    }

    private void OnFileGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragCandidateItem = null;
        _isDragActive = false;

        if (_isMiddleAutoScrolling)
        {
            var props = e.GetCurrentPoint(FileGridContainer).Properties;
            // If the middle button was released after moving, stop autoscroll (hold-to-scroll gesture)
            if (!props.IsMiddleButtonPressed && _hasMovedDuringMiddleScroll)
            {
                StopMiddleAutoScroll();
                e.Pointer.Capture(null);
                e.Handled = true;
            }
            return;
        }

        if (_isMouseDownForMarquee)
        {
            _isMouseDownForMarquee = false;
            if (_isMarqueeActive)
            {
                _isMarqueeActive = false;
                _autoScrollTimer.Stop();
                _autoScrollVelocity = 0;
                e.Pointer.Capture(null);
                if (MarqueeBox != null) MarqueeBox.IsVisible = false;
                if (DataContext is ExplorerPaneViewModel vm)
                {
                    vm.IsSuppressingPreview = false;
                }
            }
            else
            {
                // Click on blank background space without dragging
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    if (FileDataGrid != null) FileDataGrid.SelectedItems.Clear();
                    if (DataContext is ExplorerPaneViewModel vmEmpty && vmEmpty.SelectedTab != null)
                    {
                        vmEmpty.SelectedTab.ClearThumbnailSelection();
                        vmEmpty.SelectedTab.SelectedItems.Clear();
                        vmEmpty.SelectedTab.SelectedItem = null;
                        vmEmpty.NotifyContextMenuProperties();
                        vmEmpty.TriggerPreviewForSelectedItem();
                    }
                }
            }
        }
    }

    private void OnFileGridPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _pendingPlainClickItem = null;
        _dragOccurredForCurrentPress = false;

        if (_isMiddleAutoScrolling)
        {
            StopMiddleAutoScroll();
        }

        _autoScrollTimer.Stop();
        _autoScrollVelocity = 0;
        if (MarqueeBox != null) MarqueeBox.IsVisible = false;
        _isMarqueeActive = false;
        _isMouseDownForMarquee = false;
        if (DataContext is ExplorerPaneViewModel vm)
        {
            vm.IsSuppressingPreview = false;
        }
    }

    private void UpdateMarqueeSelection(bool isCtrl)
    {
        if (FileGridContainer == null || DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
        var tab = vm.SelectedTab;
        var items = tab.FilteredItems;
        if (items.Count == 0) return;

        double minX = Math.Min(_marqueeStartPos.X, _lastMarqueePos.X);
        double maxX = Math.Max(_marqueeStartPos.X, _lastMarqueePos.X);
        double minY = Math.Min(_marqueeStartPos.Y, _lastMarqueePos.Y);
        double maxY = Math.Max(_marqueeStartPos.Y, _lastMarqueePos.Y);

        if (vm.IsThumbnailView && ThumbnailListBox != null)
        {
            var visibleCards = ThumbnailListBox.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Classes.Contains("thumbnail-card") && b.DataContext is FileItem)
                .Select(b =>
                {
                    var pt = b.TranslatePoint(new Point(0, 0), FileGridContainer);
                    return new
                    {
                        Border = b,
                        Left = pt?.X ?? -1000,
                        Top = pt?.Y ?? -1000,
                        Width = b.Bounds.Width,
                        Height = b.Bounds.Height,
                        Item = (FileItem)b.DataContext!
                    };
                })
                .Where(x => x.Left >= -100 && x.Top >= -100 && x.Width > 0 && x.Height > 0)
                .ToList();

            var matchingItems = new HashSet<FileItem>();
            if (isCtrl)
            {
                foreach (var baseItem in _marqueeBaseSelection)
                {
                    matchingItems.Add(baseItem);
                }
            }

            foreach (var card in visibleCards)
            {
                double cardRight = card.Left + card.Width;
                double cardBottom = card.Top + card.Height;
                bool intersects = cardRight >= minX && card.Left <= maxX && cardBottom >= minY && card.Top <= maxY;
                if (intersects)
                {
                    matchingItems.Add(card.Item);
                }
            }

            // Apply thumbnail selection
            tab.ClearThumbnailSelection();
            foreach (var item in matchingItems)
            {
                tab.AddThumbnailSelection(item);
            }
            tab.SelectedItem = matchingItems.LastOrDefault();
            vm.NotifyContextMenuProperties();
            return;
        }

        if (FileDataGrid == null) return;

        var visibleRows = FileDataGrid.GetVisualDescendants()
            .OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.DataContext is FileItem)
            .Select(r =>
            {
                var pt = r.TranslatePoint(new Point(0, 0), FileGridContainer);
                return new { Row = r, Top = pt?.Y ?? -1, Height = r.Bounds.Height, Item = (FileItem)r.DataContext! };
            })
            .Where(x => x.Top >= 0 && x.Height > 0)
            .OrderBy(x => x.Top)
            .ToList();

        HashSet<int> targetIndexes = new();

        var baseIndexes = _marqueeBaseSelection
            .Select(items.IndexOf)
            .Where(index => index >= 0);

        if (visibleRows.Count > 0)
        {
            var firstRow = visibleRows[0];
            double rowHeight = firstRow.Height;
            double firstRowTop = firstRow.Top;
            int firstVisibleIndex = items.IndexOf(firstRow.Item);

            if (firstVisibleIndex >= 0 && rowHeight > 0)
            {
                targetIndexes = MarqueeSelectionCalculator.CalculateFromVisibleRow(
                    minY,
                    maxY,
                    firstVisibleIndex,
                    firstRowTop,
                    rowHeight,
                    items.Count,
                    baseIndexes,
                    isCtrl);
            }
        }

        if (targetIndexes.Count == 0)
        {
            if (isCtrl)
            {
                foreach (var idx in baseIndexes) targetIndexes.Add(idx);
            }

            foreach (var vr in visibleRows)
            {
                double bottom = vr.Top + vr.Height;
                if (bottom >= minY && vr.Top <= maxY)
                {
                    int idx = items.IndexOf(vr.Item);
                    if (idx >= 0) targetIndexes.Add(idx);
                }
            }
        }

        var targetItems = targetIndexes
            .Where(index => index >= 0 && index < items.Count)
            .Select(index => items[index])
            .ToHashSet();

        // Synchronize with FileDataGrid
        FileDataGrid.SelectedItems.Clear();
        foreach (var item in targetItems)
        {
            FileDataGrid.SelectedItems.Add(item);
        }

        if (targetItems.Count > 0 && (vm.SelectedTab.SelectedItem == null || !targetItems.Contains(vm.SelectedTab.SelectedItem)))
        {
            vm.SelectedTab.SelectedItem = targetItems.Last();
        }
        else if (targetItems.Count == 0)
        {
            vm.SelectedTab.SelectedItem = null;
        }
        vm.NotifyContextMenuProperties();
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (_isMarqueeActive && _autoScrollVelocity != 0 && DataContext is ExplorerPaneViewModel vm)
        {
            ScrollViewer? sv = vm.IsThumbnailView && ThumbnailListBox != null
                ? ThumbnailListBox.FindDescendantOfType<ScrollViewer>()
                : FileDataGrid?.FindDescendantOfType<ScrollViewer>();

            if (sv != null)
            {
                sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + _autoScrollVelocity));
            }
            UpdateMarqueeSelection(false);
        }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ExplorerPaneViewModel vm)
        {
            vm.SubmitAddressCommand.Execute(null);
        }
    }

    private void OnRenameTextBoxAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is FileItem item)
        {
            Dispatcher.UIThread.Post(() =>
            {
                tb.Focus();
                string name = tb.Text ?? item.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    int dotIndex = item.IsDirectory ? -1 : name.LastIndexOf('.');
                    if (dotIndex > 0)
                    {
                        tb.SelectionStart = 0;
                        tb.SelectionEnd = dotIndex;
                    }
                    else
                    {
                        tb.SelectAll();
                    }
                }
            }, DispatcherPriority.Input);
        }
    }

    private void OnRenameTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is FileItem item && DataContext is ExplorerPaneViewModel vm)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitInlineRename(item, tb.Text, vm);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                item.IsRenaming = false;
                item.EditingName = item.Name;
            }
        }
    }

    private void OnRenameTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is FileItem item && DataContext is ExplorerPaneViewModel vm)
        {
            if (item.IsRenaming)
            {
                CommitInlineRename(item, tb.Text, vm);
            }
        }
    }

    private async void CommitInlineRename(FileItem item, string? newName, ExplorerPaneViewModel vm)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;

        newName = newName?.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name)
        {
            item.EditingName = item.Name;
            return;
        }

        // Check for invalid filename characters
        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (newName.IndexOfAny(invalidChars) >= 0)
        {
            item.EditingName = item.Name;
            return;
        }

        try
        {
            if (!await vm.RenameItemAsync(item, newName))
            {
                item.EditingName = item.Name;
            }
        }
        catch
        {
            item.EditingName = item.Name;
        }
    }

    private void OnDataGridKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ExplorerPaneViewModel vm) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        KeyboardShortcutHandler.HandlePaneKeyDown(vm, e, focused);
    }

    private void OnThumbnailKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ExplorerPaneViewModel vm) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        KeyboardShortcutHandler.HandlePaneKeyDown(vm, e, focused);
    }

    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab?.SelectedItem == null) return;

        // Ensure double-tap actually occurred on a row/cell, and NOT on the ScrollBar, ColumnHeader, or empty area
        var source = e.Source as Visual;
        bool isRow = false;
        var curr = source;
        while (curr != null && curr != FileDataGrid)
        {
            if (curr is ScrollBar || curr is Avalonia.Controls.Primitives.Thumb || curr is DataGridColumnHeader || curr is Button)
            {
                return;
            }
            if (curr is DataGridRow || curr is DataGridCell)
            {
                isRow = true;
                break;
            }
            curr = curr.GetVisualParent();
        }

        if (isRow)
        {
            e.Handled = true;
            await vm.OpenItem(vm.SelectedTab.SelectedItem);
        }
    }

    private void CollapseSelectionToItem(ExplorerPaneViewModel vm, FileItem item)
    {
        if (vm.SelectedTab == null) return;
        var tab = vm.SelectedTab;

        if (vm.IsThumbnailView)
        {
            tab.SelectThumbnailItem(item, control: false, shift: false);
        }
        else if (FileDataGrid != null)
        {
            _isApplyingDetailsSelection = true;
            try
            {
                FileDataGrid.SelectedItems.Clear();
                FileDataGrid.SelectedItems.Add(item);
                FileDataGrid.SelectedItem = item;
            }
            finally
            {
                _isApplyingDetailsSelection = false;
            }

            SyncDetailsSelectionToTab(vm, item);
            tab.SetSelectionAnchor(item);
        }

        vm.NotifyContextMenuProperties();
        vm.TriggerPreviewForSelectedItem();
    }

    private void ApplyDetailsRangeSelection(
        ExplorerPaneViewModel vm,
        FileItem item,
        bool additive)
    {
        if (FileDataGrid == null || vm.SelectedTab == null) return;

        var tab = vm.SelectedTab;
        var range = tab.GetSelectionRange(item);
        if (range.Count == 0) return;

        _isApplyingDetailsSelection = true;
        try
        {
            if (!additive)
                FileDataGrid.SelectedItems.Clear();

            foreach (var rangeItem in range)
            {
                if (!FileDataGrid.SelectedItems.Contains(rangeItem))
                    FileDataGrid.SelectedItems.Add(rangeItem);
            }

            FileDataGrid.SelectedItem = item;
        }
        finally
        {
            _isApplyingDetailsSelection = false;
        }

        SyncDetailsSelectionToTab(vm, item);
        vm.NotifyContextMenuProperties();
        vm.TriggerPreviewForSelectedItem();
    }

    private void SyncDetailsSelectionToTab(
        ExplorerPaneViewModel vm,
        FileItem? preferredActiveItem = null)
    {
        if (FileDataGrid == null || vm.SelectedTab == null) return;

        var tab = vm.SelectedTab;
        var currentGridSelected = FileDataGrid.SelectedItems
            .Cast<FileItem>()
            .ToList();

        tab.SelectedItems.Clear();
        foreach (var selected in currentGridSelected)
            tab.SelectedItems.Add(selected);

        FileItem? activeItem = preferredActiveItem
            ?? FileDataGrid.SelectedItem as FileItem;

        tab.SelectedItem =
            activeItem != null && currentGridSelected.Contains(activeItem)
                ? activeItem
                : currentGridSelected.LastOrDefault();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingDetailsSelection) return;
        if (DataContext is not ExplorerPaneViewModel vm ||
            vm.SelectedTab == null ||
            FileDataGrid == null)
            return;

        // The DataGrid is authoritative here. Do not resurrect an old
        // SelectedItem after the grid has explicitly cleared its selection.
        SyncDetailsSelectionToTab(vm);
        vm.NotifyContextMenuProperties();
    }

    private async void OnCopyPathClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm && vm.SelectedTab != null)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                var path = vm.SelectedTab.SelectedItem?.FullPath ?? vm.SelectedTab.CurrentPath;
                await topLevel.Clipboard.SetTextAsync(path);
            }
        }
    }

    #region Windows Drag-and-Drop Implementation

    private async void StartDragAsync(PointerEventArgs triggerEvent, FileItem triggerItem)
    {
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
        var tab = vm.SelectedTab;

        List<string> dragPaths;
        bool isAlreadySelected = (vm.IsThumbnailView && triggerItem.IsThumbnailSelected) ||
                                 (!vm.IsThumbnailView && tab.SelectedItems.Contains(triggerItem));

        if (isAlreadySelected)
        {
            dragPaths = tab.SelectedItems
                .Where(i => !string.IsNullOrEmpty(i.FullPath))
                .Select(i => i.FullPath)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToList();
        }
        else
        {
            if (vm.IsThumbnailView)
            {
                tab.SelectThumbnailItem(triggerItem, control: false, shift: false);
            }
            else
            {
                tab.SelectedItems.Clear();
                tab.SelectedItems.Add(triggerItem);
                tab.SelectedItem = triggerItem;
            }
            dragPaths = new List<string> { triggerItem.FullPath };
        }

        if (dragPaths.Count == 0) return;

        var dataObject = new DataObject();
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        var storageItems = new List<Avalonia.Platform.Storage.IStorageItem>();

        if (storageProvider != null)
        {
            foreach (var p in dragPaths)
            {
                try
                {
                    var fileUri = new Uri(Path.GetFullPath(p));
                    if (Directory.Exists(p))
                    {
                        var f = storageProvider.TryGetFolderFromPathAsync(fileUri).GetAwaiter().GetResult();
                        if (f != null) storageItems.Add(f);
                    }
                    else if (File.Exists(p))
                    {
                        var f = storageProvider.TryGetFileFromPathAsync(fileUri).GetAwaiter().GetResult();
                        if (f != null) storageItems.Add(f);
                    }
                }
                catch { }
            }
        }

        if (storageItems.Count > 0)
        {
            dataObject.Set(DataFormats.Files, storageItems);
        }
        dataObject.Set(DataFormats.FileNames, dragPaths);
        dataObject.Set(DataFormats.Text, string.Join(Environment.NewLine, dragPaths));

        try
        {
            await DragDrop.DoDragDrop(triggerEvent, dataObject, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch { }
        finally
        {
            _dragCandidateItem = null;
            _isDragActive = false;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDragOverState(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        UpdateDragOverState(e);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        ClearDragOverHighlight();
    }

    private void ClearDragOverHighlight()
    {
        if (_hoveredDragTarget != null)
        {
            _hoveredDragTarget.IsDragOver = false;
            _hoveredDragTarget = null;
        }
    }

    private void UpdateDragOverState(DragEventArgs e)
    {
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDragOverHighlight();
            return;
        }

        var sourcePaths = FileDragDropService.ExtractPaths(e.Data);
        if (sourcePaths.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDragOverHighlight();
            return;
        }

        // Find folder under cursor if any
        FileItem? targetFolder = null;
        if (e.Source is Visual visual)
        {
            var element = visual;
            while (element != null && element != this)
            {
                if (element is Control { DataContext: FileItem item } && item.IsDirectory)
                {
                    targetFolder = item;
                    break;
                }
                element = element.GetVisualParent();
            }
        }

        if (targetFolder != _hoveredDragTarget)
        {
            ClearDragOverHighlight();
            if (targetFolder != null)
            {
                _hoveredDragTarget = targetFolder;
                _hoveredDragTarget.IsDragOver = true;
            }
        }

        string destDir = targetFolder?.FullPath ?? vm.SelectedTab.CurrentPath;
        e.DragEffects = FileDragDropService.ResolveEffect(sourcePaths, destDir, e.KeyModifiers);
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var targetFolder = _hoveredDragTarget;
        ClearDragOverHighlight();

        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;

        var sourcePaths = FileDragDropService.ExtractPaths(e.Data);
        if (sourcePaths.Count == 0) return;

        string destDir = targetFolder?.FullPath ?? vm.SelectedTab.CurrentPath;
        var effect = FileDragDropService.ResolveEffect(sourcePaths, destDir, e.KeyModifiers);
        if (effect == DragDropEffects.None) return;

        bool isMove = effect.HasFlag(DragDropEffects.Move);
        e.Handled = true;

        await vm.ExecuteDropAsync(sourcePaths, destDir, isMove);
    }

    #endregion
}
