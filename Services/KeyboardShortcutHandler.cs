using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Services;

/// <summary>
/// Centralized keyboard shortcut router implementing standard Windows Explorer
/// keyboard behaviors with focus-aware filtering for text controls.
/// </summary>
public static class KeyboardShortcutHandler
{
    /// <summary>
    /// Checks if the given visual event source or currently focused element is an active text-editing control.
    /// When inside a text editor / textbox / rename field, text editing shortcuts must pass through.
    /// </summary>
    public static bool IsTextEditingControl(object? source, Control? focusedControl = null)
    {
        if (source is TextBox or AutoCompleteBox) return true;
        if (focusedControl is TextBox or AutoCompleteBox) return true;

        if (source is Control c)
        {
            if (c.Classes.Contains("rename-input") || c.Classes.Contains("address-bar")) return true;
        }
        if (focusedControl != null)
        {
            if (focusedControl.Classes.Contains("rename-input") || focusedControl.Classes.Contains("address-bar")) return true;
        }

        return false;
    }

    /// <summary>
    /// Handles keyboard shortcuts routed to an Explorer pane (Details view, Thumbnail view, or Pane container).
    /// Returns true if the key event was recognized and handled as a file manager shortcut.
    /// </summary>
    public static bool HandlePaneKeyDown(ExplorerPaneViewModel? pane, KeyEventArgs e, Control? focusedControl = null)
    {
        if (pane == null || pane.SelectedTab == null) return false;
        var tab = pane.SelectedTab;

        bool isTextContext = IsTextEditingControl(e.Source, focusedControl);

        // When inside a text editing control (Address bar, Rename box, Search field):
        // Allow native text shortcuts (Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+A, Delete, Backspace, etc.) to pass through untouched.
        if (isTextContext)
        {
            // Escape inside text box: Cancel inline rename or blur
            if (e.Key == Key.Escape)
            {
                if (e.Source is TextBox tb && tb.Classes.Contains("rename-input"))
                {
                    pane.CancelRename();
                    e.Handled = true;
                    return true;
                }
                if (tab.IsFilterBarOpen)
                {
                    tab.IsFilterBarOpen = false;
                    e.Handled = true;
                    return true;
                }
            }
            return false;
        }

        // ==========================================
        // STANDARD FILE MANAGEMENT KEYBOARD SHORTCUTS
        // ==========================================

        var key = e.Key;
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        bool alt = mods.HasFlag(KeyModifiers.Alt);

        // 1. Ctrl + C: Copy selected file(s) / folder(s)
        if (ctrl && !shift && !alt && key == Key.C)
        {
            pane.CopyFiles();
            e.Handled = true;
            return true;
        }

        // 2. Ctrl + X: Cut selected file(s) / folder(s)
        if (ctrl && !shift && !alt && key == Key.X)
        {
            pane.CutFiles();
            e.Handled = true;
            return true;
        }

        // 3. Ctrl + V: Paste files from clipboard into the active directory
        if (ctrl && !shift && !alt && key == Key.V)
        {
            _ = pane.PasteFilesAsync();
            e.Handled = true;
            return true;
        }

        // 4. Ctrl + A: Select all items in the active view (Details or Thumbnail)
        if (ctrl && !shift && !alt && key == Key.A)
        {
            pane.SelectAll();
            e.Handled = true;
            return true;
        }

        // 5. Delete & Shift+Delete: Delete Selected
        if (!ctrl && !alt && key == Key.Delete)
        {
            pane.DeleteSelected(shift);
            e.Handled = true;
            return true;
        }

        // 6. F2: Rename selected item (single selection only)
        if (!ctrl && !shift && !alt && key == Key.F2)
        {
            if (tab.SelectedItem != null && tab.SelectedItems.Count <= 1)
            {
                pane.TriggerRename();
                e.Handled = true;
                return true;
            }
        }

        // 7. Enter: Open selected file or folder
        if (!ctrl && !shift && !alt && key == Key.Enter)
        {
            if (tab.SelectedItem != null)
            {
                pane.OpenSelected();
                e.Handled = true;
                return true;
            }
        }

        // 8. Backspace or Alt + Left: Navigate Back
        if ((!ctrl && !shift && !alt && key == Key.Back) ||
            (!ctrl && !shift && alt && key == Key.Left))
        {
            if (tab.CanGoBack)
            {
                pane.GoBack();
                e.Handled = true;
                return true;
            }
        }

        // 9. Alt + Right: Navigate Forward
        if (!ctrl && !shift && alt && key == Key.Right)
        {
            if (tab.CanGoForward)
            {
                pane.GoForward();
                e.Handled = true;
                return true;
            }
        }

        // 10. Alt + Up: Go to Parent Directory
        if (!ctrl && !shift && alt && key == Key.Up)
        {
            pane.GoUp();
            e.Handled = true;
            return true;
        }

        // 11. F5: Refresh active directory
        if (!ctrl && !shift && !alt && key == Key.F5)
        {
            pane.Refresh();
            e.Handled = true;
            return true;
        }

        // 12. Ctrl + F: Toggle / Focus Filter Bar
        if (ctrl && !shift && !alt && key == Key.F)
        {
            tab.IsFilterBarOpen = !tab.IsFilterBarOpen;
            e.Handled = true;
            return true;
        }

        // 13. Ctrl + Shift + N: New Folder
        if (ctrl && shift && !alt && key == Key.N)
        {
            pane.TriggerNewFolder();
            e.Handled = true;
            return true;
        }

        // 14. Ctrl + N: New File
        if (ctrl && !shift && !alt && key == Key.N)
        {
            pane.TriggerNewFile();
            e.Handled = true;
            return true;
        }

        // 15. Ctrl + Shift + C: Copy Full Path
        if (ctrl && shift && !alt && key == Key.C)
        {
            pane.CopyPath();
            e.Handled = true;
            return true;
        }

        // 16. Escape: Cancel rename, dismiss filter bar, or clear selection
        if (!ctrl && !shift && !alt && key == Key.Escape)
        {
            if (tab.IsFilterBarOpen)
            {
                tab.IsFilterBarOpen = false;
                e.Handled = true;
                return true;
            }
            if (tab.SelectedItem?.IsRenaming == true)
            {
                pane.CancelRename();
                e.Handled = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Handles application-level keyboard shortcuts for MainWindow.
    /// </summary>
    public static bool HandleWindowKeyDown(MainViewModel? mainVm, KeyEventArgs e, Control? focusedControl = null)
    {
        if (mainVm == null) return false;
        var activePane = mainVm.ActivePane;

        bool isTextContext = IsTextEditingControl(e.Source, focusedControl);
        if (isTextContext) return false;

        var key = e.Key;
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        bool alt = mods.HasFlag(KeyModifiers.Alt);

        // F3: Toggle Inspector
        if (!ctrl && !shift && !alt && key == Key.F3)
        {
            mainVm.ToggleInspector();
            e.Handled = true;
            return true;
        }

        // Ctrl + Shift + D: Toggle Dual Pane
        if (ctrl && shift && !alt && key == Key.D)
        {
            mainVm.ToggleDualPane();
            e.Handled = true;
            return true;
        }

        // Ctrl + T: New Tab in active pane
        if (ctrl && !shift && !alt && key == Key.T)
        {
            activePane?.AddNewTab();
            e.Handled = true;
            return true;
        }

        // Ctrl + W: Close Active Tab
        if (ctrl && !shift && !alt && key == Key.W)
        {
            activePane?.CloseTab(null);
            e.Handled = true;
            return true;
        }

        // If not a window-specific shortcut, dispatch to active pane
        return HandlePaneKeyDown(activePane, e, focusedControl);
    }
}
