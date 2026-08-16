using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
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
                FileDataGrid.AddHandler(PointerReleasedEvent, (sender, args) =>
                {
                    SaveCurrentColumnLayout();
                    CaptureFolderViewportAnchors();
                }, RoutingStrategies.Bubble);
                FileDataGrid.PointerWheelChanged += (_, _) =>
                    Dispatcher.UIThread.Post(CaptureFolderViewportAnchors, DispatcherPriority.Background);
                FileDataGrid.KeyUp += (_, _) =>
                    Dispatcher.UIThread.Post(CaptureFolderViewportAnchors, DispatcherPriority.Background);
                FileDataGrid.ColumnReordered += (sender, args) => SaveCurrentColumnLayout();
                FileDataGrid.Sorting += OnDataGridSorting;
            }

            if (FileGridContainer != null)
            {
                FileGridContainer.AddHandler(PointerPressedEvent, OnFileGridPointerPressedTunnel, RoutingStrategies.Tunnel);
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

            Dispatcher.UIThread.Post(UpdateTabScrollButtonsVisibility, DispatcherPriority.Loaded);
        };

        Unloaded += (_, _) =>
        {
            _thumbnailDebounceTimer.Stop();
            _folderScrollSaveTimer.Stop();
            _thumbnailViewportCts?.Cancel();
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
                    or nameof(ExplorerPaneViewModel.ThumbnailSize))
                {
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
                RestoreViewportAnchors(vm);
                if (_detailsScrollViewer != null)
                    _detailsScrollViewer.Offset = new Vector(vm.DetailsHorizontalOffset, vm.DetailsVerticalOffset);
                if (_thumbnailScrollViewer != null)
                    _thumbnailScrollViewer.Offset = new Vector(0, vm.ThumbnailVerticalOffset);
            }
            finally
            {
                _restoringFolderViewState = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private void RestoreViewportAnchors(ExplorerPaneViewModel vm)
    {
        var tab = vm.SelectedTab;
        if (tab == null) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.IsNullOrWhiteSpace(vm.DetailsTopItemPath))
        {
            var item = tab.FilteredItems.FirstOrDefault(candidate =>
                string.Equals(candidate.FullPath, vm.DetailsTopItemPath, comparison));
            if (item != null) FileDataGrid?.ScrollIntoView(item, null);
        }

        if (!string.IsNullOrWhiteSpace(vm.ThumbnailTopItemPath) && ThumbnailListBox != null)
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
            int rowIndex = index < 0 ? -1 : index / Math.Max(1, vm.ThumbnailColumnCount);
            if (rowIndex >= 0 && rowIndex < vm.ThumbnailRows.Count)
                ThumbnailListBox.ScrollIntoView(vm.ThumbnailRows[rowIndex]);
        }
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

    private void ScheduleThumbnailViewportUpdate()
    {
        _thumbnailViewportCts?.Cancel();
        if (DataContext is not ExplorerPaneViewModel vm || !vm.IsThumbnailView) return;

        int delay = Math.Clamp(SettingsService.Instance.CurrentSettings.ThumbnailScrollDebounceMilliseconds, 50, 150);
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
            if (!item.IsThumbnailSelected)
            {
                tab.SelectThumbnailItem(item, control: false, shift: false);
            }
        }
        else
        {
            tab.SelectThumbnailItem(item, ctrl, shift);
        }

        vm.NotifyContextMenuProperties();
    }

    private void OnThumbnailItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FileItem item } && DataContext is ExplorerPaneViewModel vm)
        {
            vm.OpenItem(item);
            e.Handled = true;
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
        if (e.GetCurrentPoint(FileDataGrid).Properties.IsRightButtonPressed)
        {
            var source = e.Source as Visual;
            while (source != null && source is not DataGridRow && source != FileDataGrid)
            {
                source = source.GetVisualParent();
            }

            if (source is DataGridRow row && row.DataContext is FileItem item && DataContext is ExplorerPaneViewModel vm)
            {
                if (vm.SelectedTab != null)
                {
                    vm.SelectedTab.SelectedItem = item;
                    vm.NotifyContextMenuProperties();
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
            bool canScroll = TabsScrollViewer.Extent.Width > TabsScrollViewer.Viewport.Width + 4;
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
            // Left click on background strip begins marquee or deselects file row
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && FileDataGrid != null)
                {
                    if (vm.IsThumbnailView) vm.SelectedTab.ClearThumbnailSelection();
                    else
                    {
                        FileDataGrid.SelectedItems.Clear();
                        vm.SelectedTab.SelectedItem = null;
                    }
                    vm.NotifyContextMenuProperties();
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
        }
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
                while (source != null && source != FileGridContainer)
                {
                    if (source is Border border && border.Classes.Contains("thumbnail-card"))
                    {
                        isThumbnailCard = true;
                        break;
                    }
                    source = source.GetVisualParent();
                }

                if (!isThumbnailCard && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    vm.SelectedTab?.ClearThumbnailSelection();
                    vm.NotifyContextMenuProperties();
                }
                return;
            }

            bool isRowOrCell = false;
            while (source != null && source != FileGridContainer)
            {
                if (source is DataGridRow || source is DataGridCell)
                {
                    isRowOrCell = true;
                    break;
                }
                source = source.GetVisualParent();
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

        if (!_isMouseDownForMarquee || FileGridContainer == null || FileDataGrid == null || DataContext is not ExplorerPaneViewModel vm) return;

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
        if (_isMiddleAutoScrolling)
        {
            var props = e.GetCurrentPoint(FileGridContainer).Properties;
            // If the middle button was released after moving, stop autoscroll (hold-to-scroll gesture)
            if (!props.IsMiddleButtonPressed && _hasMovedDuringMiddleScroll)
            {
                StopMiddleAutoScroll();
                e.Pointer.Capture(null);
                e.Handled = true;
                return;
            }
            return;
        }

        _autoScrollTimer.Stop();
        _autoScrollVelocity = 0;
        if (MarqueeBox != null) MarqueeBox.IsVisible = false;

        if (DataContext is ExplorerPaneViewModel vm)
        {
            vm.IsSuppressingPreview = false;
        }

        if (_isMarqueeActive)
        {
            e.Pointer.Capture(null);
            _isMarqueeActive = false;
            if (DataContext is ExplorerPaneViewModel vmPane)
            {
                vmPane.TriggerPreviewForSelectedItem();
            }
        }
        else if (_isMouseDownForMarquee)
        {
            // Click on blank background space without dragging
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && FileDataGrid != null)
            {
                FileDataGrid.SelectedItems.Clear();
                if (DataContext is ExplorerPaneViewModel vmEmpty && vmEmpty.SelectedTab != null)
                {
                    vmEmpty.SelectedTab.SelectedItem = null;
                    vmEmpty.NotifyContextMenuProperties();
                    vmEmpty.TriggerPreviewForSelectedItem();
                }
            }
        }

        _isMouseDownForMarquee = false;
    }

    private void OnFileGridPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
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
        if (FileDataGrid == null || FileGridContainer == null || DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
        var items = vm.SelectedTab.FilteredItems;
        if (items.Count == 0) return;

        double minY = Math.Min(_marqueeStartPos.Y, _lastMarqueePos.Y);
        double maxY = Math.Max(_marqueeStartPos.Y, _lastMarqueePos.Y);

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
        if (_isMarqueeActive && _autoScrollVelocity != 0 && FileDataGrid != null)
        {
            var sv = FileDataGrid.FindDescendantOfType<ScrollViewer>();
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

    private void OnThumbnailKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;

        // Enter: Open
        if (e.Key == Key.Enter)
        {
            if (vm.SelectedTab.SelectedItem != null)
            {
                vm.OpenItem(vm.SelectedTab.SelectedItem);
                e.Handled = true;
            }
        }
        // F2: Rename
        else if (e.Key == Key.F2)
        {
            if (vm.SelectedTab.SelectedItem != null)
            {
                vm.TriggerRename();
                e.Handled = true;
            }
        }
        // Delete / Shift+Delete
        else if (e.Key == Key.Delete)
        {
            bool isPermanent = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            vm.DeleteSelected(isPermanent);
            e.Handled = true;
        }
        // Ctrl+C: Copy
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C)
        {
            vm.CopyFiles();
            e.Handled = true;
        }
        // Ctrl+X: Cut
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.X)
        {
            vm.CutFiles();
            e.Handled = true;
        }
        // Ctrl+V: Paste
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V)
        {
            _ = vm.PasteFilesAsync();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A)
        {
            vm.SelectedTab.SelectAllThumbnails();
            vm.NotifyContextMenuProperties();
            e.Handled = true;
        }
        // F5: Refresh
        else if (e.Key == Key.F5)
        {
            vm.Refresh();
            e.Handled = true;
        }
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm && vm.SelectedTab?.SelectedItem != null)
        {
            vm.OpenItem(vm.SelectedTab.SelectedItem);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm)
        {
            vm.NotifyContextMenuProperties();
        }
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
}
