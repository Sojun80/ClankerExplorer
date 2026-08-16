using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Tests;

public sealed class FolderViewStateTests
{
    [Fact]
    public void Store_RoundTripsCompleteFolderStateAcrossServiceInstances()
    {
        using var fs = new TemporaryFileSystem();
        string dataDirectory = fs.CreateDirectory("folder-state-data");
        var expected = new FolderViewState
        {
            ViewMode = "Thumbnails",
            ThumbnailSize = 224,
            SortColumn = "Created",
            SortAscending = false,
            SmartColumnSizing = false,
            ShowColumnExt = false,
            ShowColumnDateAccessed = true,
            ColumnWidthName = 412,
            ColumnWidthDateCreated = 188,
            ColumnOrder = new List<string> { "Size", "Name", "Date Created" },
            DetailsHorizontalOffset = 17,
            DetailsVerticalOffset = 930,
            ThumbnailVerticalOffset = 1440,
            DetailsTopItemPath = Path.Combine(fs.FolderA, "file250.txt"),
            ThumbnailTopItemPath = Path.Combine(fs.FolderA, "image900.png")
        };

        using (var writer = new FolderViewStateService(dataDirectory))
        {
            writer.Set(fs.FolderA, expected);
            writer.Flush();
        }

        using var reader = new FolderViewStateService(dataDirectory);
        Assert.True(reader.TryGet(fs.FolderA + Path.DirectorySeparatorChar, out var actual));
        Assert.Equal("Thumbnails", actual.ViewMode);
        Assert.Equal(224, actual.ThumbnailSize);
        Assert.Equal("Created", actual.SortColumn);
        Assert.False(actual.SortAscending);
        Assert.False(actual.SmartColumnSizing);
        Assert.False(actual.ShowColumnExt);
        Assert.True(actual.ShowColumnDateAccessed);
        Assert.Equal(412, actual.ColumnWidthName);
        Assert.Equal(expected.ColumnOrder, actual.ColumnOrder);
        Assert.Equal(930, actual.DetailsVerticalOffset);
        Assert.Equal(1440, actual.ThumbnailVerticalOffset);
        Assert.Equal(expected.DetailsTopItemPath, actual.DetailsTopItemPath);
        Assert.Equal(expected.ThumbnailTopItemPath, actual.ThumbnailTopItemPath);
    }

    [Fact]
    public async Task NavigateAwayAndBack_RestoresEachFoldersIndependentViewState()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("pane-state-data"));
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();

        pane.ViewMode = "Thumbnails";
        pane.ThumbnailSize = 208;
        pane.SmartColumnSizing = false;
        pane.ColumnWidthName = 401;
        pane.ShowColumnExt = false;
        pane.SelectedTab.SortColumn = "Created";
        pane.SelectedTab.SortAscending = false;
        pane.SetCurrentColumnOrder(new[] { "Size", "Name", "Date Created" });
        pane.UpdateFolderScrollState(12, 840, 1230);
        pane.PersistCurrentFolderViewState();

        pane.SelectedTab.NavigateTo(fs.FolderB);
        await pane.SelectedTab.RefreshAsync();
        pane.ViewMode = "Details";
        pane.ThumbnailSize = 96;
        pane.SmartColumnSizing = true;
        pane.ColumnWidthName = 260;
        pane.ShowColumnExt = true;
        pane.SelectedTab.SortColumn = "Name";
        pane.SelectedTab.SortAscending = true;
        pane.SetCurrentColumnOrder(new[] { "Name", "Ext", "Size" });
        pane.UpdateFolderScrollState(0, 40, 0);
        pane.PersistCurrentFolderViewState();

        pane.SelectedTab.GoBack();
        await pane.SelectedTab.RefreshAsync();

        Assert.Equal(Path.GetFullPath(fs.FolderA), pane.SelectedTab.CurrentPath);
        Assert.Equal("Thumbnails", pane.ViewMode);
        Assert.Equal(208, pane.ThumbnailSize);
        Assert.False(pane.SmartColumnSizing);
        Assert.Equal(401, pane.ColumnWidthName);
        Assert.False(pane.ShowColumnExt);
        Assert.Equal("Created", pane.SelectedTab.SortColumn);
        Assert.False(pane.SelectedTab.SortAscending);
        Assert.Equal(new[] { "Size", "Name", "Date Created" }, pane.CurrentColumnOrder);
        Assert.Equal(12, pane.DetailsHorizontalOffset);
        Assert.Equal(840, pane.DetailsVerticalOffset);
        Assert.Equal(1230, pane.ThumbnailVerticalOffset);
    }
}
