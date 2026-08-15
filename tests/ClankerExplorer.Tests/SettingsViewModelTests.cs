using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    [Fact]
    public void Save_RoundTripsTabAndThumbnailPreferences()
    {
        using var fs = new TemporaryFileSystem();
        SettingsService.Instance.SaveSettings(new AppSettings
        {
            DefaultPath = fs.FolderA,
            StartupBehavior = "OpenDefaultPath",
            TabWidth = 190,
            ViewMode = "Thumbnails",
            ThumbnailSize = 176
        });
        var viewModel = new SettingsViewModel();

        Assert.Equal(190, viewModel.TabWidth);
        Assert.Equal("Thumbnails", viewModel.ViewMode);
        Assert.Equal(176, viewModel.ThumbnailSize);

        viewModel.TabWidth = 214;
        viewModel.ViewMode = "Details";
        viewModel.ThumbnailSize = 208;
        viewModel.Save();

        Assert.Equal(214, SettingsService.Instance.CurrentSettings.TabWidth);
        Assert.Equal("Details", SettingsService.Instance.CurrentSettings.ViewMode);
        Assert.Equal(208, SettingsService.Instance.CurrentSettings.ThumbnailSize);
    }

    [Fact]
    public void InvalidPersistedDimensionsFallBackToSafeDefaults()
    {
        using var fs = new TemporaryFileSystem();
        SettingsService.Instance.SaveSettings(new AppSettings
        {
            DefaultPath = fs.FolderA,
            StartupBehavior = "OpenDefaultPath",
            TabWidth = 5000,
            ViewMode = "UnsupportedMode",
            ThumbnailSize = 900
        });

        var viewModel = new SettingsViewModel();

        Assert.Equal(150, viewModel.TabWidth);
        Assert.Equal("Details", viewModel.ViewMode);
        Assert.Equal(144, viewModel.ThumbnailSize);
    }

    public void Dispose() => TestEnvironment.ResetGlobalSettings(TestEnvironment.DefaultFolder);
}
