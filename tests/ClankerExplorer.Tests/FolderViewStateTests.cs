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

    [Fact]
    public async Task Case1_SavedDestinationState_TakesPrecedenceOverInheritedPreviousState()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("case1-store"));
        store.Set(fs.FolderB, new FolderViewState
        {
            ViewMode = "Details",
            ThumbnailSize = 96,
            SortColumn = "Name",
            SortAscending = true
        });

        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();

        pane.ViewMode = "Thumbnails";
        pane.ThumbnailSize = 224;
        pane.SelectedTab.SortColumn = "Modified";
        pane.SelectedTab.SortAscending = false;
        pane.PersistCurrentFolderViewState();

        pane.SelectedTab.NavigateTo(fs.FolderB);
        await pane.SelectedTab.RefreshAsync();

        Assert.Equal("Details", pane.ViewMode);
        Assert.Equal(96, pane.ThumbnailSize);
        Assert.Equal("Name", pane.SelectedTab.SortColumn);
        Assert.True(pane.SelectedTab.SortAscending);
    }

    [Fact]
    public async Task Case2_UnsavedDestination_InheritsPreviousViewMode()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("case2-store"));
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();

        pane.ViewMode = "Thumbnails";
        pane.PersistCurrentFolderViewState();

        pane.SelectedTab.NavigateTo(fs.FolderB);
        await pane.SelectedTab.RefreshAsync();

        Assert.Equal("Thumbnails", pane.ViewMode);
    }

    [Fact]
    public async Task Case3_UnsavedDestination_InheritsThumbnailSize()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("case3-store"));
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();

        pane.ViewMode = "Thumbnails";
        pane.ThumbnailSize = 192;
        pane.PersistCurrentFolderViewState();

        pane.SelectedTab.NavigateTo(fs.FolderB);
        await pane.SelectedTab.RefreshAsync();

        Assert.Equal(192, pane.ThumbnailSize);
    }

    [Fact]
    public async Task Case4_UnsavedDestination_InheritsSorting()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("case4-store"));
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();

        pane.SelectedTab.SortColumn = "Modified";
        pane.SelectedTab.SortAscending = false;
        pane.PersistCurrentFolderViewState();

        pane.SelectedTab.NavigateTo(fs.FolderB);
        await pane.SelectedTab.RefreshAsync();

        Assert.Equal("Modified", pane.SelectedTab.SortColumn);
        Assert.False(pane.SelectedTab.SortAscending);
    }

    [Fact]
    public async Task Case5_UnsavedDestination_InheritsColumnConfigurationAndOrder()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("case5-store"));
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();

        pane.SmartColumnSizing = false;
        pane.ShowColumnExt = false;
        pane.ShowColumnDateAccessed = true;
        pane.ColumnWidthName = 380;
        pane.ColumnWidthSize = 120;
        var order = new[] { "Size", "Name", "Date Created" };
        pane.SetCurrentColumnOrder(order);
        pane.PersistCurrentFolderViewState();

        pane.SelectedTab.NavigateTo(fs.FolderB);
        await pane.SelectedTab.RefreshAsync();

        Assert.False(pane.SmartColumnSizing);
        Assert.False(pane.ShowColumnExt);
        Assert.True(pane.ShowColumnDateAccessed);
        Assert.Equal(380, pane.ColumnWidthName);
        Assert.Equal(120, pane.ColumnWidthSize);
        Assert.Equal(order, pane.CurrentColumnOrder);
    }

    [Fact]
    public async Task Case6_UnsavedDestination_DoesNotInheritScrollOffsetsOrTopItemAnchors()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("case6-store"));
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();

        pane.UpdateFolderScrollState(45, 670, 1150);
        pane.UpdateFolderViewportAnchors(Path.Combine(fs.FolderA, "item1.txt"), Path.Combine(fs.FolderA, "thumb1.png"));
        pane.PersistCurrentFolderViewState();

        pane.SelectedTab.NavigateTo(fs.FolderB);
        await pane.SelectedTab.RefreshAsync();

        Assert.Equal(0, pane.DetailsHorizontalOffset);
        Assert.Equal(0, pane.DetailsVerticalOffset);
        Assert.Equal(0, pane.ThumbnailVerticalOffset);
        Assert.Null(pane.DetailsTopItemPath);
        Assert.Null(pane.ThumbnailTopItemPath);
    }

    [Fact]
    public async Task Case7_NoPreviousFolderState_UsesGlobalDefaultSettings()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("case7-store"));
        var settings = SettingsService.Instance.CurrentSettings;

        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();

        Assert.Equal(settings.ViewMode == "Thumbnails" ? "Thumbnails" : "Details", pane.ViewMode);
        Assert.Equal(settings.ThumbnailSize, pane.ThumbnailSize);
        Assert.Equal("Name", pane.SelectedTab.SortColumn);
        Assert.True(pane.SelectedTab.SortAscending);
    }

    [Fact]
    public async Task Case8_NavigatingBackToFolderWithSavedState_RestoresSavedStateInsteadOfInheriting()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("case8-store"));

        store.Set(fs.FolderA, new FolderViewState
        {
            ViewMode = "Thumbnails",
            ThumbnailSize = 224,
            SortColumn = "Modified",
            SortAscending = false
        });

        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();

        Assert.Equal("Thumbnails", pane.ViewMode);
        Assert.Equal(224, pane.ThumbnailSize);

        pane.SelectedTab.NavigateTo(fs.FolderB);
        await pane.SelectedTab.RefreshAsync();

        pane.ViewMode = "Details";
        pane.ThumbnailSize = 100;
        pane.SelectedTab.SortColumn = "Name";
        pane.SelectedTab.SortAscending = true;
        pane.PersistCurrentFolderViewState();

        pane.SelectedTab.GoBack();
        await pane.SelectedTab.RefreshAsync();

        Assert.Equal("Thumbnails", pane.ViewMode);
        Assert.Equal(224, pane.ThumbnailSize);
        Assert.Equal("Modified", pane.SelectedTab.SortColumn);
        Assert.False(pane.SelectedTab.SortAscending);
    }
}
