using System;
using System.Diagnostics;
using System.IO;
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

// Navigation, keyboard shortcuts, inline renaming, opening, and clipboard actions.
public partial class ExplorerPaneView
{
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

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ExplorerPaneViewModel vm)
        {
            vm.SubmitAddressCommand.Execute(null);
        }
    }

    private static bool IsFromActiveRenameTextBox(RoutedEventArgs? e)
    {
        if (e?.Source is not Visual visual) return false;
        var current = visual;
        while (current != null)
        {
            if (current is TextBox tb && tb.DataContext is FileItem { IsRenaming: true })
            {
                return true;
            }
            current = current.GetVisualParent();
        }
        return false;
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

    private async void OnThumbnailItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsFromActiveRenameTextBox(e))
        {
            e.Handled = true;
            return;
        }

        if (sender is Control { DataContext: FileItem item } && DataContext is ExplorerPaneViewModel vm)
        {
            e.Handled = true;
            try
            {
                await vm.OpenItem(item);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Open item failed: {ex}");
                if (vm.SelectedTab != null)
                {
                    vm.SelectedTab.StatusMessage = "Unable to open item.";
                }
            }
        }
    }

    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsFromActiveRenameTextBox(e))
        {
            e.Handled = true;
            return;
        }

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
            try
            {
                await vm.OpenItem(vm.SelectedTab.SelectedItem);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Open item failed: {ex}");
                if (vm.SelectedTab != null)
                {
                    vm.SelectedTab.StatusMessage = "Unable to open item.";
                }
            }
        }
    }

    private async void OnCopyPathClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm && vm.SelectedTab != null)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                {
                    var path = vm.SelectedTab.SelectedItem?.FullPath ?? vm.SelectedTab.CurrentPath;
                    await topLevel.Clipboard.SetTextAsync(path);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Copy path failed: {ex}");
                if (vm.SelectedTab != null)
                {
                    vm.SelectedTab.StatusMessage = "Unable to copy path.";
                }
            }
        }
    }

    private void BindClipboardRequests(ExplorerPaneViewModel vm)
    {
        vm.RequestSetClipboardText += async text =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null && !string.IsNullOrEmpty(text))
                {
                    await topLevel.Clipboard.SetTextAsync(text);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetClipboardText failed: {ex}");
                if (vm.SelectedTab != null)
                {
                    vm.SelectedTab.StatusMessage = "Unable to access clipboard.";
                }
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
    }

    private void InitializeContextMenus()
    {
        if (GridContextMenu != null)
        {
            GridContextMenu.Opening += (_, _) => RefreshContextMenu();
        }

        if (ThumbnailContextMenu != null)
        {
            ThumbnailContextMenu.Opening += (_, _) => RefreshContextMenu();
        }
    }

    private void RefreshContextMenu()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        _ = ClipboardFileService.UpdateFromSystemClipboardAsync(topLevel?.Clipboard);
        if (DataContext is ExplorerPaneViewModel vm)
        {
            vm.NotifyContextMenuProperties();
        }
    }
}
