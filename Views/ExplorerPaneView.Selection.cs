using System;
using System.Collections.Generic;
using System.Linq;
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

// Details and thumbnail selection, including shared pointer gesture routing.
public partial class ExplorerPaneView
{
    private FileItem? _pendingPlainClickItem;
    private bool _isApplyingDetailsSelection;

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

    private void OnThumbnailItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsFromActiveRenameTextBox(e)) return;

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

    private void OnDataGridPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (IsFromActiveRenameTextBox(e)) return;
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
        if (IsFromActiveRenameTextBox(e)) return;
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
        if (IsFromActiveRenameTextBox(e)) return;
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

    private void InitializeFileInputHandlers()
    {
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
    }
}
