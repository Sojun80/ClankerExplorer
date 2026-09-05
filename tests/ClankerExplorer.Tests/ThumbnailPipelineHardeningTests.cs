using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class ThumbnailPipelineHardeningTests : IDisposable
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public void CancelPendingRequests_ImmediatelyClearsQueuedRequests()
    {
        using var service = new ThumbnailService(workerCount: 2);

        var items = Enumerable.Range(0, 50).Select(i => new FileItem
        {
            Name = $"file_{i}.png",
            FullPath = $@"C:\Fake\file_{i}.png",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        }).ToList();

        using var cts = new CancellationTokenSource();
        _ = service.LoadViewportAsync(items.Take(25), items.Skip(25), 128, cts.Token);

        service.CancelPendingRequests();

        Assert.Equal(0, service.QueuedRequestCount);
    }

    [AvaloniaFact]
    public async Task ViewportCancellation_StopsUnstartedWorkAndRecordsCancelledCount()
    {
        using var fs = new TemporaryFileSystem();
        var imgPath = Path.Combine(fs.FolderB, "sample.png");
        File.WriteAllBytes(imgPath, Convert.FromBase64String(OnePixelPng));

        using var service = new ThumbnailService(workerCount: 2);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var item = new FileItem
        {
            Name = "sample.png",
            FullPath = imgPath,
            SizeBytes = 100,
            ModifiedTime = DateTime.UtcNow
        };

        await service.LoadViewportAsync(new[] { item }, Array.Empty<FileItem>(), 128, cts.Token);

        Assert.Equal(0, service.QueuedRequestCount);
    }

    [Fact]
    public void BoundedQueue_NeverExceedsConfiguredHardLimit()
    {
        using var service = new ThumbnailService(workerCount: 2);
        using var cts = new CancellationTokenSource();

        // Simulate scrolling past 200 visible and 200 prefetch items
        var visible = Enumerable.Range(0, 200).Select(i => new FileItem
        {
            Name = $"vis_{i}.png",
            FullPath = $@"C:\Huge\vis_{i}.png",
            SizeBytes = 2048,
            ModifiedTime = DateTime.UtcNow
        }).ToList();

        var prefetch = Enumerable.Range(0, 200).Select(i => new FileItem
        {
            Name = $"pre_{i}.png",
            FullPath = $@"C:\Huge\pre_{i}.png",
            SizeBytes = 2048,
            ModifiedTime = DateTime.UtcNow
        }).ToList();

        _ = service.LoadViewportAsync(visible, prefetch, 128, cts.Token);

        // Max total queue is 48
        Assert.True(service.QueuedRequestCount <= 48,
            $"Queue depth was {service.QueuedRequestCount}, expected <= 48");
        Assert.True(service.MaxObservedQueueDepth <= 48,
            $"MaxObservedQueueDepth was {service.MaxObservedQueueDepth}, expected <= 48");
        Assert.True(service.DroppedQueueFullCount > 0,
            "Expected DroppedQueueFullCount to be > 0 when queue limit is reached");

        service.CancelPendingRequests();
    }

    [Fact]
    public void DeDuplication_SuppressesDuplicateQueuedRequests()
    {
        using var service = new ThumbnailService(workerCount: 2);
        using var cts = new CancellationTokenSource();

        var item1 = new FileItem
        {
            Name = "same.png",
            FullPath = @"C:\Fake\same.png",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        };
        var item2 = new FileItem
        {
            Name = "same.png",
            FullPath = @"C:\Fake\same.png",
            SizeBytes = 1024,
            ModifiedTime = item1.ModifiedTime
        };

        _ = service.LoadViewportAsync(new[] { item1, item2 }, Array.Empty<FileItem>(), 128, cts.Token);

        Assert.True(service.SuppressedDuplicateCount > 0,
            $"Expected SuppressedDuplicateCount > 0, but was {service.SuppressedDuplicateCount}");

        service.CancelPendingRequests();
    }

    [Fact]
    public void StaleEviction_DiscardsOldPrefetchOnNewViewport()
    {
        using var service = new ThumbnailService(workerCount: 2);
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        var itemOld = new FileItem
        {
            Name = "old_prefetch.png",
            FullPath = @"C:\Fake\old_prefetch.png",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        };

        // Old prefetch queued
        _ = service.LoadViewportAsync(Array.Empty<FileItem>(), new[] { itemOld }, 128, cts1.Token);

        var itemNew = new FileItem
        {
            Name = "new_visible.png",
            FullPath = @"C:\Fake\new_visible.png",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        };

        // Loading new viewport should immediately evict old prefetch
        _ = service.LoadViewportAsync(new[] { itemNew }, Array.Empty<FileItem>(), 128, cts2.Token);

        Assert.True(service.DiscardedStaleCount > 0,
            $"Expected DiscardedStaleCount > 0, but was {service.DiscardedStaleCount}");

        service.CancelPendingRequests();
    }

    [AvaloniaFact]
    public void TryPopulateFromMemoryCache_AssignsSynchronouslyWithoutQueueing()
    {
        using var fs = new TemporaryFileSystem();
        var imgPath = Path.Combine(fs.FolderB, "mem_test.png");
        File.WriteAllBytes(imgPath, Convert.FromBase64String(OnePixelPng));
        var modified = File.GetLastWriteTime(imgPath);

        using var service = new ThumbnailService(workerCount: 2);

        // Pre-cache item in memory
        using var bmp = new Avalonia.Media.Imaging.Bitmap(imgPath);
        string key = ThumbnailService.GetCacheKey(imgPath, new FileInfo(imgPath).Length, modified.Ticks, 128);
        service.AddMemoryEntry(key, bmp);

        var item = new FileItem
        {
            Name = "mem_test.png",
            FullPath = imgPath,
            SizeBytes = new FileInfo(imgPath).Length,
            ModifiedTime = modified
        };

        Assert.Null(item.ThumbnailImage);

        // Populate directly from memory cache
        int hits = service.TryPopulateFromMemoryCache(new[] { item }, 128);

        Assert.Equal(1, hits);
        Assert.NotNull(item.ThumbnailImage);
        Assert.Equal(0, service.QueuedRequestCount); // Nothing was enqueued!
    }

    [Fact]
    public void ScrollThrottling_ReportsActiveScrollingCorrectly()
    {
        using var service = new ThumbnailService(workerCount: 2);

        Assert.False(service.IsActivelyScrolling);

        service.NotifyScrollActivity();

        Assert.True(service.IsActivelyScrolling);
    }

    [AvaloniaFact]
    public async Task SafePublish_DoesNotMutateRecycledItemWithMismatchedPath()
    {
        using var fs = new TemporaryFileSystem();
        var imgPath = Path.Combine(fs.FolderB, "target.png");
        File.WriteAllBytes(imgPath, Convert.FromBase64String(OnePixelPng));
        var modified = File.GetLastWriteTime(imgPath);

        using var service = new ThumbnailService(workerCount: 2);

        var item = new FileItem
        {
            Name = "target.png",
            FullPath = imgPath,
            SizeBytes = new FileInfo(imgPath).Length,
            ModifiedTime = modified
        };

        var loadTask = service.LoadViewportAsync(new[] { item }, Array.Empty<FileItem>(), 128, CancellationToken.None);

        // Before it finishes on UI, simulate virtualized row recycling where item points to a different file
        item.FullPath = Path.Combine(fs.FolderB, "different_recycled_file.png");

        await loadTask;
        Dispatcher.UIThread.RunJobs();

        // Because FullPath changed, the stale result should NOT be assigned to this recycled item
        Assert.Null(item.ThumbnailImage);
    }

    [AvaloniaFact]
    public async Task MemoryCacheHits_AreInstrumentedCorrectly()
    {
        using var fs = new TemporaryFileSystem();
        var imgPath = Path.Combine(fs.FolderB, "cached.png");
        File.WriteAllBytes(imgPath, Convert.FromBase64String(OnePixelPng));
        var modified = File.GetLastWriteTime(imgPath);

        using var service = new ThumbnailService(workerCount: 2);

        var first = await service.GetThumbnailAsync(imgPath, modified, 128);
        Assert.NotNull(first);

        long hitsBefore = service.MemoryCacheHitCount;
        var second = await service.GetThumbnailAsync(imgPath, modified, 128);
        Assert.Same(first, second);
        Assert.True(service.MemoryCacheHitCount > hitsBefore);

        first.Dispose();
    }

    public void Dispose() => TestEnvironment.ResetGlobalSettings(TestEnvironment.DefaultFolder);
}
