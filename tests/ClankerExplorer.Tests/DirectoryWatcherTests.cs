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

    [Fact]
    public async Task RegressionA_RenameFollowedByChanged_PreservesRenameSemantics()
    {
        using var fs = new TemporaryFileSystem();
        var fileA = Path.Combine(fs.FolderA, "fileA.txt");
        var fileB = Path.Combine(fs.FolderA, "fileB.txt");
        File.WriteAllText(fileA, "initial content");

        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(tab.Items, i => i.Name == "fileA.txt");

        // Rename A -> B, then mutate B
        File.Move(fileA, fileB);
        File.AppendAllText(fileB, " - appended extra content for change");

        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Renamed, fileB, fileA));
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Changed, fileB));

        bool reconciled = await WaitForConditionAsync(() =>
            tab.Items.Any(i => i.Name == "fileB.txt") &&
            !tab.Items.Any(i => i.Name == "fileA.txt"));

        Assert.True(reconciled, "Only fileB.txt should exist and fileA.txt should be gone.");
        Assert.Single(tab.Items, i => i.Name == "fileB.txt");
        Assert.DoesNotContain(tab.Items, i => i.Name == "fileA.txt");

        var itemB = tab.Items.First(i => i.Name == "fileB.txt");
        Assert.Equal(fileB, itemB.FullPath);
        Assert.True(itemB.SizeBytes > "initial content".Length);
    }

    [Fact]
    public async Task RegressionB_RenameFollowedByDelete_NeitherItemExists()
    {
        using var fs = new TemporaryFileSystem();
        var fileA = Path.Combine(fs.FolderA, "to_rename_then_del.txt");
        var fileB = Path.Combine(fs.FolderA, "renamed_target.txt");
        File.WriteAllText(fileA, "will be deleted");

        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(tab.Items, i => i.Name == "to_rename_then_del.txt");

        // Enqueue Rename A -> B followed by Delete B within debounce window
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Renamed, fileB, fileA));
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Deleted, fileB));

        bool neitherExists = await WaitForConditionAsync(() =>
            !tab.Items.Any(i => i.Name == "to_rename_then_del.txt") &&
            !tab.Items.Any(i => i.Name == "renamed_target.txt"));

        Assert.True(neitherExists, "Neither original nor renamed item should exist in tab items.");
        Assert.DoesNotContain(tab.FilteredItems, i => i.Name == "to_rename_then_del.txt");
        Assert.DoesNotContain(tab.FilteredItems, i => i.Name == "renamed_target.txt");
    }

    [Fact]
    public async Task RegressionC_ChainedRenames_ResolvesOnlyToFinalName()
    {
        using var fs = new TemporaryFileSystem();
        var fileA = Path.Combine(fs.FolderA, "chainA.txt");
        var fileB = Path.Combine(fs.FolderA, "chainB.txt");
        var fileC = Path.Combine(fs.FolderA, "chainC.txt");
        File.WriteAllText(fileA, "chain test");

        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(tab.Items, i => i.Name == "chainA.txt");

        File.Move(fileA, fileC);

        // Enqueue chained renames: A -> B, then B -> C
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Renamed, fileB, fileA));
        watcher.EnqueueChange(new FileChangeEvent(DirectoryChangeKind.Renamed, fileC, fileB));

        bool resolved = await WaitForConditionAsync(() =>
            tab.Items.Any(i => i.Name == "chainC.txt") &&
            !tab.Items.Any(i => i.Name == "chainA.txt") &&
            !tab.Items.Any(i => i.Name == "chainB.txt"));

        Assert.True(resolved, "Only chainC.txt should exist.");
        Assert.Single(tab.Items, i => i.Name == "chainC.txt");
        Assert.DoesNotContain(tab.Items, i => i.Name == "chainA.txt");
        Assert.DoesNotContain(tab.Items, i => i.Name == "chainB.txt");
    }

    [Fact]
    public async Task RegressionD_ChangedMetadataLookupCompletingAfterDelete_DoesNotReappear()
    {
        using var fs = new TemporaryFileSystem();
        var targetFile = Path.Combine(fs.FolderA, "race_file.txt");
        File.WriteAllText(targetFile, "content");

        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(tab.Items, i => i.Name == "race_file.txt");

        // 1. Reconcile deletion of targetFile
        tab.Reconciler.ReconcileDeletedSync(targetFile);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(tab.Items, i => i.Name == "race_file.txt");

        // 2. Simulate late arrival of an asynchronous Changed batch whose metadata lookup resolved earlier
        var lateChangedBatch = new DirectoryChangeBatch(
            fs.FolderA,
            new[] { new FileChangeEvent(DirectoryChangeKind.Changed, targetFile) });

        tab.Reconciler.HandleBatch(lateChangedBatch);

        // Wait for reconciler pipeline to execute
        await Task.Delay(150);
        Dispatcher.UIThread.RunJobs();

        // The deleted item must NOT reappear
        Assert.DoesNotContain(tab.Items, i => i.Name == "race_file.txt");
        Assert.DoesNotContain(tab.FilteredItems, i => i.Name == "race_file.txt");
    }

    [Fact]
    public async Task RegressionE_NavigationWhileReconciliationPending_DoesNotModifyNewDirectory()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Path.GetFullPath(fs.FolderA), tab.CurrentPath);

        // Navigate to FolderB
        tab.NavigateTo(fs.FolderB);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Path.GetFullPath(fs.FolderB), tab.CurrentPath);

        // Simulate a stale batch from FolderA arriving while on FolderB
        var staleBatch = new DirectoryChangeBatch(
            fs.FolderA,
            new[] { new FileChangeEvent(DirectoryChangeKind.Created, Path.Combine(fs.FolderA, "stale_from_A.txt")) });

        tab.Reconciler.HandleBatch(staleBatch);

        await Task.Delay(150);
        Dispatcher.UIThread.RunJobs();

        // Stale result from FolderA must NOT modify FolderB items
        Assert.DoesNotContain(tab.Items, i => i.Name == "stale_from_A.txt");
        Assert.DoesNotContain(tab.FilteredItems, i => i.Name == "stale_from_A.txt");
    }

    [Fact]
    public void RegressionF_ChangedMetadataUpdatesObservableFileItemProperties()
    {
        var item = new FileItem
        {
            Name = "test.txt",
            Extension = ".txt",
            FullPath = @"C:\dummy\test.txt",
            SizeBytes = 100,
            ModifiedTime = DateTime.UtcNow.AddHours(-2),
            CreatedTime = DateTime.UtcNow.AddHours(-5),
            AccessedTime = DateTime.UtcNow.AddHours(-1),
            AttributesString = "Normal"
        };

        var notifications = new HashSet<string>();
        item.PropertyChanged += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
            {
                notifications.Add(e.PropertyName);
            }
        };

        // Mutate metadata properties
        item.SizeBytes = 5000;
        item.FormattedSize = "5 KB";
        item.ModifiedTime = DateTime.UtcNow;
        item.CreatedTime = DateTime.UtcNow.AddHours(-1);
        item.AccessedTime = DateTime.UtcNow;
        item.AttributesString = "ReadOnly, Archive";
        item.PermissionsString = "rw-r--r--";
        item.OwnerGroupString = "root:root";

        Assert.Contains(nameof(FileItem.SizeBytes), notifications);
        Assert.Contains(nameof(FileItem.SizeDisplay), notifications);
        Assert.Contains(nameof(FileItem.FormattedSize), notifications);
        Assert.Contains(nameof(FileItem.ModifiedTime), notifications);
        Assert.Contains(nameof(FileItem.FormattedModifiedTime), notifications);
        Assert.Contains(nameof(FileItem.CreatedTime), notifications);
        Assert.Contains(nameof(FileItem.FormattedCreatedTime), notifications);
        Assert.Contains(nameof(FileItem.AccessedTime), notifications);
        Assert.Contains(nameof(FileItem.FormattedAccessedTime), notifications);
        Assert.Contains(nameof(FileItem.AttributesString), notifications);
        Assert.Contains(nameof(FileItem.PermissionsString), notifications);
        Assert.Contains(nameof(FileItem.OwnerGroupString), notifications);
    }

    [Fact]
    public async Task RegressionG_DeleteSelectedItemFromMiddle_MovesToNearestWithoutJumpingToEnd()
    {
        using var fs = new TemporaryFileSystem();
        var middleDir = Path.Combine(fs.Root, "middle_select_test");
        Directory.CreateDirectory(middleDir);

        // Create 10 files
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(middleDir, $"item_{i:D2}.txt"), $"content {i}");
        }

        using var watcher = new DirectoryWatcher(debounceMilliseconds: 40);
        using var tab = new ExplorerTabViewModel(middleDir, watcher);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(10, tab.FilteredItems.Count);

        // Select item at index 4 (item_04.txt)
        var selectedItem = tab.FilteredItems[4];
        Assert.Equal("item_04.txt", selectedItem.Name);

        tab.SelectedItem = selectedItem;
        tab.SelectedItems.Add(selectedItem);
        selectedItem.IsThumbnailSelected = true;

        // Delete item_04.txt externally
        File.Delete(selectedItem.FullPath);
        tab.Reconciler.ReconcileDeletedSync(selectedItem.FullPath);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(9, tab.FilteredItems.Count);
        Assert.NotNull(tab.SelectedItem);

        // Nearest item now occupying index 4 is item_05.txt
        Assert.Equal("item_05.txt", tab.SelectedItem.Name);
        Assert.NotEqual("item_09.txt", tab.SelectedItem.Name);
    }

    [Fact]
    public async Task RegressionH_WatcherErrorOverflow_RefreshesAndRestartsWatcher()
    {
        using var fs = new TemporaryFileSystem();
        using var watcher = new DirectoryWatcher(debounceMilliseconds: 30);
        using var tab = new ExplorerTabViewModel(fs.FolderA, watcher);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(watcher.IsRunning);

        // Simulate watcher buffer overflow / internal error
        watcher.RaiseErrorForTesting(new InternalBufferOverflowException("Test buffer overflow"));

        // Wait for tab refresh and watcher restart
        bool restarted = await WaitForConditionAsync(() => watcher.IsRunning, timeoutMs: 3000);
        Assert.True(restarted, "Watcher should restart after error/overflow.");

        // Subsequent external changes should be detected and processed
        var newFile = Path.Combine(fs.FolderA, "post_recovery.txt");
        File.WriteAllText(newFile, "post recovery content");

        bool detected = await WaitForConditionAsync(() =>
            tab.FilteredItems.Any(i => i.Name == "post_recovery.txt"), timeoutMs: 3000);

        Assert.True(detected, "Subsequent external file should appear after watcher recovery.");
    }
}
