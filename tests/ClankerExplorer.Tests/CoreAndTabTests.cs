using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Tests;

public sealed class CoreAndTabTests
{
    [Fact]
    public void PaneInitialization_AlwaysCreatesASelectedVisibleTab()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, "PANE 1");

        var tab = Assert.Single(pane.Tabs);
        Assert.Same(tab, pane.SelectedTab);
        Assert.True(tab.IsSelected);
        Assert.Equal(Path.GetFullPath(fs.FolderA), tab.CurrentPath);
    }

    [Fact]
    public void Tabs_CanBeCreatedSwitchedAndRetainIndependentPaths()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        var first = pane.SelectedTab!;

        pane.AddNewTab(fs.FolderB);
        var second = pane.SelectedTab!;
        pane.AddNewTab(fs.FolderC);
        var third = pane.SelectedTab!;

        Assert.Equal(new[] { first, second, third }, pane.Tabs);
        pane.SelectedTab = first;
        Assert.Equal(Path.GetFullPath(fs.FolderA), pane.SelectedTab.CurrentPath);
        pane.SelectedTab = second;
        Assert.Equal(Path.GetFullPath(fs.FolderB), pane.SelectedTab.CurrentPath);
        pane.SelectedTab = third;
        Assert.Equal(Path.GetFullPath(fs.FolderC), pane.SelectedTab.CurrentPath);
        Assert.False(first.IsSelected);
        Assert.True(third.IsSelected);
    }

    [Fact]
    public void CloseTabs_HandlesFirstMiddleAndLastWithoutLosingAllTabs()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        var first = pane.SelectedTab!;
        pane.AddNewTab(fs.FolderB);
        var middle = pane.SelectedTab!;
        pane.AddNewTab(fs.FolderC);
        var last = pane.SelectedTab!;

        pane.CloseTab(first);
        Assert.Equal(new[] { middle, last }, pane.Tabs);

        pane.SelectedTab = middle;
        pane.CloseTab(middle);
        Assert.Single(pane.Tabs);
        Assert.Same(last, pane.SelectedTab);

        pane.CloseTab(last);
        Assert.Single(pane.Tabs);
        Assert.Same(last, pane.SelectedTab);
    }

    [Fact]
    public void PinnedTab_CannotBeClosed()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        pane.AddNewTab(fs.FolderB);
        var pinned = pane.SelectedTab!;
        pinned.IsPinned = true;

        pane.CloseTab(pinned);

        Assert.Contains(pinned, pane.Tabs);
        Assert.Same(pinned, pane.SelectedTab);
    }

    [Fact]
    public void Navigation_BackAndForwardRestoreLocations()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.FolderA);

        tab.NavigateTo(fs.FolderB);
        tab.NavigateTo(fs.FolderC);
        tab.GoBack();
        Assert.Equal(Path.GetFullPath(fs.FolderB), tab.CurrentPath);
        tab.GoBack();
        Assert.Equal(Path.GetFullPath(fs.FolderA), tab.CurrentPath);
        tab.GoForward();
        Assert.Equal(Path.GetFullPath(fs.FolderB), tab.CurrentPath);
        Assert.True(tab.CanGoForward);
    }

    [Fact]
    public void DuplicateTab_PreservesPathHistoryAndFilterState()
    {
        using var fs = new TemporaryFileSystem();
        using var source = new ExplorerTabViewModel(fs.FolderA);
        source.NavigateTo(fs.FolderB);
        source.FilterText = "*.txt";
        source.IsFilterWildcard = true;

        using var clone = source.CloneTab();

        Assert.Equal(source.CurrentPath, clone.CurrentPath);
        Assert.Equal(source.History, clone.History);
        Assert.Equal(source.HistoryIndex, clone.HistoryIndex);
        Assert.Equal(source.FilterText, clone.FilterText);
        Assert.Equal(source.IsFilterWildcard, clone.IsFilterWildcard);
    }

    [Fact]
    public void DuplicateTabCommand_PreservesPathHistoryAndFilterState()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        var source = pane.SelectedTab!;
        source.NavigateTo(fs.FolderB);
        source.FilterText = "*.txt";
        source.IsFilterWildcard = true;

        pane.DuplicateTab(source);

        var clone = pane.SelectedTab!;
        Assert.Equal(2, pane.Tabs.Count);
        Assert.NotSame(source, clone);
        Assert.Equal(source.CurrentPath, clone.CurrentPath);
        Assert.Equal(source.History, clone.History);
        Assert.Equal(source.HistoryIndex, clone.HistoryIndex);
        Assert.Equal(source.FilterText, clone.FilterText);
        Assert.Equal(source.IsFilterWildcard, clone.IsFilterWildcard);
    }

    [Fact]
    public void MainModel_CreatesIndependentLeftAndRightPaneState()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);
        using var main = new MainViewModel(loadSidebarData: false);

        Assert.NotNull(main.LeftPane.SelectedTab);
        Assert.NotNull(main.RightPane.SelectedTab);
        Assert.NotSame(main.LeftPane.SelectedTab, main.RightPane.SelectedTab);

        main.LeftPane.SelectedTab!.NavigateTo(fs.FolderB);
        main.RightPane.SelectedTab!.NavigateTo(fs.FolderC);

        Assert.Equal(Path.GetFullPath(fs.FolderB), main.LeftPane.SelectedTab.CurrentPath);
        Assert.Equal(Path.GetFullPath(fs.FolderC), main.RightPane.SelectedTab.CurrentPath);
        Assert.Same(main.LeftPane, main.ActivePane);
    }

    [Fact]
    public void MainModel_TogglesPreviewDualPaneAndAlwaysOnTopState()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);
        using var main = new MainViewModel(loadSidebarData: false);

        bool initialPreview = main.ShowInspector;
        main.ToggleInspector();
        main.ToggleDualPane();
        main.ToggleAlwaysOnTop();

        Assert.Equal(!initialPreview, main.ShowInspector);
        Assert.True(main.IsDualPane);
        Assert.True(main.IsAlwaysOnTop);

        main.ToggleAlwaysOnTop();
        Assert.False(main.IsAlwaysOnTop);
    }

    [Fact]
    public void PaneLoadsAndReactsToConfiguredTabAndThumbnailViewSettings()
    {
        using var fs = new TemporaryFileSystem();
        SettingsService.Instance.SaveSettings(new ClankerExplorer.Models.AppSettings
        {
            DefaultPath = fs.FolderB,
            StartupBehavior = "OpenDefaultPath",
            TabWidth = 208,
            ViewMode = "Details",
            ThumbnailSize = 192
        });
        using var pane = new ExplorerPaneViewModel("left", fs.FolderB);

        Assert.Equal(208, pane.TabWidth);
        Assert.True(pane.IsDetailsView);
        Assert.Equal(192, pane.ThumbnailSize);
        Assert.Equal(220, pane.ThumbnailCellWidth);
        Assert.Equal(246, pane.ThumbnailCellHeight);

        pane.SetThumbnailView();
        Assert.True(pane.IsThumbnailView);
        Assert.Equal("Thumbnails", SettingsService.Instance.CurrentSettings.ViewMode);

        pane.ThumbnailSize = 224;
        Assert.Equal(224, SettingsService.Instance.CurrentSettings.ThumbnailSize);

        pane.SetDetailsView();
        Assert.True(pane.IsDetailsView);
        Assert.Equal("Details", SettingsService.Instance.CurrentSettings.ViewMode);
    }

    [Fact]
    public void NewTabs_RespectConfiguredMaximum()
    {
        using var fs = new TemporaryFileSystem();
        var settings = new ClankerExplorer.Models.AppSettings
        {
            DefaultPath = fs.FolderA,
            StartupBehavior = "OpenDefaultPath",
            MaxTabsAllowed = 2
        };
        SettingsService.Instance.SaveSettings(settings);

        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        pane.AddNewTab(fs.FolderB);
        var selectedAtLimit = pane.SelectedTab;
        pane.AddNewTab(fs.FolderC);

        Assert.Equal(2, pane.Tabs.Count);
        Assert.Same(selectedAtLimit, pane.SelectedTab);
        TestEnvironment.ResetGlobalSettings(fs.FolderA);
    }
}
