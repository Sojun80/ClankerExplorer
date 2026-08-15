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

    private void OnTabClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is ExplorerTabViewModel tab && DataContext is ExplorerPaneViewModel vm)
        {
            vm.SelectedTab = tab;
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
