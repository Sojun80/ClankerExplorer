using Avalonia.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Tests;

public sealed class DeleteCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextMenuDelete_WithNoParameter_RequestsRecycleBinConfirmation(bool isDirectory)
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        await pane.SelectedTab!.RefreshAsync();
        var selected = new FileItem
        {
            Name = isDirectory ? "folder" : "alpha.txt",
            FullPath = isDirectory ? fs.FolderB : Path.Combine(fs.FolderA, "alpha.txt"),
            IsDirectory = isDirectory
        };
        pane.SelectedTab!.SelectedItem = selected;
        FileItem? requestedItem = null;
        bool? requestedPermanent = null;
        pane.RequestDeleteWithConfirmation += (item, permanent) =>
        {
            requestedItem = item;
            requestedPermanent = permanent;
        };

        Assert.True(pane.DeleteSelectedCommand.CanExecute(null));
        pane.DeleteSelectedCommand.Execute(null);

        Assert.Same(selected, requestedItem);
        Assert.Equal(false, requestedPermanent);
        Assert.True(File.Exists(Path.Combine(fs.FolderA, "alpha.txt")));
        Assert.True(Directory.Exists(fs.FolderB));
    }

    [Fact]
    public async Task ContextMenuDelete_MultipleItems_RequestsOneConfirmationForEntireSelection()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        await pane.SelectedTab!.RefreshAsync();
        var first = new FileItem { Name = "alpha.txt", FullPath = Path.Combine(fs.FolderA, "alpha.txt") };
        var second = new FileItem { Name = "FolderB", FullPath = fs.FolderB, IsDirectory = true };
        pane.SelectedTab!.SelectedItems.Add(first);
        pane.SelectedTab.SelectedItems.Add(second);
        pane.SelectedTab.SelectedItem = first;
        int singleRequests = 0;
        int multipleRequests = 0;
        List<FileItem>? requestedItems = null;
        bool? requestedPermanent = null;
        pane.RequestDeleteWithConfirmation += (_, _) => singleRequests++;
        pane.RequestDeleteMultipleWithConfirmation += (items, permanent) =>
        {
            multipleRequests++;
            requestedItems = items;
            requestedPermanent = permanent;
        };

        Assert.True(pane.DeleteSelectedCommand.CanExecute(null));
        pane.DeleteSelectedCommand.Execute(null);

        Assert.Equal(0, singleRequests);
        Assert.Equal(1, multipleRequests);
        Assert.Equal(new[] { first, second }, requestedItems);
        Assert.Equal(false, requestedPermanent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteKey_RequestsTheCorrectDeletionMode(bool permanent)
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        await pane.SelectedTab!.RefreshAsync();
        var selected = new FileItem { Name = "alpha.txt", FullPath = Path.Combine(fs.FolderA, "alpha.txt") };
        pane.SelectedTab!.SelectedItem = selected;
        bool? requestedPermanent = null;
        pane.RequestDeleteWithConfirmation += (_, mode) => requestedPermanent = mode;
        var key = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Delete,
            KeyModifiers = permanent ? KeyModifiers.Shift : KeyModifiers.None
        };

        Assert.True(KeyboardShortcutHandler.HandlePaneKeyDown(pane, key));

        Assert.True(key.Handled);
        Assert.Equal(permanent, requestedPermanent);
    }

    [Fact]
    public async Task ContextMenuDelete_WithNoSelection_DoesNotRequestDeletion()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        await pane.SelectedTab!.RefreshAsync();
        int requests = 0;
        pane.RequestDeleteWithConfirmation += (_, _) => requests++;
        pane.RequestDeleteMultipleWithConfirmation += (_, _) => requests++;

        pane.DeleteSelectedCommand.Execute(null);

        Assert.Equal(0, requests);
    }
}
