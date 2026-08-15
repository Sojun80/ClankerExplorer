using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class ExplorerPaneView : UserControl
{
    public ExplorerPaneView()
    {
        InitializeComponent();

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
    private IInputElement? _capturedTabBorder;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is IInputElement inputElem && sender is Visual visual && visual.DataContext is ExplorerTabViewModel tab && DataContext is ExplorerPaneViewModel vm)
        {
            if (e.GetCurrentPoint(visual).Properties.IsLeftButtonPressed)
            {
                _pressedTab = tab;
                _tabPressStartPoint = e.GetPosition(this);
                _isTabDragging = false;
                _capturedTabBorder = inputElem;
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

            if (!_isTabDragging && (Math.Abs(delta.X) > 6 || Math.Abs(delta.Y) > 6))
            {
                _isTabDragging = true;
                if (_capturedTabBorder != null)
                {
                    e.Pointer.Capture(_capturedTabBorder);
                }
                TabDragCoordinator.Instance.StartDrag(_pressedTab, vm, e.KeyModifiers.HasFlag(KeyModifiers.Control));
            }

            if (_isTabDragging)
            {
                // Hit test across top level window to find target tab and pane
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    var windowPos = e.GetPosition(topLevel);
                    var hitVisual = topLevel.InputHitTest(windowPos) as Visual;

                    ExplorerPaneViewModel? targetPane = null;
                    ExplorerTabViewModel? targetTab = null;
                    bool isLeftHalf = false;

                    while (hitVisual != null)
                    {
                        if (targetTab == null && hitVisual.DataContext is ExplorerTabViewModel tvm)
                        {
                            targetTab = tvm;
                            var tabPos = e.GetPosition(hitVisual);
                            isLeftHalf = tabPos.X < hitVisual.Bounds.Width / 2;
                        }

                        if (targetPane == null && hitVisual is ExplorerPaneView paneView && paneView.DataContext is ExplorerPaneViewModel pvm)
                        {
                            targetPane = pvm;
                        }

                        hitVisual = hitVisual.GetVisualParent();
                    }

                    TabDragCoordinator.Instance.UpdateDrag(targetPane ?? vm, targetTab, isLeftHalf, e.KeyModifiers.HasFlag(KeyModifiers.Control));
                }
            }
        }
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isTabDragging && _pressedTab != null)
        {
            var targetPane = TabDragCoordinator.Instance.CurrentTargetPane ?? DataContext as ExplorerPaneViewModel;
            var targetTab = TabDragCoordinator.Instance.CurrentHoveredTab;
            bool isLeft = TabDragCoordinator.Instance.IsLeftDropHalf;
            bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            int targetIndex = -1;
            if (targetPane != null && targetTab != null)
            {
                int idx = targetPane.Tabs.IndexOf(targetTab);
                if (idx >= 0)
                {
                    targetIndex = isLeft ? idx : idx + 1;
                }
            }

            TabDragCoordinator.Instance.CompleteDrop(targetPane, targetIndex, isCtrl);
            e.Pointer.Capture(null);
        }

        _pressedTab = null;
        _isTabDragging = false;
        _capturedTabBorder = null;
    }

    private void OnTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_isTabDragging)
        {
            TabDragCoordinator.Instance.CancelDrag();
        }
        _pressedTab = null;
        _isTabDragging = false;
        _capturedTabBorder = null;
    }

    private void OnFolderBackgroundStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm && vm.SelectedTab != null)
        {
            // Left click on background strip deselects file row without changing directory
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                vm.SelectedTab.SelectedItem = null;
                vm.NotifyContextMenuProperties();
            }
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
