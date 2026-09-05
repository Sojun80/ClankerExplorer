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
            ThumbnailSize = 224,
            ThemePreset = "CyberDark"
        };

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.Equal(205, clone.TabWidth);
        Assert.Equal("Thumbnails", clone.ViewMode);
        Assert.Equal(224, clone.ThumbnailSize);
        Assert.Equal("CyberDark", clone.ThemePreset);

        // Mutating primitive/string on clone does not mutate original
        clone.TabWidth = 300;
        clone.ViewMode = "Details";
        clone.ThemePreset = "DraculaViolet";

        Assert.Equal(205, original.TabWidth);
        Assert.Equal("Thumbnails", original.ViewMode);
        Assert.Equal("CyberDark", original.ThemePreset);
    }

    [Fact]
    public void SettingsService_UpdateSettings_MutatesPersistsAndRaisesEvent()
    {
        using var fs = new TemporaryFileSystem();
        var service = new SettingsService(fs.Config);
        AppSettings? eventReceived = null;
        service.SettingsChanged += s => eventReceived = s;

        service.UpdateSettings(s =>
        {
            s.ThumbnailSize = 280;
            s.ThemePreset = "NordicSlate";
            s.ConfirmBeforeDelete = false;
        });

        Assert.Equal(280, service.CurrentSettings.ThumbnailSize);
        Assert.Equal("NordicSlate", service.CurrentSettings.ThemePreset);
        Assert.False(service.CurrentSettings.ConfirmBeforeDelete);

        // Verify event fired with updated instance
        Assert.NotNull(eventReceived);
        Assert.Equal(280, eventReceived!.ThumbnailSize);
        Assert.Equal("NordicSlate", eventReceived.ThemePreset);

        // Verify persistence to disk by reloading in a new service instance
        var reloaded = new SettingsService(fs.Config);
        Assert.Equal(280, reloaded.CurrentSettings.ThumbnailSize);
        Assert.Equal("NordicSlate", reloaded.CurrentSettings.ThemePreset);
        Assert.False(reloaded.CurrentSettings.ConfirmBeforeDelete);
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

    [Fact]
    public void Session_RoundTripPreservesWindowGeometryAndMaximizedState()
    {
        using var fs = new TemporaryFileSystem();
        var service = new SessionService(fs.Config);
        var expected = new AppSessionState
        {
            WindowX = 140,
            WindowY = 180,
            WindowWidth = 1420,
            WindowHeight = 910,
            IsMaximized = true
        };

        service.SaveSession(expected);
        var actual = new SessionService(fs.Config).LoadSession();

        Assert.NotNull(actual);
        Assert.Equal(140, actual.WindowX);
        Assert.Equal(180, actual.WindowY);
        Assert.Equal(1420, actual.WindowWidth);
        Assert.Equal(910, actual.WindowHeight);
        Assert.True(actual.IsMaximized);
        Assert.True(SessionService.HasValidWindowGeometry(actual));
    }

    [Fact]
    public void Session_LegacySessionWithoutWindowGeometry_DeserializesNullsAndIsCompatible()
    {
        using var fs = new TemporaryFileSystem();
        string sessionFile = Path.Combine(fs.Config, "session.json");
        string legacyJson = """
        {
          "IsDualPane": false,
          "ActivePaneId": "left",
          "InspectorWidth": 320.0,
          "LeftPane": { "Tabs": [], "ActiveTabIndex": 0 },
          "RightPane": { "Tabs": [], "ActiveTabIndex": 0 }
        }
        """;
        File.WriteAllText(sessionFile, legacyJson);

        var service = new SessionService(fs.Config);
        var session = service.LoadSession();

        Assert.NotNull(session);
        Assert.Null(session.WindowX);
        Assert.Null(session.WindowY);
        Assert.Null(session.WindowWidth);
        Assert.Null(session.WindowHeight);
        Assert.False(session.IsMaximized);
        Assert.False(SessionService.HasValidWindowGeometry(session));
    }

    [Fact]
    public void WindowGeometryHelper_ClampWindowBounds_PreservesPositionWhenIntersectsWorkArea()
    {
        var screens = new[]
        {
            new ScreenBounds(0, 0, 1920, 1080, IsPrimary: true)
        };

        var (x, y, w, h) = WindowGeometryHelper.ClampWindowBounds(150, 120, 1400, 900, screens);

        Assert.Equal(150, x);
        Assert.Equal(120, y);
        Assert.Equal(1400, w);
        Assert.Equal(900, h);
    }

    [Fact]
    public void WindowGeometryHelper_ClampWindowBounds_DisconnectedMonitor_RecentersOntoPrimaryScreen()
    {
        // Saved window was at X=2560 (2nd monitor), but only primary monitor is currently connected
        var screens = new[]
        {
            new ScreenBounds(0, 0, 1920, 1080, IsPrimary: true)
        };

        var (x, y, w, h) = WindowGeometryHelper.ClampWindowBounds(2560, 100, 1360, 960, screens);

        // Clamped window must be centered and completely inside primary work area
        Assert.Equal((1920 - 1360) / 2, x);
        Assert.Equal((1080 - 960) / 2, y);
        Assert.Equal(1360, w);
        Assert.Equal(960, h);
    }

    [Fact]
    public void WindowGeometryHelper_ClampWindowBounds_SanitizesZeroNegativeNaNAndAbsurdSizes()
    {
        var screens = new[]
        {
            new ScreenBounds(0, 0, 1920, 1080, IsPrimary: true)
        };

        // Case A: zero/negative dimensions reset to defaults
        var resA = WindowGeometryHelper.ClampWindowBounds(50, 50, 0, -20, screens);
        Assert.Equal(WindowGeometryHelper.DefaultWindowWidth, resA.Width);
        Assert.Equal(WindowGeometryHelper.DefaultWindowHeight, resA.Height);

        // Case B: NaN / infinity reset to defaults
        var resB = WindowGeometryHelper.ClampWindowBounds(50, 50, double.NaN, double.PositiveInfinity, screens);
        Assert.Equal(WindowGeometryHelper.DefaultWindowWidth, resB.Width);
        Assert.Equal(WindowGeometryHelper.DefaultWindowHeight, resB.Height);

        // Case C: Absurd sizes (> MaxAbsurdDimension) reset to defaults
        var resC = WindowGeometryHelper.ClampWindowBounds(0, 0, 50000, 50000, screens);
        Assert.Equal(WindowGeometryHelper.DefaultWindowWidth, resC.Width);
        Assert.Equal(WindowGeometryHelper.DefaultWindowHeight, resC.Height);

        // Case C2: Size larger than screen but under absurd threshold clamps to screen work area
        var resC2 = WindowGeometryHelper.ClampWindowBounds(0, 0, 2500, 1500, screens);
        Assert.Equal(1920, resC2.Width);
        Assert.Equal(1080, resC2.Height);

        // Case D: Title bar off-screen above screen (Y = -500) gets clamped onto work area
        var resD = WindowGeometryHelper.ClampWindowBounds(50, -500, 1200, 800, screens);
        Assert.True(resD.Y >= 0);
    }
}
