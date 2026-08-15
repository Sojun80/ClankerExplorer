using System;
using Avalonia.Controls;
using Avalonia.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Loaded += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.RequestCreateItem += async (type, parent) =>
                {
                    var dlg = new NewItemWindow(type, parent);
                    var res = await dlg.ShowDialog<bool>(this);
                    if (res)
                    {
                        if (type == "folder")
                        {
                            FileSystemService.Instance.CreateFolder(parent, dlg.ItemName);
                        }
                        else
                        {
                            FileSystemService.Instance.CreateFile(parent, dlg.ItemName);
                        }
                        vm.ActivePane.Refresh();
                    }
                };

                vm.RequestOpenNetworkShare += async () =>
                {
                    var dlg = new NetworkShareWindow();
                    var res = await dlg.ShowDialog<bool>(this);
                    if (res && !string.IsNullOrWhiteSpace(dlg.NetworkPath))
                    {
                        var path = dlg.NetworkPath.Trim();
                        var serverName = path.TrimStart('\\').Split('\\')[0];
                        if (!string.IsNullOrEmpty(serverName))
                        {
                            vm.AddDiscoveredServer(serverName);
                        }
                        vm.NavigateSidebar(path);
                    }
                };

                vm.RequestOpenSettings += async () =>
                {
                    var dlg = new SettingsWindow();
                    await dlg.ShowDialog(this);
                    vm.RefreshAll();
                };

                vm.RequestRename += async item =>
                {
                    if (item == null) return;
                    var dlg = new RenameWindow(item.Name);
                    var res = await dlg.ShowDialog<bool>(this);
                    if (res && !string.IsNullOrWhiteSpace(dlg.NewName) && dlg.NewName != item.Name)
                    {
                        FileSystemService.Instance.Rename(item.FullPath, dlg.NewName);
                        vm.ActivePane.Refresh();
                    }
                };

                vm.RequestProperties += async item =>
                {
                    var target = item ?? new FileItem
                    {
                        FullPath = vm.ActivePane.SelectedTab?.CurrentPath ?? @"C:\",
                        Name = System.IO.Path.GetFileName(vm.ActivePane.SelectedTab?.CurrentPath ?? @"C:\"),
                        IsDirectory = true
                    };
                    var dlg = new PropertiesWindow(target);
                    await dlg.ShowDialog(this);
                };

                vm.RequestDeleteWithConfirmation += async (item, perm) =>
                {
                    if (item == null) return;
                    var dlg = new ConfirmDeleteWindow(item.Name, item.FullPath);
                    var res = await dlg.ShowDialog<bool>(this);
                    if (res)
                    {
                        FileSystemService.Instance.Delete(new[] { item.FullPath }, perm);
                        vm.ActivePane.Refresh();
                    }
                };
            }
        };

        // Hotkey bindings & Mouse navigation
        KeyDown += (s, e) =>
        {
            if (DataContext is not MainViewModel vm) return;

            // Delete Key: Confirm Delete
            if (e.Key == Key.Delete)
            {
                if (e.Source is not TextBox)
                {
                    vm.ActivePane.DeleteSelected();
                    e.Handled = true;
                    return;
                }
            }

            // F3: Toggle Inspector
            if (e.Key == Key.F3)
            {
                vm.ToggleInspector();
                e.Handled = true;
            }
            // Ctrl+Shift+D: Toggle Dual Pane
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.D)
            {
                vm.ToggleDualPane();
                e.Handled = true;
            }
            // Ctrl+T: New Tab
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.T)
            {
                vm.ActivePane.AddNewTab();
                e.Handled = true;
            }
            // Ctrl+W: Close Tab
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.W)
            {
                vm.ActivePane.CloseTab(null);
                e.Handled = true;
            }
            // Alt+Left / Backspace: Go Back
            else if ((e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Left) || e.Key == Key.Back)
            {
                if (e.Source is not TextBox)
                {
                    vm.ActivePane.GoBack();
                    e.Handled = true;
                }
            }
            // Alt+Right: Go Forward
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Right)
            {
                if (e.Source is not TextBox)
                {
                    vm.ActivePane.GoForward();
                    e.Handled = true;
                }
            }
            // F5: Refresh
            else if (e.Key == Key.F5)
            {
                vm.RefreshAll();
                e.Handled = true;
            }
        };
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsXButton1Pressed)
            {
                vm.ActivePane.GoBack();
                e.Handled = true;
            }
            else if (props.IsXButton2Pressed)
            {
                vm.ActivePane.GoForward();
                e.Handled = true;
            }
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnToggleNetworkPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ToggleNetworkSectionCommand.Execute(null);
        }
    }

    private void OnDriveClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is DriveModel drive && DataContext is MainViewModel vm)
        {
            vm.NavigateSidebar(drive.RootPath);
        }
    }

    private void OnNetworkNodeClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is NetworkNode node && DataContext is MainViewModel vm)
        {
            if (!string.IsNullOrWhiteSpace(node.UncPath))
            {
                vm.NavigateSidebar(node.UncPath);
            }
        }
    }

    private void OnQuickAccessClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is QuickAccessItem item && DataContext is MainViewModel vm)
        {
            vm.NavigateSidebar(item.Path);
        }
    }

    private void OnWslRootClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is WslDistroItem distro && DataContext is MainViewModel vm)
        {
            vm.NavigateSidebar(distro.RootPath);
        }
    }

    private void OnWslHomeClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is WslDistroItem distro && DataContext is MainViewModel vm)
        {
            vm.NavigateSidebar(distro.HomePath);
        }
    }

    private void OnFrequentFolderClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is FrequentFolderItem item && DataContext is MainViewModel vm)
        {
            vm.NavigateSidebar(item.Path);
        }
    }

    private void OnLeftPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SetActivePane("left");
        }
    }

    private void OnRightPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SetActivePane("right");
        }
    }
}
