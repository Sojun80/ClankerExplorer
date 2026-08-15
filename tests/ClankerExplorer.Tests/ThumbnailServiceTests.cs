using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class ThumbnailServiceTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task MissingPathsAndDirectoriesDoNotProduceThumbnails()
    {
        using var fs = new TemporaryFileSystem();
        var service = new ThumbnailService();

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
        var service = new ThumbnailService();

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
        var service = new ThumbnailService();

        await service.LoadThumbnailsAsync(new[] { image, directory, empty }, 96, CancellationToken.None);
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

    private static string WritePng(TemporaryFileSystem fs)
    {
        var path = Path.Combine(fs.FolderB, "pixel.png");
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelPng));
        return path;
    }
}
