using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;
using Xunit;

namespace ClankerExplorer.Tests;

public class VideoThumbnailCustomTimeTests
{
    [Fact]
    public void TryParseTimestamp_ParsesValidFormatsCorrectly()
    {
        // mm:ss
        Assert.True(VideoThumbnailService.TryParseTimestamp("01:30", out var ts1));
        Assert.Equal(TimeSpan.FromSeconds(90), ts1);

        Assert.True(VideoThumbnailService.TryParseTimestamp("1:05", out var ts2));
        Assert.Equal(TimeSpan.FromSeconds(65), ts2);

        Assert.True(VideoThumbnailService.TryParseTimestamp("00:45", out var ts3));
        Assert.Equal(TimeSpan.FromSeconds(45), ts3);

        // Raw seconds
        Assert.True(VideoThumbnailService.TryParseTimestamp("45", out var ts4));
        Assert.Equal(TimeSpan.FromSeconds(45), ts4);

        Assert.True(VideoThumbnailService.TryParseTimestamp("120.5", out var ts5));
        Assert.Equal(TimeSpan.FromSeconds(120.5), ts5);

        // hh:mm:ss
        Assert.True(VideoThumbnailService.TryParseTimestamp("01:15:30", out var ts6));
        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30), ts6);

        Assert.True(VideoThumbnailService.TryParseTimestamp("2:00:00", out var ts7));
        Assert.Equal(TimeSpan.FromHours(2), ts7);
    }

    [Fact]
    public void TryParseTimestamp_RejectsInvalidFormats()
    {
        Assert.False(VideoThumbnailService.TryParseTimestamp("", out _));
        Assert.False(VideoThumbnailService.TryParseTimestamp("   ", out _));
        Assert.False(VideoThumbnailService.TryParseTimestamp("invalid", out _));
        Assert.False(VideoThumbnailService.TryParseTimestamp("-15", out _));
        Assert.False(VideoThumbnailService.TryParseTimestamp("01:99", out _)); // invalid seconds >= 60
        Assert.False(VideoThumbnailService.TryParseTimestamp("01:02:99", out _));
        Assert.False(VideoThumbnailService.TryParseTimestamp("01:99:00", out _));
    }

    [Fact]
    public void ExplorerPaneViewModel_IsVideoFileSelected_CorrectlyIdentifiesVideo()
    {
        var pane = new ExplorerPaneViewModel("test", "Test");
        var tab = new ExplorerTabViewModel(@"C:\FakeFolder");
        pane.Tabs.Add(tab);
        pane.SelectedTab = tab;

        // Video file
        var videoItem = new FileItem
        {
            Name = "sample.mp4",
            FullPath = @"C:\FakeFolder\sample.mp4",
            Extension = ".mp4",
            IsDirectory = false
        };
        tab.SelectedItem = videoItem;
        pane.NotifyContextMenuProperties();
        Assert.True(pane.IsVideoFileSelected);

        // MKV Video file
        var mkvItem = new FileItem
        {
            Name = "movie.mkv",
            FullPath = @"C:\FakeFolder\movie.mkv",
            Extension = ".mkv",
            IsDirectory = false
        };
        tab.SelectedItem = mkvItem;
        pane.NotifyContextMenuProperties();
        Assert.True(pane.IsVideoFileSelected);

        // Text file
        var textItem = new FileItem
        {
            Name = "notes.txt",
            FullPath = @"C:\FakeFolder\notes.txt",
            Extension = ".txt",
            IsDirectory = false
        };
        tab.SelectedItem = textItem;
        pane.NotifyContextMenuProperties();
        Assert.False(pane.IsVideoFileSelected);

        // Folder
        var folderItem = new FileItem
        {
            Name = "MyVideos",
            FullPath = @"C:\FakeFolder\MyVideos",
            IsDirectory = true
        };
        tab.SelectedItem = folderItem;
        pane.NotifyContextMenuProperties();
        Assert.False(pane.IsVideoFileSelected);
    }

    [Fact]
    public void ExplorerPaneViewModel_GenerateThumbnailAtTime_RaisesRequestEvent()
    {
        var pane = new ExplorerPaneViewModel("test", "Test");
        var tab = new ExplorerTabViewModel(@"C:\FakeFolder");
        pane.Tabs.Add(tab);
        pane.SelectedTab = tab;

        var videoItem = new FileItem
        {
            Name = "sample.mp4",
            FullPath = @"C:\FakeFolder\sample.mp4",
            Extension = ".mp4",
            IsDirectory = false
        };
        tab.SelectedItem = videoItem;

        FileItem? requestedItem = null;
        pane.RequestVideoThumbnailAtTime += item => requestedItem = item;

        pane.GenerateThumbnailAtTimeCommand.Execute(null);

        Assert.NotNull(requestedItem);
        Assert.Equal("sample.mp4", requestedItem.Name);
    }

    [AvaloniaFact]
    public async Task ThumbnailService_SetCustomThumbnail_UpdatesMemoryAndDiskCache()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_CustomThumb_" + Guid.NewGuid());
        string cacheDir = Path.Combine(tempDir, "cache");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(cacheDir);

            string dummyVideoPath = Path.Combine(tempDir, "test.mp4");
            File.WriteAllText(dummyVideoPath, "DUMMY VIDEO DATA");
            var modified = File.GetLastWriteTimeUtc(dummyVideoPath);

            using var service = new ThumbnailService(cacheDir);

            // Create a small 64x64 bitmap
            var bmp = new WriteableBitmap(
                new Avalonia.PixelSize(64, 64),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            service.SetCustomThumbnail(dummyVideoPath, modified, bmp, 128);

            // Retrieve from cache
            var retrieved = await service.GetThumbnailAsync(dummyVideoPath, modified, 128);
            Assert.NotNull(retrieved);
            Assert.Same(bmp, retrieved);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
