using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class ThumbnailServiceTests : IDisposable
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task MissingPathsAndDirectoriesDoNotProduceThumbnails()
    {
        using var fs = new TemporaryFileSystem();
        using var service = new ThumbnailService();

        Assert.Null(await service.GetThumbnailAsync(
            Path.Combine(fs.Root, "missing.png"), DateTime.UtcNow, 96));
        Assert.Null(await service.GetThumbnailAsync(fs.FolderA, DateTime.UtcNow, 96));
    }

    [AvaloniaFact]
    public async Task DirectImageThumbnail_IsDecodedAndCachedWithinSizeBucket()
    {
        using var fs = new TemporaryFileSystem();
        var imagePath = WritePng(fs);
        var modified = File.GetLastWriteTime(imagePath);
        using var service = new ThumbnailService();

        var first = await service.GetThumbnailAsync(imagePath, modified, 64);
        var sameBucket = await service.GetThumbnailAsync(imagePath, modified, 96);
        var changedVersion = await service.GetThumbnailAsync(imagePath, modified.AddTicks(1), 96);

        Assert.NotNull(first);
        Assert.Same(first, sameBucket);
        Assert.NotNull(changedVersion);
        Assert.NotSame(first, changedVersion);

        service.ClearCache();
        first.Dispose();
        changedVersion.Dispose();
    }

    [AvaloniaFact]
    public async Task BatchLoad_AssignsImagesAndSkipsDirectoriesAndEmptyFiles()
    {
        using var fs = new TemporaryFileSystem();
        var imagePath = WritePng(fs);
        var image = new FileItem
        {
            Name = Path.GetFileName(imagePath),
            FullPath = imagePath,
            Extension = ".png",
            SizeBytes = new FileInfo(imagePath).Length,
            ModifiedTime = File.GetLastWriteTime(imagePath)
        };
        var directory = new FileItem
        {
            Name = "FolderA",
            FullPath = fs.FolderA,
            IsDirectory = true
        };
        var empty = new FileItem
        {
            Name = "empty.png",
            FullPath = fs.CreateFile("FolderB/empty.png", string.Empty),
            Extension = ".png",
            SizeBytes = 0
        };
        using var service = new ThumbnailService();

        await service.LoadViewportAsync(new[] { image, directory, empty }, Array.Empty<FileItem>(), 96, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(image.HasThumbnail);
        Assert.Null(directory.ThumbnailImage);
        Assert.Null(empty.ThumbnailImage);

        service.ClearCache();
        (image.ThumbnailImage as IDisposable)?.Dispose();
    }

    [Fact]
    public void ExplorerPaneViewModel_ViewModeAndSizingCalculations_ReflectThumbnailSettings()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderB);
        using var pane = new ExplorerPaneViewModel("left", fs.FolderB);
        
        Assert.Equal("Details", pane.ViewMode);
        Assert.True(pane.IsDetailsView);
        Assert.False(pane.IsThumbnailView);

        pane.SetThumbnailView();
        Assert.Equal("Thumbnails", pane.ViewMode);
        Assert.False(pane.IsDetailsView);
        Assert.True(pane.IsThumbnailView);

        pane.ThumbnailSize = 160;
        Assert.Equal(188, pane.ThumbnailCellWidth);
        Assert.Equal(214, pane.ThumbnailCellHeight);
        Assert.Equal(160, pane.ThumbnailImageWidth);
        Assert.Equal(160, pane.ThumbnailImageHeight);

        pane.ToggleViewMode();
        Assert.True(pane.IsDetailsView);
        Assert.False(pane.IsThumbnailView);
    }

    [Fact]
    public void CanonicalSizes_DoNotCreateEntriesForArbitraryDisplaySizes()
    {
        Assert.Equal(128, ThumbnailService.GetCanonicalSize(64));
        Assert.Equal(128, ThumbnailService.GetCanonicalSize(128));
        Assert.Equal(256, ThumbnailService.GetCanonicalSize(129));
        Assert.Equal(256, ThumbnailService.GetCanonicalSize(220));
        Assert.Equal(512, ThumbnailService.GetCanonicalSize(320));
    }

    [Fact]
    public void ViewportPlanner_BoundsWorkInFiftyThousandItemFolder()
    {
        var window = ThumbnailViewportPlanner.Plan(
            itemCount: 50_000,
            columnCount: 5,
            firstVisibleRow: 5_000,
            lastVisibleRow: 5_005,
            prefetchViewports: 1.5);

        Assert.Equal(30, window.VisibleEnd - window.VisibleStart);
        Assert.Equal(120, window.RetainedEnd - window.RetainedStart);
        Assert.True(window.RetainedEnd < 50_000);
    }

    [AvaloniaFact]
    public async Task DiskCache_PersistsAcrossServiceInstancesAndInvalidatesOnSourceChange()
    {
        using var fs = new TemporaryFileSystem();
        string cacheDirectory = fs.CreateDirectory("thumbnail-cache");
        string imagePath = WritePng(fs);
        DateTime modified = File.GetLastWriteTime(imagePath);

        using (var firstService = new ThumbnailService(cacheDirectory))
        {
            var first = await firstService.GetThumbnailAsync(imagePath, modified, 96);
            Assert.NotNull(first);
            Assert.Single(Directory.EnumerateFiles(cacheDirectory, "*.png"));
        }

        using (var secondService = new ThumbnailService(cacheDirectory))
        {
            var cached = await secondService.GetThumbnailAsync(imagePath, modified, 96);
            Assert.NotNull(cached);
            File.SetLastWriteTime(imagePath, modified.AddSeconds(2));
            var invalidated = await secondService.GetThumbnailAsync(imagePath, modified.AddSeconds(2), 96);
            Assert.NotNull(invalidated);
            Assert.Equal(2, Directory.EnumerateFiles(cacheDirectory, "*.png").Count());
        }
    }

    [Fact]
    public async Task ThumbnailSelection_PreservesControlAndShiftSemantics()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.FolderB);
        await tab.RefreshAsync();
        tab.Items = new System.Collections.ObjectModel.ObservableCollection<FileItem>(
            Enumerable.Range(0, 6).Select(index => new FileItem { Name = $"file{index}.png" }));
        tab.ApplyFilter();

        tab.SelectThumbnailItem(tab.FilteredItems[1], control: false, shift: false);
        tab.SelectThumbnailItem(tab.FilteredItems[3], control: true, shift: false);
        Assert.Equal(2, tab.SelectedItems.Count);

        tab.SelectThumbnailItem(tab.FilteredItems[5], control: false, shift: true);
        Assert.Equal(new[] { "file3.png", "file4.png", "file5.png" }, tab.SelectedItems.Select(item => item.Name));

        tab.SelectAllThumbnails();
        Assert.Equal(6, tab.SelectedItems.Count);
        tab.ClearThumbnailSelection();
        Assert.Empty(tab.SelectedItems);
        Assert.All(tab.FilteredItems, item => Assert.False(item.IsThumbnailSelected));
    }

    private static string WritePng(TemporaryFileSystem fs)
    {
        var path = Path.Combine(fs.FolderB, "pixel.png");
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelPng));
        return path;
    }

    public void Dispose() => TestEnvironment.ResetGlobalSettings(TestEnvironment.DefaultFolder);
}
