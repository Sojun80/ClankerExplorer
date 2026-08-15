using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private bool _isMouseDownForMarquee;
    private bool _isMarqueeActive;
    private Point _marqueeStartPos;
    private Point _lastMarqueePos;
    private HashSet<FileItem> _marqueeBaseSelection = new();
    private double _autoScrollVelocity;

    public ExplorerPaneView()
    {
        InitializeComponent();

        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScrollTimer.Tick += OnAutoScrollTick;

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
            }

            if (FileDataGrid != null)
            {
                FileDataGrid.AddHandler(PointerPressedEvent, OnDataGridPointerPressedTunnel, RoutingStrategies.Tunnel);
                FileDataGrid.AddHandler(PointerReleasedEvent, (sender, args) => SaveCurrentColumnLayout(), RoutingStrategies.Bubble);
                FileDataGrid.ColumnReordered += (sender, args) => SaveCurrentColumnLayout();
            }

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
        };
    }

    private void SaveCurrentColumnLayout()
    {
        if (FileDataGrid == null || DataContext is not ExplorerPaneViewModel vm) return;

        var s = SettingsService.Instance.CurrentSettings;
        bool changed = false;

        foreach (var col in FileDataGrid.Columns)
        {
            var header = col.Header?.ToString();
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
    }

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
        if (DataContext is ExplorerPaneViewModel vm)
        {
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.XButton1Released)
            {
                vm.GoBack();
                e.Handled = true;
            }
            else if (kind == PointerUpdateKind.XButton2Released)
            {
                vm.GoForward();
                e.Handled = true;
            }
        }
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

    private void OnFolderBackgroundStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm && vm.SelectedTab != null)
        {
            // Left click on background strip begins marquee or deselects file row
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && FileDataGrid != null)
                {
                    FileDataGrid.SelectedItems.Clear();
                    vm.SelectedTab.SelectedItem = null;
                    vm.NotifyContextMenuProperties();
                }

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
        if (FileGridContainer == null || FileDataGrid == null || DataContext is not ExplorerPaneViewModel vm) return;

        var props = e.GetCurrentPoint(FileGridContainer).Properties;
        if (props.IsLeftButtonPressed)
        {
            var source = e.Source as Visual;
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
        if (!_isMouseDownForMarquee || FileGridContainer == null || FileDataGrid == null || DataContext is not ExplorerPaneViewModel vm) return;

        var cur = e.GetPosition(FileGridContainer);
        var delta = cur - _marqueeStartPos;

        if (!_isMarqueeActive && PointerGestureClassifier.ExceedsDragThreshold(delta.X, delta.Y, 4))
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
                return new { Row = r, Top = pt?.Y ?? -1, Height = r.Bounds.Height, Item = (FileItem)r.DataContext };
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
