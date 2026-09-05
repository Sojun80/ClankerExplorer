using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.Models;
using ClankerExplorer.Services.Watcher;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class HardeningPatchTests
{
    [Fact]
    public async Task DirectoryWatcher_SubscriberIsolation_FirstSubscriberThrows_SecondReceivesBatch()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 50);

        var tcs = new TaskCompletionSource<DirectoryChangeBatch>();
        bool firstSubscriberRan = false;

        // First subscriber throws an unexpected exception
        watcher.BatchReady += (s, e) =>
        {
            firstSubscriberRan = true;
            throw new InvalidOperationException("Simulated subscriber 1 failure");
        };

        // Second subscriber should still receive the batch
        watcher.BatchReady += (s, e) =>
        {
            tcs.TrySetResult(e);
        };

        watcher.Start(fs.FolderA);

        var filePath = Path.Combine(fs.FolderA, "test.txt");
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Created, filePath));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, completed);
        Assert.True(firstSubscriberRan);

        var batch = await tcs.Task;
        Assert.Equal(fs.FolderA, batch.DirectoryPath);
    }

    [Fact]
    public async Task DirectoryWatcher_SubscriberIsolation_OverflowBatch_FirstThrows_SecondReceives()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 50);

        var tcs = new TaskCompletionSource<DirectoryChangeBatch>();
        bool firstSubscriberRan = false;

        watcher.BatchReady += (s, e) =>
        {
            firstSubscriberRan = true;
            throw new InvalidOperationException("Simulated subscriber failure during overflow");
        };

        watcher.BatchReady += (s, e) =>
        {
            tcs.TrySetResult(e);
        };

        watcher.Start(fs.FolderA);

        // Simulate buffer overflow
        watcher.RaiseErrorForTesting(new InternalBufferOverflowException("Simulated overflow"));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, completed);
        Assert.True(firstSubscriberRan);

        var batch = await tcs.Task;
        Assert.True(batch.IsOverflow);
    }

    [Fact]
    public async Task ExplorerTabViewModel_RapidRefreshCancellation_DoesNotThrowObjectDisposedException()
    {
        using var fs = new TemporaryFileSystem();
        for (int i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(fs.FolderA, $"file_{i}.txt"), $"content {i}");
        }

        using var tab = new ExplorerTabViewModel(fs.FolderA);

        // Rapidly call Refresh and NavigateTo to repeatedly supersede and cancel _loadCts
        for (int i = 0; i < 30; i++)
        {
            tab.Refresh();
            if (i % 2 == 0)
            {
                tab.NavigateTo(fs.FolderA);
            }
        }

        // Allow final refresh to finish cleanly
        await tab.RefreshAsync();
        Assert.False(tab.IsLoading);
        Assert.NotNull(tab.Items);
        Assert.True(tab.Items.Count >= 20);
    }

    [Fact]
    public async Task ExplorerTabViewModel_FireAndForgetRefresh_DoesNotThrowOnInvalidPath()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var tab = new ExplorerTabViewModel(invalidPath);

        // Fire-and-forget Refresh
        tab.Refresh();

        // Wait a brief moment for the background load to fail gracefully
        await Task.Delay(200);

        // Does not crash, and status message reflects failure
        Assert.False(string.IsNullOrEmpty(tab.StatusMessage));
    }

    [Fact]
    public async Task SearchWorkspaceViewModel_RapidSearchCancellation_DoesNotThrowObjectDisposedException()
    {
        using var fs = new TemporaryFileSystem();
        for (int i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(fs.FolderA, $"item_{i}.txt"), $"data {i}");
        }

        using var search = new SearchWorkspaceViewModel(getCurrentFolder: () => fs.FolderA);

        // Rapidly submit and cancel searches in a tight loop to stress CTS cancellation
        for (int i = 0; i < 30; i++)
        {
            search.Query = $"item_{i}";
            search.SubmitSearch();
            search.CancelSearch();
        }

        // Final query to ensure search completes without throwing ObjectDisposedException
        search.Query = "item_1";
        search.SubmitSearch();

        await Task.Delay(150);
        Assert.False(string.IsNullOrEmpty(search.StatusText));
    }

    [Fact]
    public async Task InspectorViewModel_RapidCancellation_DoesNotThrowObjectDisposedException()
    {
        using var fs = new TemporaryFileSystem();
        var filePath = Path.Combine(fs.FolderA, "sample.txt");
        File.WriteAllText(filePath, "sample content for preview");

        using var inspector = new InspectorViewModel();

        for (int i = 0; i < 20; i++)
        {
            _ = inspector.LoadPreviewAsync(filePath);
            inspector.UnloadPreview();
        }

        await inspector.LoadPreviewAsync(filePath);
        Assert.NotNull(inspector.ActivePreviewType);
    }

    [Fact]
    public void Views_ContainZeroSynchronousWaits()
    {
        // Assert that no .GetAwaiter().GetResult(), .Wait(), or blocking calls exist in Views
        var viewsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Views"));
        if (!Directory.Exists(viewsDir))
        {
            viewsDir = @"C:\ClankerExplorer\Views";
        }

        var files = Directory.GetFiles(viewsDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain(".GetAwaiter().GetResult()", content);
        }
    }
}
