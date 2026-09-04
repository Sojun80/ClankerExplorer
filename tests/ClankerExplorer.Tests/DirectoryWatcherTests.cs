using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClankerExplorer.Models;
using ClankerExplorer.Services.Watcher;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class DirectoryWatcherTests
{
    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs = 4000, int pollIntervalMs = 20)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(pollIntervalMs);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    [Fact]
    public async Task DirectoryWatcher_DebouncesAndCoalescesBurstOfChangedEvents()
    {
        using var fs = new TemporaryFileSystem();
        var filePath = Path.Combine(fs.FolderA, "alpha.txt");
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 50);

        var batches = new List<DirectoryChangeBatch>();
        var tcs = new TaskCompletionSource<bool>();

        watcher.BatchReady += (s, batch) =>
        {
            lock (batches)
            {
                batches.Add(batch);
            }
            tcs.TrySetResult(true);
        };

        watcher.Start(fs.FolderA);
        Assert.True(watcher.IsRunning);

        // Rapid burst of 20 changed events for the same file
        for (int i = 0; i < 20; i++)
        {
            watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Changed, filePath));
        }

        // Wait for debounce to fire
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, completed);

        lock (batches)
        {
            Assert.Single(batches);
            var batch = batches[0];
            Assert.Equal(fs.FolderA, batch.DirectoryPath);
            Assert.Single(batch.Changes);
            Assert.Equal(DirectoryChangeKind.Changed, batch.Changes[0].Kind);
            Assert.Equal(filePath, batch.Changes[0].FullPath);
        }
    }

    [Fact]
    public async Task DirectoryWatcher_CreateFollowedByDelete_CancelsOut()
    {
        using var fs = new TemporaryFileSystem();
        var tempFile = Path.Combine(fs.FolderA, "temp_transient.tmp");
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 50);

        var batches = new List<DirectoryChangeBatch>();
        watcher.BatchReady += (s, batch) =>
        {
            lock (batches)
            {
                batches.Add(batch);
            }
        };

        watcher.Start(fs.FolderA);

        // Created then immediate Deleted within same debounce window
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Created, tempFile));
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Deleted, tempFile));

        // Wait longer than debounce window
        await Task.Delay(150);

        lock (batches)
        {
            // Changes should have cancelled out completely
            Assert.Empty(batches);
        }
    }

    [Fact]
    public async Task DirectoryWatcher_RenameChain_CoalescesCorrectly()
    {
        using var fs = new TemporaryFileSystem();
        var path1 = Path.Combine(fs.FolderA, "step1.txt");
        var path2 = Path.Combine(fs.FolderA, "step2.txt");
        var path3 = Path.Combine(fs.FolderA, "step3.txt");
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 50);

        var batches = new List<DirectoryChangeBatch>();
        var tcs = new TaskCompletionSource<bool>();

        watcher.BatchReady += (s, batch) =>
        {
            lock (batches)
            {
                batches.Add(batch);
            }
            tcs.TrySetResult(true);
        };

        watcher.Start(fs.FolderA);

        // Created step1, then renamed step1 -> step2
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Created, path1));
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Renamed, path2, path1));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, completed);

        lock (batches)
        {
            Assert.Single(batches);
            var batch = batches[0];
            Assert.Single(batch.Changes);
            // Created + Renamed -> Created with new path
            Assert.Equal(DirectoryChangeKind.Created, batch.Changes[0].Kind);
            Assert.Equal(path2, batch.Changes[0].FullPath);
        }
    }

    [Fact]
    public async Task Tab_ExternalFileCreation_AppearsAutomatically()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);

        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        int initialCount = tab.FilteredItems.Count;

        // Create a new file externally on disk
        var newFilePath = Path.Combine(fs.FolderA, "live_created.txt");
        File.WriteAllText(newFilePath, "hello live world");

        bool appeared = await WaitForConditionAsync(() =>
            tab.FilteredItems.Any(i => string.Equals(i.Name, "live_created.txt", StringComparison.OrdinalIgnoreCase)));

        Assert.True(appeared, "Externally created file should appear in tab items automatically.");
        Assert.True(tab.FilteredItems.Count > initialCount);
        var createdItem = tab.FilteredItems.First(i => i.Name == "live_created.txt");
        Assert.Equal(newFilePath, createdItem.FullPath);
        Assert.False(createdItem.IsDirectory);
        Assert.True(createdItem.SizeBytes > 0);
    }

    [Fact]
    public async Task Tab_ExternalFileDeletion_DisappearsAutomatically()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);

        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var targetFile = Path.Combine(fs.FolderA, "beta.txt");
        Assert.Contains(tab.FilteredItems, i => i.Name == "beta.txt");

        // Delete the file externally on disk
        File.Delete(targetFile);

        bool removed = await WaitForConditionAsync(() =>
            !tab.FilteredItems.Any(i => i.Name == "beta.txt"));

        Assert.True(removed, "Externally deleted file should disappear from tab items automatically.");
    }

    [Fact]
    public async Task Tab_ExternalFileRename_UpdatesAndPreservesSelection()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);

        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var alpha = tab.FilteredItems.FirstOrDefault(i => i.Name == "alpha.txt");
        Assert.NotNull(alpha);

        // Select alpha.txt
        tab.SelectThumbnailItem(alpha, control: false, shift: false);
        Assert.True(alpha.IsThumbnailSelected);
        Assert.Equal(alpha, tab.SelectedItem);

        // Rename alpha.txt to alpha_renamed.txt externally
        var oldPath = Path.Combine(fs.FolderA, "alpha.txt");
        var newPath = Path.Combine(fs.FolderA, "alpha_renamed.txt");
        File.Move(oldPath, newPath);

        bool renamed = await WaitForConditionAsync(() =>
            tab.FilteredItems.Any(i => i.Name == "alpha_renamed.txt") &&
            !tab.FilteredItems.Any(i => i.Name == "alpha.txt"));

        Assert.True(renamed, "Renamed file should be reflected in tab items.");

        var renamedItem = tab.FilteredItems.First(i => i.Name == "alpha_renamed.txt");
        Assert.Equal(newPath, renamedItem.FullPath);
        Assert.True(renamedItem.IsThumbnailSelected, "Selection should follow the renamed item.");
        Assert.Equal(renamedItem, tab.SelectedItem);
        Assert.Contains(renamedItem, tab.SelectedItems);
    }

    [Fact]
    public async Task Tab_ExternalFileDeletion_PreservesSensibleRemainingSelection()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);

        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var file2 = tab.FilteredItems.FirstOrDefault(i => i.Name == "file2.txt");
        Assert.NotNull(file2);

        // Select file2.txt
        tab.SelectThumbnailItem(file2, control: false, shift: false);
        Assert.Equal(file2, tab.SelectedItem);

        // Delete file2.txt externally
        File.Delete(Path.Combine(fs.FolderA, "file2.txt"));

        bool removed = await WaitForConditionAsync(() =>
            !tab.FilteredItems.Any(i => i.Name == "file2.txt"));

        Assert.True(removed);
        // Selection should remain valid (either remaining item or null) without throwing
        Assert.DoesNotContain(tab.SelectedItems, i => i.Name == "file2.txt");
    }

    [Fact]
    public async Task Tab_Navigation_SwapsWatchersAndIgnoresOldDirectoryEvents()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);

        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        // Navigate to FolderB
        tab.NavigateTo(fs.FolderB);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Path.GetFullPath(fs.FolderB), tab.CurrentPath);
        Assert.Equal(Path.GetFullPath(fs.FolderB), watcher.WatchedPath);

        // Write a file in FolderA (the old directory)
        File.WriteAllText(Path.Combine(fs.FolderA, "from_old_dir.txt"), "late");
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        // File from FolderA should NOT appear in FolderB tab
        Assert.DoesNotContain(tab.Items, i => i.Name == "from_old_dir.txt");

        // Write a file in FolderB (the active directory)
        File.WriteAllText(Path.Combine(fs.FolderB, "in_new_dir.txt"), "active");

        bool activeAppeared = await WaitForConditionAsync(() =>
            tab.FilteredItems.Any(i => i.Name == "in_new_dir.txt"));

        Assert.True(activeAppeared, "File in active directory should appear after navigation.");
    }

    [Fact]
    public void Tab_WatcherDisposal_TerminatesWatchingCleanly()
    {
        using var fs = new TemporaryFileSystem();
        var watcher = new DirectoryWatcher(debounceMilliseconds: 50);
        var tab = new ExplorerTabViewModel(fs.FolderA, watcher);

        Assert.True(tab.Watcher.IsRunning);

        tab.Dispose();

        Assert.False(tab.Watcher.IsRunning);
    }

    [Fact]
    public async Task Tab_OverflowBatch_TriggersSafeRefreshFallback()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 50);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);

        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        // Simulate buffer overflow
        var reconciler = new DirectoryChangeReconciler(tab);
        reconciler.HandleBatch(new DirectoryChangeBatch(tab.CurrentPath, Array.Empty<FileChangeEvent>(), IsOverflow: true));

        // Let background refresh run
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(tab.FilteredItems);
    }

    [Fact]
    public void Tab_InvalidOrUnsupportedPath_DoesNotCrash()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"clanker_nonexistent_{Guid.NewGuid():N}");
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 50);

        // Should not throw
        using var tab = new ExplorerTabViewModel(invalidPath, watcher);
        Dispatcher.UIThread.RunJobs();

        Assert.False(watcher.IsRunning);
        Assert.Empty(tab.Items);
    }
}
