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

        // Max total queue is 20
        Assert.True(service.QueuedRequestCount <= 20,
            $"Queue depth was {service.QueuedRequestCount}, expected <= 20");
        Assert.True(service.MaxObservedQueueDepth <= 20,
            $"MaxObservedQueueDepth was {service.MaxObservedQueueDepth}, expected <= 20");
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
        using var service = new ThumbnailService(workerCount: 1);
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        var oldPrefetch = Enumerable.Range(0, 4).Select(i => new FileItem
        {
            Name = $"old_prefetch_{i}.png",
            FullPath = $@"C:\Fake\old_prefetch_{i}.png",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        }).ToList();

        // Old prefetch queued
        _ = service.LoadViewportAsync(Array.Empty<FileItem>(), oldPrefetch, 128, cts1.Token);

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

    [Fact]
    public void StaleVisibleWork_FromOldViewportGenerations_IsDiscardedOnNewViewport()
    {
        using var service = new ThumbnailService(workerCount: 2);
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        var oldItems = Enumerable.Range(0, 10).Select(i => new FileItem
        {
            Name = $"old_vis_{i}.png",
            FullPath = $@"C:\Old\old_vis_{i}.png",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        }).ToList();

        // Enqueue old visible generation
        _ = service.LoadViewportAsync(oldItems, Array.Empty<FileItem>(), 128, cts1.Token);

        var newItems = Enumerable.Range(0, 5).Select(i => new FileItem
        {
            Name = $"new_vis_{i}.png",
            FullPath = $@"C:\New\new_vis_{i}.png",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        }).ToList();

        // Enqueue new visible generation: should discard old generation's visible items
        _ = service.LoadViewportAsync(newItems, Array.Empty<FileItem>(), 128, cts2.Token);

        Assert.True(service.DiscardedStaleCount > 0,
            $"Expected DiscardedStaleCount > 0 when replacing viewport generation, got {service.DiscardedStaleCount}");

        service.CancelPendingRequests();
    }

    [Fact]
    public void PrefetchDoesNotRun_DuringActiveScrolling()
    {
        using var service = new ThumbnailService(workerCount: 2);
        using var cts = new CancellationTokenSource();

        service.NotifyScrollActivity();
        Assert.True(service.IsActivelyScrolling);

        var prefetch = Enumerable.Range(0, 5).Select(i => new FileItem
        {
            Name = $"pre_{i}.png",
            FullPath = $@"C:\Fake\pre_{i}.png",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        }).ToList();

        _ = service.LoadViewportAsync(Array.Empty<FileItem>(), prefetch, 128, cts.Token);

        // While scrolling, prefetch should not be enqueued
        Assert.Equal(0, service.QueuedRequestCount);

        service.CancelPendingRequests();
    }

    [Fact]
    public void QueueSignalCount_DoesNotCreateWorkerSpin_AfterClearingQueues()
    {
        using var service = new ThumbnailService(workerCount: 2);
        using var cts = new CancellationTokenSource();

        var items = Enumerable.Range(0, 20).Select(i => new FileItem
        {
            Name = $"spin_test_{i}.png",
            FullPath = $@"C:\Fake\spin_test_{i}.png",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        }).ToList();

        _ = service.LoadViewportAsync(items, Array.Empty<FileItem>(), 128, cts.Token);
        Assert.True(service.QueuedRequestCount > 0);

        // Cancel all pending
        service.CancelPendingRequests();

        Assert.Equal(0, service.QueuedRequestCount);

        // Workers should not be spinning on phantom permits
        Thread.Sleep(50);
        Assert.Equal(0, service.ActiveWorkerCount);
    }

    [Fact]
    public async Task YieldFileAsync_RemovesQueuedWork_ForOnlyThatPath()
    {
        using var service = new ThumbnailService(workerCount: 2);
        using var cts = new CancellationTokenSource();

        var b0 = new FileItem { Name = "b0.mp4", FullPath = @"C:\Videos\b0.mp4", SizeBytes = 1024, ModifiedTime = DateTime.UtcNow };
        var b1 = new FileItem { Name = "b1.mp4", FullPath = @"C:\Videos\b1.mp4", SizeBytes = 1024, ModifiedTime = DateTime.UtcNow };
        var b2 = new FileItem { Name = "b2.mp4", FullPath = @"C:\Videos\b2.mp4", SizeBytes = 1024, ModifiedTime = DateTime.UtcNow };
        var itemA = new FileItem { Name = "target_video.mp4", FullPath = @"C:\Videos\target_video.mp4", SizeBytes = 1024, ModifiedTime = DateTime.UtcNow };
        var b3 = new FileItem { Name = "b3.mp4", FullPath = @"C:\Videos\b3.mp4", SizeBytes = 1024, ModifiedTime = DateTime.UtcNow };

        _ = service.LoadViewportAsync(new[] { b0, b1, b2, itemA, b3 }, Array.Empty<FileItem>(), 128, cts.Token);

        int queuedBefore = service.QueuedRequestCount;
        Assert.True(queuedBefore >= 3, $"Expected queued >= 3, but was {queuedBefore}");

        await service.YieldFileAsync(itemA.FullPath);

        // Item A was removed from queue, unrelated items remain queued
        Assert.True(service.QueuedRequestCount < queuedBefore);
        Assert.True(service.QueuedRequestCount > 0);
        Assert.True(service.CancelledCount > 0);

        service.CancelPendingRequests();
    }

    [Fact]
    public async Task YieldFileAsync_PreventsImmediateReacquisition_UntilExplicitIntent()
    {
        using var service = new ThumbnailService(workerCount: 2);
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        var item = new FileItem
        {
            Name = "video.mp4",
            FullPath = @"C:\Videos\video.mp4",
            SizeBytes = 1024,
            ModifiedTime = DateTime.UtcNow
        };

        await service.YieldFileAsync(item.FullPath);
        Assert.True(service.IsYielded(item.FullPath));

        // Attempting to re-enqueue via viewport load should be suppressed
        _ = service.LoadViewportAsync(new[] { item }, Array.Empty<FileItem>(), 128, cts1.Token);
        Assert.Equal(0, service.QueuedRequestCount);

        // Explicit user intent clears the yield guard
        service.ClearYieldGuard(item.FullPath);
        Assert.False(service.IsYielded(item.FullPath));

        // Now it can be enqueued normally
        _ = service.LoadViewportAsync(new[] { item }, Array.Empty<FileItem>(), 128, cts2.Token);
        Assert.Equal(1, service.QueuedRequestCount);

        service.CancelPendingRequests();
    }

    public void Dispose() => TestEnvironment.ResetGlobalSettings(TestEnvironment.DefaultFolder);
}
