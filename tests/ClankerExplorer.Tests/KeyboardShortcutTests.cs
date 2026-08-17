using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class KeyboardShortcutTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _sourceDir;
    private readonly string _destDir;

    public KeyboardShortcutTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "CE_KeyShortcutTests_" + Guid.NewGuid().ToString("N"));
        _sourceDir = Path.Combine(_testRoot, "Source");
        _destDir = Path.Combine(_testRoot, "Dest");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public void IsTextEditingControl_DetectsTextBoxAndRenameControls()
    {
        var tb = new TextBox();
        Assert.True(KeyboardShortcutHandler.IsTextEditingControl(tb));

        var acb = new AutoCompleteBox();
        Assert.True(KeyboardShortcutHandler.IsTextEditingControl(acb));

        var borderWithRename = new Border();
        borderWithRename.Classes.Add("rename-input");
        Assert.True(KeyboardShortcutHandler.IsTextEditingControl(borderWithRename));

        var normalGrid = new Grid();
        Assert.False(KeyboardShortcutHandler.IsTextEditingControl(normalGrid));
    }

    [Fact]
    public void HandlePaneKeyDown_InsideTextBox_PassesTextShortcutsThrough()
    {
        var pane = new ExplorerPaneViewModel(_sourceDir);
        var tb = new TextBox();

        var ctrlC = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.C,
            KeyModifiers = KeyModifiers.Control,
            Source = tb
        };

        bool handled = KeyboardShortcutHandler.HandlePaneKeyDown(pane, ctrlC, tb);
        Assert.False(handled);
        Assert.False(ctrlC.Handled);
    }

    [Fact]
    public async Task HandlePaneKeyDown_CtrlC_And_CtrlV_CopiesSingleFile()
    {
        var filePath = Path.Combine(_sourceDir, "document.txt");
        File.WriteAllText(filePath, "Hello Clanker");

        var pane1 = new ExplorerPaneViewModel(_sourceDir);
        pane1.SelectedTab!.NavigateTo(_sourceDir);
        await pane1.SelectedTab.RefreshAsync();

        var item = pane1.SelectedTab.FilteredItems.First(f => f.Name == "document.txt");
        pane1.SelectedTab.SelectedItem = item;
        pane1.SelectedTab.SelectedItems.Add(item);

        var ctrlC = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.C,
            KeyModifiers = KeyModifiers.Control
        };

        bool copyHandled = KeyboardShortcutHandler.HandlePaneKeyDown(pane1, ctrlC);
        Assert.True(copyHandled);
        Assert.True(ctrlC.Handled);
        Assert.True(ClipboardFileService.CanPaste);
        Assert.Contains(filePath, ClipboardFileService.StoredPaths);

        var pane2 = new ExplorerPaneViewModel(_destDir);
        pane2.SelectedTab!.NavigateTo(_destDir);
        await pane2.SelectedTab.RefreshAsync();

        var ctrlV = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.V,
            KeyModifiers = KeyModifiers.Control
        };

        bool pasteHandled = KeyboardShortcutHandler.HandlePaneKeyDown(pane2, ctrlV);
        Assert.True(pasteHandled);
        Assert.True(ctrlV.Handled);

        // Wait briefly for asynchronous paste transfer to complete
        var pastedPath = Path.Combine(_destDir, "document.txt");
        for (int i = 0; i < 50 && !File.Exists(pastedPath); i++)
        {
            await Task.Delay(20);
        }
        Assert.True(File.Exists(pastedPath));
        Assert.True(File.Exists(filePath)); // Original still exists
    }

    [Fact]
    public async Task HandlePaneKeyDown_CtrlC_CopiesMultipleSelectedFiles()
    {
        var file1 = Path.Combine(_sourceDir, "file1.txt");
        var file2 = Path.Combine(_sourceDir, "file2.txt");
        File.WriteAllText(file1, "1");
        File.WriteAllText(file2, "2");

        var pane = new ExplorerPaneViewModel(_sourceDir);
        pane.SelectedTab!.NavigateTo(_sourceDir);
        await pane.SelectedTab.RefreshAsync();

        pane.SelectedTab.SelectedItems.Clear();
        foreach (var itm in pane.SelectedTab.FilteredItems)
        {
            pane.SelectedTab.SelectedItems.Add(itm);
        }

        var ctrlC = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.C,
            KeyModifiers = KeyModifiers.Control
        };

        KeyboardShortcutHandler.HandlePaneKeyDown(pane, ctrlC);
        Assert.Equal(2, ClipboardFileService.StoredPaths.Count);
        Assert.Contains(file1, ClipboardFileService.StoredPaths);
        Assert.Contains(file2, ClipboardFileService.StoredPaths);
    }

    [Fact]
    public async Task HandlePaneKeyDown_CtrlX_And_CtrlV_MovesFilesCorrectly()
    {
        var fileToMove = Path.Combine(_sourceDir, "move_me.txt");
        File.WriteAllText(fileToMove, "Move me");

        var pane1 = new ExplorerPaneViewModel(_sourceDir);
        pane1.SelectedTab!.NavigateTo(_sourceDir);
        await pane1.SelectedTab.RefreshAsync();

        var item = pane1.SelectedTab.FilteredItems.First(f => f.Name == "move_me.txt");
        pane1.SelectedTab.SelectedItem = item;
        pane1.SelectedTab.SelectedItems.Add(item);

        var ctrlX = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.X,
            KeyModifiers = KeyModifiers.Control
        };

        bool cutHandled = KeyboardShortcutHandler.HandlePaneKeyDown(pane1, ctrlX);
        Assert.True(cutHandled);
        Assert.True(ctrlX.Handled);
        Assert.True(ClipboardFileService.IsCutMode);

        var pane2 = new ExplorerPaneViewModel(_destDir);
        pane2.SelectedTab!.NavigateTo(_destDir);
        await pane2.SelectedTab.RefreshAsync();

        var ctrlV = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.V,
            KeyModifiers = KeyModifiers.Control
        };

        KeyboardShortcutHandler.HandlePaneKeyDown(pane2, ctrlV);

        var destPath = Path.Combine(_destDir, "move_me.txt");
        for (int i = 0; i < 50 && (!File.Exists(destPath) || ClipboardFileService.IsCutMode); i++)
        {
            await Task.Delay(20);
        }
        Assert.True(File.Exists(destPath));
        Assert.False(File.Exists(fileToMove)); // Original moved
        Assert.False(ClipboardFileService.IsCutMode);
    }

    [Fact]
    public async Task HandlePaneKeyDown_CtrlA_SelectsAllInDetailsAndThumbnailViews()
    {
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_sourceDir, $"item{i}.txt"), $"Content {i}");
        }

        var pane = new ExplorerPaneViewModel(_sourceDir);
        pane.SelectedTab!.NavigateTo(_sourceDir);
        await pane.SelectedTab.RefreshAsync();

        // Details View
        pane.SetDetailsView();
        var ctrlA = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.A,
            KeyModifiers = KeyModifiers.Control
        };

        bool handledDetails = KeyboardShortcutHandler.HandlePaneKeyDown(pane, ctrlA);
        Assert.True(handledDetails);
        Assert.Equal(5, pane.SelectedTab.SelectedItems.Count);

        // Thumbnail View
        pane.SetThumbnailView();
        pane.SelectedTab.ClearThumbnailSelection();
        Assert.Empty(pane.SelectedTab.SelectedItems);

        var ctrlAThumb = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.A,
            KeyModifiers = KeyModifiers.Control
        };

        bool handledThumb = KeyboardShortcutHandler.HandlePaneKeyDown(pane, ctrlAThumb);
        Assert.True(handledThumb);
        Assert.Equal(5, pane.SelectedTab.SelectedItems.Count);
        Assert.All(pane.SelectedTab.FilteredItems, itm => Assert.True(itm.IsThumbnailSelected));
    }

    [Fact]
    public async Task HandlePaneKeyDown_F2_TriggersRenameOnlyOnSingleSelection()
    {
        var f1 = Path.Combine(_sourceDir, "single.txt");
        var f2 = Path.Combine(_sourceDir, "second.txt");
        File.WriteAllText(f1, "1");
        File.WriteAllText(f2, "2");

        var pane = new ExplorerPaneViewModel(_sourceDir);
        pane.SelectedTab!.NavigateTo(_sourceDir);
        await pane.SelectedTab.RefreshAsync();

        // Single selection
        var item1 = pane.SelectedTab.FilteredItems.First(f => f.Name == "single.txt");
        pane.SelectedTab.SelectedItem = item1;
        pane.SelectedTab.SelectedItems.Clear();
        pane.SelectedTab.SelectedItems.Add(item1);

        var f2Key = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.F2
        };

        bool handledSingle = KeyboardShortcutHandler.HandlePaneKeyDown(pane, f2Key);
        Assert.True(handledSingle);
        Assert.True(item1.IsRenaming);

        // Multi selection -> F2 must NOT start rename
        item1.IsRenaming = false;
        var item2 = pane.SelectedTab.FilteredItems.First(f => f.Name == "second.txt");
        pane.SelectedTab.SelectedItems.Add(item2);

        var f2Multi = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.F2
        };

        bool handledMulti = KeyboardShortcutHandler.HandlePaneKeyDown(pane, f2Multi);
        Assert.False(handledMulti);
        Assert.False(item1.IsRenaming);
        Assert.False(item2.IsRenaming);
    }

    [Fact]
    public async Task HandlePaneKeyDown_NavigationShortcuts_GoBackAndForward()
    {
        var subDir = Path.Combine(_sourceDir, "SubFolder");
        Directory.CreateDirectory(subDir);

        var pane = new ExplorerPaneViewModel(_sourceDir);
        pane.SelectedTab!.NavigateTo(_sourceDir);
        await pane.SelectedTab.RefreshAsync();
        pane.SelectedTab.NavigateTo(subDir);
        await pane.SelectedTab.RefreshAsync();

        Assert.True(pane.SelectedTab.CanGoBack);
        Assert.Equal(subDir, pane.SelectedTab.CurrentPath);

        // Backspace
        var backKey = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Back
        };

        bool handledBack = KeyboardShortcutHandler.HandlePaneKeyDown(pane, backKey);
        Assert.True(handledBack);
        Assert.Equal(_sourceDir, pane.SelectedTab.CurrentPath);
        Assert.True(pane.SelectedTab.CanGoForward);

        // Alt + Right
        var forwardKey = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Right,
            KeyModifiers = KeyModifiers.Alt
        };

        bool handledForward = KeyboardShortcutHandler.HandlePaneKeyDown(pane, forwardKey);
        Assert.True(handledForward);
        Assert.Equal(subDir, pane.SelectedTab.CurrentPath);
    }

    [Fact]
    public async Task HandlePaneKeyDown_Delete_TriggersDeletion()
    {
        var fileToDelete = Path.Combine(_sourceDir, "delete_me.txt");
        File.WriteAllText(fileToDelete, "Bye");

        var pane = new ExplorerPaneViewModel(_sourceDir);
        pane.SelectedTab!.NavigateTo(_sourceDir);
        await pane.SelectedTab.RefreshAsync();

        var item = pane.SelectedTab.FilteredItems.First(f => f.Name == "delete_me.txt");
        pane.SelectedTab.SelectedItem = item;
        pane.SelectedTab.SelectedItems.Add(item);

        bool deleteRequested = false;
        pane.RequestDeleteWithConfirmation += (itm, perm) =>
        {
            deleteRequested = true;
            Assert.Equal(item, itm);
            Assert.False(perm);
        };

        var delKey = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Delete
        };

        bool handled = KeyboardShortcutHandler.HandlePaneKeyDown(pane, delKey);
        Assert.True(handled);
        Assert.True(delKey.Handled);
        Assert.True(deleteRequested);
    }

    [Fact]
    public void HandleWindowKeyDown_WindowShortcuts_ToggleState()
    {
        var mainVm = new MainViewModel(loadSidebarData: false);

        // F3 toggles inspector
        bool initialInspector = mainVm.ShowInspector;
        var f3Key = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.F3
        };

        bool handledF3 = KeyboardShortcutHandler.HandleWindowKeyDown(mainVm, f3Key);
        Assert.True(handledF3);
        Assert.Equal(!initialInspector, mainVm.ShowInspector);

        // Ctrl + Shift + D toggles dual pane
        bool initialDual = mainVm.IsDualPane;
        var ctrlShiftD = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift
        };

        bool handledDual = KeyboardShortcutHandler.HandleWindowKeyDown(mainVm, ctrlShiftD);
        Assert.True(handledDual);
        Assert.Equal(!initialDual, mainVm.IsDualPane);

        // Ctrl + T adds a new tab in active pane
        int tabCountBefore = mainVm.ActivePane.Tabs.Count;
        var ctrlT = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.T,
            KeyModifiers = KeyModifiers.Control
        };

        bool handledT = KeyboardShortcutHandler.HandleWindowKeyDown(mainVm, ctrlT);
        Assert.True(handledT);
        Assert.Equal(tabCountBefore + 1, mainVm.ActivePane.Tabs.Count);
    }
}
