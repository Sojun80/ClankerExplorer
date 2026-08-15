using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void Settings_RoundTripViewAndColumnStateInIsolatedDirectory()
    {
        using var fs = new TemporaryFileSystem();
        var service = new SettingsService(fs.Config);
        var expected = new AppSettings
        {
            DefaultPath = fs.FolderB,
            StartupBehavior = "RestoreSession",
            StartInDualPane = true,
            InspectorWidth = 444,
            TabWidth = 212,
            ViewMode = "Thumbnails",
            ThumbnailSize = 196,
            SmartColumnSizing = false,
            ColumnWidthName = 411,
            ColumnWidthSize = 123,
            ShowColumnPermissions = true
        };

        service.SaveSettings(expected);
        var reloaded = new SettingsService(fs.Config);

        Assert.Equal(fs.FolderB, reloaded.CurrentSettings.DefaultPath);
        Assert.Equal("RestoreSession", reloaded.CurrentSettings.StartupBehavior);
        Assert.True(reloaded.CurrentSettings.StartInDualPane);
        Assert.Equal(444, reloaded.CurrentSettings.InspectorWidth);
        Assert.Equal(212, reloaded.CurrentSettings.TabWidth);
        Assert.Equal("Thumbnails", reloaded.CurrentSettings.ViewMode);
        Assert.Equal(196, reloaded.CurrentSettings.ThumbnailSize);
        Assert.False(reloaded.CurrentSettings.SmartColumnSizing);
        Assert.Equal(411, reloaded.CurrentSettings.ColumnWidthName);
        Assert.Equal(123, reloaded.CurrentSettings.ColumnWidthSize);
        Assert.True(reloaded.CurrentSettings.ShowColumnPermissions);
        Assert.StartsWith(fs.Config, reloaded.SettingsFilePath);
    }

    [Fact]
    public void AppSettingsClone_PreservesTabAndThumbnailViewPreferences()
    {
        var original = new AppSettings
        {
            TabWidth = 205,
            ViewMode = "Thumbnails",
            ThumbnailSize = 224
        };

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.Equal(205, clone.TabWidth);
        Assert.Equal("Thumbnails", clone.ViewMode);
        Assert.Equal(224, clone.ThumbnailSize);
    }

    [Fact]
    public void Session_RoundTripPreservesPaneTabOrderActiveTabAndInspectorWidth()
    {
        using var fs = new TemporaryFileSystem();
        var service = new SessionService(fs.Config);
        var expected = new AppSessionState
        {
            IsDualPane = true,
            ActivePaneId = "right",
            InspectorWidth = 476,
            LeftPane = new PaneSessionState
            {
                ActiveTabIndex = 1,
                ActiveTabPath = fs.FolderB,
                Tabs = new List<TabSessionItem>
                {
                    new() { Path = fs.FolderA, IsPinned = true },
                    new() { Path = fs.FolderB }
                }
            },
            RightPane = new PaneSessionState
            {
                ActiveTabIndex = 0,
                ActiveTabPath = fs.FolderC,
                Tabs = new List<TabSessionItem> { new() { Path = fs.FolderC } }
            }
        };

        service.SaveSession(expected);
        var actual = new SessionService(fs.Config).LoadSession();

        Assert.NotNull(actual);
        Assert.True(actual.IsDualPane);
        Assert.Equal("right", actual.ActivePaneId);
        Assert.Equal(476, actual.InspectorWidth);
        Assert.Equal(new[] { fs.FolderA, fs.FolderB }, actual.LeftPane.Tabs.Select(tab => tab.Path));
        Assert.Equal(fs.FolderB, actual.LeftPane.ActiveTabPath);
        Assert.Equal(fs.FolderC, actual.RightPane.ActiveTabPath);
    }

    [Fact]
    public void SessionRestoreFilter_PreservesPinnedAndActiveTabsWithinCap()
    {
        using var fs = new TemporaryFileSystem();
        var service = new SessionService(fs.Config);
        var extra1 = fs.CreateDirectory("Extra1");
        var extra2 = fs.CreateDirectory("Extra2");
        var tabs = new List<TabSessionItem>
        {
            new() { Path = fs.FolderA, IsPinned = true, LastActiveTime = DateTime.UtcNow.AddHours(-5) },
            new() { Path = fs.FolderB, LastActiveTime = DateTime.UtcNow.AddHours(-4) },
            new() { Path = fs.FolderC, LastActiveTime = DateTime.UtcNow.AddHours(-3) },
            new() { Path = extra1, LastActiveTime = DateTime.UtcNow.AddHours(-2) },
            new() { Path = extra2, LastActiveTime = DateTime.UtcNow.AddHours(-1) }
        };

        var restored = service.FilterTabsToRestore(tabs, maxRestoreCap: 3, activePath: fs.FolderB);

        Assert.Equal(3, restored.Count);
        Assert.Contains(restored, tab => tab.Path == fs.FolderA && tab.IsPinned);
        Assert.Contains(restored, tab => tab.Path == fs.FolderB);
        Assert.Contains(restored, tab => tab.Path == extra2);
    }

    [Fact]
    public void History_RoundTripPreservesCountsAndTimestamps()
    {
        using var fs = new TemporaryFileSystem();
        var service = new HistoryService(fs.Config);
        var timestamp = DateTime.Now.AddDays(-1);
        service.ImportHistoryEntries(new[]
        {
            new FolderHistoryEntry { Path = fs.FolderA, VisitCount = 7, LastVisited = timestamp }
        });

        var reloaded = new HistoryService(fs.Config);
        var item = Assert.Single(reloaded.GetAllHistoryEntries());

        Assert.Equal(fs.FolderA, item.Path);
        Assert.Equal(7, item.VisitCount);
        Assert.Equal(timestamp, item.LastVisited, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void MainModel_RestoresTabOrderActivePanePathsAndInspectorWidth()
    {
        using var fs = new TemporaryFileSystem();
        SettingsService.Instance.SaveSettings(new AppSettings
        {
            DefaultPath = fs.FolderA,
            StartupBehavior = "RestoreSession",
            MaxTabsRestoredOnStartup = 8
        });
        SessionService.Instance.SaveSession(new AppSessionState
        {
            IsDualPane = true,
            ActivePaneId = "right",
            InspectorWidth = 455,
            LeftPane = new PaneSessionState
            {
                ActiveTabPath = fs.FolderB,
                Tabs = new List<TabSessionItem>
                {
                    new() { Path = fs.FolderA, IsPinned = true },
                    new() { Path = fs.FolderB }
                }
            },
            RightPane = new PaneSessionState
            {
                ActiveTabPath = fs.FolderC,
                Tabs = new List<TabSessionItem> { new() { Path = fs.FolderC } }
            }
        });

        using var main = new MainViewModel(loadSidebarData: false);

        Assert.True(main.IsDualPane);
        Assert.Same(main.RightPane, main.ActivePane);
        Assert.Equal(455, main.InspectorWidth);
        Assert.Equal(new[] { fs.FolderA, fs.FolderB }, main.LeftPane.Tabs.Select(tab => tab.CurrentPath));
        Assert.Equal(fs.FolderB, main.LeftPane.SelectedTab!.CurrentPath);
        Assert.Equal(fs.FolderC, main.RightPane.SelectedTab!.CurrentPath);
        TestEnvironment.ResetGlobalSettings(fs.FolderA);
    }
}
