using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

// Background drag selection and edge autoscroll.
public partial class ExplorerPaneView
{
    private readonly DispatcherTimer _autoScrollTimer;
    private bool _isMouseDownForMarquee;
    private bool _isMarqueeActive;
    private Point _marqueeStartPos;
    private Point _lastMarqueePos;
    private HashSet<FileItem> _marqueeBaseSelection = new();
    private double _autoScrollVelocity;

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

    private void OnFileGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsFromActiveRenameTextBox(e)) return;
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
}
