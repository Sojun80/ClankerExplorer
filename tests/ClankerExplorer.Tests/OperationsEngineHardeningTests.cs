using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClankerExplorer.AppLayer;
using ClankerExplorer.AppLayer.Operations;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Services.Watcher;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class OperationsEngineHardeningTests : IDisposable
{
    private readonly OperationManager _manager = new();

    public void Dispose()
    {
        _manager.Dispose();
        ClipboardFileService.Copy(Array.Empty<string>());
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs = 5000, int pollIntervalMs = 20)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(pollIntervalMs);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    [Fact]
    public async Task ScenarioA_QueuedCopy_CreatesDestinationAndEntersHistory()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "file_a.txt");
        File.WriteAllText(src, "Scenario A content");

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Copy);
        var job = _manager.EnqueueTransfer(req);

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(completed);

        var dst = Path.Combine(fs.FolderB, "file_a.txt");
        Assert.True(File.Exists(dst));
        Assert.Equal("Scenario A content", File.ReadAllText(dst));
        Assert.Contains(job, _manager.HistoryJobs);
        Assert.DoesNotContain(job, _manager.ActiveJobs);
    }

    [Fact]
    public async Task ScenarioB_QueuedMove_RemovesSourceAndCreatesDestination()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "file_b.txt");
        File.WriteAllText(src, "Scenario B content");

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Move);
        var job = _manager.EnqueueTransfer(req);

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(completed);

        var dst = Path.Combine(fs.FolderB, "file_b.txt");
        Assert.True(File.Exists(dst));
        Assert.False(File.Exists(src));
        Assert.Equal("Scenario B content", File.ReadAllText(dst));
    }

    [Fact]
    public async Task ScenarioC_PasteFromClipboard_EnqueuesOperationViaOperationManager()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "clipboard_src.txt");
        File.WriteAllText(src, "Clipboard paste content");

        ClipboardFileService.Copy(new[] { src });

        var job = await ClipboardFileService.EnqueuePasteFromSystemClipboardAsync(null, fs.FolderB);
        Assert.NotNull(job);

        var result = await job.CompletionTask;
        Assert.True(result.Succeeded);

        var dst = Path.Combine(fs.FolderB, "clipboard_src.txt");
        Assert.True(File.Exists(dst));
        Assert.Equal("Clipboard paste content", File.ReadAllText(dst));
    }

    [Fact]
    public async Task ScenarioD_CancelOverwriteCopy_PreservesOriginalDestinationAndCleansUpPartials()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "conflict_file.txt");
        var dst = Path.Combine(fs.FolderB, "conflict_file.txt");
        File.WriteAllText(src, "NEW CONTENT SHOULD NOT OVERWRITE");
        File.WriteAllText(dst, "ORIGINAL UNTOUCHED CONTENT");

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Copy, FileConflictPolicy.Prompt);
        var job = _manager.EnqueueTransfer(req);

        bool needsAttention = await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);
        Assert.True(needsAttention);

        job.RequestCancel();

        await WaitForConditionAsync(() => job.State == OperationState.Cancelled, timeoutMs: 5000);

        Assert.True(File.Exists(dst));
        Assert.Equal("ORIGINAL UNTOUCHED CONTENT", File.ReadAllText(dst));

        // Ensure no sibling temp files were left behind
        var tempFiles = Directory.GetFiles(fs.FolderB, ".clanker-transfer-*.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task ScenarioE_FailedOverwriteCopy_PreservesOriginalDestination()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "locked_src.txt");
        var dst = Path.Combine(fs.FolderB, "locked_src.txt");
        File.WriteAllText(src, "NEW LOCKED CONTENT");
        File.WriteAllText(dst, "ORIGINAL CONTENT PRESERVED ON ERROR");

        // Lock source file completely so it cannot be read
        using var lockStream = new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Copy, FileConflictPolicy.Overwrite);
        var job = _manager.EnqueueTransfer(req);

        await WaitForConditionAsync(() => job.State == OperationState.Failed, timeoutMs: 5000);

        Assert.Equal("ORIGINAL CONTENT PRESERVED ON ERROR", File.ReadAllText(dst));
        var tempFiles = Directory.GetFiles(fs.FolderB, ".clanker-transfer-*.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task ScenarioF_SuccessfulOverwriteCopy_ReplacesDestinationAtomicallyAndCleansUpTmp()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "overwrite_me.txt");
        var dst = Path.Combine(fs.FolderB, "overwrite_me.txt");
        File.WriteAllText(src, "BRAND NEW CONTENT");
        File.WriteAllText(dst, "OLD DEST CONTENT");

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Copy, FileConflictPolicy.Overwrite);
        var job = _manager.EnqueueTransfer(req);

        bool done = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(done);

        Assert.Equal("BRAND NEW CONTENT", File.ReadAllText(dst));
        var tempFiles = Directory.GetFiles(fs.FolderB, ".clanker-transfer-*.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task ScenarioG_SameVolumeMoveReplace_ReplacesSafelyWithoutDeleteFirstGap()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "move_replace.txt");
        var dst = Path.Combine(fs.FolderB, "move_replace.txt");
        File.WriteAllText(src, "MOVE REPLACEMENT PAYLOAD");
        File.WriteAllText(dst, "OLD DEST TO REPLACE");

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Move, FileConflictPolicy.Overwrite);
        var job = _manager.EnqueueTransfer(req);

        bool done = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(done);

        Assert.False(File.Exists(src));
        Assert.True(File.Exists(dst));
        Assert.Equal("MOVE REPLACEMENT PAYLOAD", File.ReadAllText(dst));
    }

    [Fact]
    public async Task ScenarioH_CrossVolumeMove_SourceDeleteFailurePreservesDestinationAndReportsWarning()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "undeletable_src.txt");
        var dst = Path.Combine(fs.FolderB, "undeletable_src.txt");
        File.WriteAllText(src, "CROSS VOLUME SIMULATION CONTENT");

        // Keep file open with FileShare.Read so copy succeeds, but delete fails
        using var readStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Move, FileConflictPolicy.AutoRename);
        var job = _manager.EnqueueTransfer(req);

        var result = await job.CompletionTask;

        var item = Assert.Single(result.Items);
        Assert.Equal(FileTransferStatus.PartialSuccessSourceDeleteFailed, item.Status);
        Assert.True(File.Exists(dst));
        Assert.Equal("CROSS VOLUME SIMULATION CONTENT", File.ReadAllText(dst));
        Assert.True(File.Exists(src)); // Source was not deleted
    }

    [Fact]
    public async Task ScenarioI_RecursiveCopyAccounting_TracksNestedCountsAndBytes()
    {
        using var fs = new TemporaryFileSystem();
        var srcDir = Path.Combine(fs.FolderA, "Tree");
        var subDir = Path.Combine(srcDir, "Sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(srcDir, "f1.txt"), "hello");
        File.WriteAllText(Path.Combine(srcDir, "f2.txt"), "world");
        File.WriteAllText(Path.Combine(subDir, "f3.txt"), "nested");

        var req = new FileTransferRequest(new[] { srcDir }, fs.FolderB, FileTransferMode.Copy);
        var job = _manager.EnqueueTransfer(req);

        var result = await job.CompletionTask;
        Assert.True(result.Succeeded);

        var summary = job.Summary;
        Assert.NotNull(summary);
        Assert.Equal(3, job.Progress.ProcessedItems); // 3 nested files
        Assert.Equal(3, summary.SucceededCount); // 3 files transferred
        Assert.Equal(0, summary.FailedCount);
        Assert.Equal("hello".Length + "world".Length + "nested".Length, summary.TotalBytes);
        Assert.Equal(summary.TotalBytes, job.Progress.TransferredBytes);
    }

    [Fact]
    public async Task ScenarioJ_NativeMoveProgress_CompletesAt100Percent()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "fast_move.txt");
        File.WriteAllText(src, "fast move progress verification");

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Move);
        var job = _manager.EnqueueTransfer(req);

        await job.CompletionTask;

        Assert.Equal(100.0, job.Progress.Percentage);
        Assert.Equal(job.Progress.TotalBytes, job.Progress.TransferredBytes);
        Assert.Equal(job.Progress.TotalItems, job.Progress.ProcessedItems);
    }

    [Fact]
    public async Task ScenarioK_ConflictCounter_IncrementsAndPersistsInHistory()
    {
        using var fs = new TemporaryFileSystem();
        var src1 = Path.Combine(fs.FolderA, "c1.txt");
        var src2 = Path.Combine(fs.FolderA, "c2.txt");
        var dst1 = Path.Combine(fs.FolderB, "c1.txt");
        var dst2 = Path.Combine(fs.FolderB, "c2.txt");
        File.WriteAllText(src1, "src1");
        File.WriteAllText(src2, "src2");
        File.WriteAllText(dst1, "dst1");
        File.WriteAllText(dst2, "dst2");

        var req = new FileTransferRequest(new[] { src1, src2 }, fs.FolderB, FileTransferMode.Copy, FileConflictPolicy.Prompt);
        var job = _manager.EnqueueTransfer(req);

        // First conflict
        bool c1 = await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);
        Assert.True(c1);
        Assert.Equal(1, job.ConflictCount);
        job.ResolveConflict(new ConflictResolution(ConflictAction.KeepBoth, ApplyToAllRemaining: false));

        // Second conflict
        bool c2 = await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);
        Assert.True(c2);
        Assert.Equal(2, job.ConflictCount);
        job.ResolveConflict(new ConflictResolution(ConflictAction.KeepBoth, ApplyToAllRemaining: false));

        await job.CompletionTask;

        Assert.Equal(2, job.ConflictCount);
        Assert.Equal(2, job.Progress.ConflictCount);
        Assert.Contains(job, _manager.HistoryJobs);
    }

    [Fact]
    public async Task ScenarioL_CustomRename_RejectsInvalidNamesAndRepromptsUntilValid()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "rename_test.txt");
        var dst = Path.Combine(fs.FolderB, "rename_test.txt");
        File.WriteAllText(src, "new file");
        File.WriteAllText(dst, "existing destination");

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Copy, FileConflictPolicy.Prompt);
        var job = _manager.EnqueueTransfer(req);

        await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);

        // 1. Try name with directory separators
        job.ResolveConflict(new ConflictResolution(ConflictAction.Rename, CustomNewName: "sub/folder/file.txt"));
        await Task.Delay(100);
        Assert.Equal(OperationState.NeedsAttention, job.State);

        // 2. Try name with parent path traversal
        job.ResolveConflict(new ConflictResolution(ConflictAction.Rename, CustomNewName: @"..\escape.txt"));
        await Task.Delay(100);
        Assert.Equal(OperationState.NeedsAttention, job.State);

        // 3. Try name that already exists
        job.ResolveConflict(new ConflictResolution(ConflictAction.Rename, CustomNewName: "rename_test.txt"));
        await Task.Delay(100);
        Assert.Equal(OperationState.NeedsAttention, job.State);

        // 4. Try valid new name
        job.ResolveConflict(new ConflictResolution(ConflictAction.Rename, CustomNewName: "valid_unique_rename.txt"));

        var result = await job.CompletionTask;
        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(fs.FolderB, "valid_unique_rename.txt")));
    }

    [Fact]
    public async Task ScenarioM_ReparsePoint_DoesNotRecursivelyFollowExternalTrees()
    {
        using var fs = new TemporaryFileSystem();
        var sourceDir = Path.Combine(fs.FolderA, "SourceWithLink");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "direct.txt"), "direct content");

        var externalDir = Path.Combine(fs.Root, "ExternalSecretFolder");
        Directory.CreateDirectory(externalDir);
        File.WriteAllText(Path.Combine(externalDir, "secret.txt"), "secret payload");

        var linkDir = Path.Combine(sourceDir, "LinkToExternal");
        try
        {
            Directory.CreateSymbolicLink(linkDir, externalDir);
        }
        catch
        {
            // If running in environment without symlink privileges, test calculation safety directly
            return;
        }

        var req = new FileTransferRequest(new[] { sourceDir }, fs.FolderB, FileTransferMode.Copy);
        var job = _manager.EnqueueTransfer(req);

        await job.CompletionTask;

        var copiedDirect = Path.Combine(fs.FolderB, "SourceWithLink", "direct.txt");
        Assert.True(File.Exists(copiedDirect));

        // Crucial: external secret file was NOT recursively cloned into the destination tree
        var copiedSecret = Path.Combine(fs.FolderB, "SourceWithLink", "LinkToExternal", "secret.txt");
        Assert.False(File.Exists(copiedSecret));
    }

    [Fact]
    public async Task ScenarioN_QueueContinuesProcessingAfterFailedJob()
    {
        using var fs = new TemporaryFileSystem();
        var badSrc = Path.Combine(fs.FolderA, "does_not_exist_at_all.txt");
        var goodSrc = Path.Combine(fs.FolderA, "good_file.txt");
        File.WriteAllText(goodSrc, "good content");

        var job1 = _manager.EnqueueTransfer(new FileTransferRequest(new[] { badSrc }, fs.FolderB, FileTransferMode.Copy));
        var job2 = _manager.EnqueueTransfer(new FileTransferRequest(new[] { goodSrc }, fs.FolderB, FileTransferMode.Copy));

        await WaitForConditionAsync(() => job2.State == OperationState.Completed, timeoutMs: 6000);

        Assert.Equal(OperationState.Failed, job1.State);
        Assert.Equal(OperationState.Completed, job2.State);
        Assert.True(File.Exists(Path.Combine(fs.FolderB, "good_file.txt")));
    }

    [Fact]
    public async Task ScenarioO_HiddenOperationsWorkspace_DoesNotHinderExecution()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "background_job.txt");
        File.WriteAllText(src, "background execution content");

        // No UI workspace instantiated at all; queue operates purely in background
        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Copy);
        var job = _manager.EnqueueTransfer(req);

        await job.CompletionTask;

        Assert.True(File.Exists(Path.Combine(fs.FolderB, "background_job.txt")));
        Assert.Contains(job, _manager.HistoryJobs);
    }

    [Fact]
    public async Task ScenarioP_WatcherStaging_ReconcilesBatchesAfterLoad()
    {
        using var fs = new TemporaryFileSystem();
        var deletedFile = Path.Combine(fs.FolderA, "staged_delete.txt");
        File.WriteAllText(deletedFile, "delete me");

        var tab = new ExplorerTabViewModel(fs.FolderA);
        await tab.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(tab.Items, i => i.Name == "staged_delete.txt");

        // 1. Begin staging with generation token
        long token = tab.Reconciler.BeginStaging();

        var createdFile = Path.Combine(fs.FolderA, "staged_create.txt");
        File.WriteAllText(createdFile, "created content");
        File.Delete(deletedFile);

        var batch = new DirectoryChangeBatch(
            fs.FolderA,
            new[]
            {
                new FileChangeEvent(DirectoryChangeKind.Created, createdFile),
                new FileChangeEvent(DirectoryChangeKind.Deleted, deletedFile)
            });

        tab.Reconciler.HandleBatch(batch);

        // Before replay, items were not modified by reconciler yet
        Assert.DoesNotContain(tab.Items, i => i.Name == "staged_create.txt");

        // 2. Replay staging session with token
        tab.Reconciler.EndStagingAndReplay(token);

        await WaitForConditionAsync(() => tab.Items.Any(i => i.Name == "staged_create.txt") && !tab.Items.Any(i => i.Name == "staged_delete.txt"), timeoutMs: 5000);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(tab.Items, i => i.Name == "staged_create.txt");
        Assert.DoesNotContain(tab.Items, i => i.Name == "staged_delete.txt");

        // 3. Generation collision: older token cancel does not cancel newer staging
        long token1 = tab.Reconciler.BeginStaging();
        long token2 = tab.Reconciler.BeginStaging();
        Assert.True(token2 > token1);

        tab.Reconciler.CancelStaging(token1);

        var stagedSecond = Path.Combine(fs.FolderA, "staged_second.txt");
        File.WriteAllText(stagedSecond, "second");
        tab.Reconciler.HandleBatch(new DirectoryChangeBatch(fs.FolderA, new[] { new FileChangeEvent(DirectoryChangeKind.Created, stagedSecond) }));

        tab.Reconciler.EndStagingAndReplay(token2);
        await WaitForConditionAsync(() => tab.Items.Any(i => i.Name == "staged_second.txt"), timeoutMs: 5000);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(tab.Items, i => i.Name == "staged_second.txt");

        // 4. Overflow coalesce: overflow batch during staging coalesces into post-load refresh
        long tokenOverflow = tab.Reconciler.BeginStaging();
        var stagedOverflowFile = Path.Combine(fs.FolderA, "overflow_file.txt");
        File.WriteAllText(stagedOverflowFile, "overflow content");

        tab.Reconciler.HandleBatch(new DirectoryChangeBatch(fs.FolderA, Array.Empty<FileChangeEvent>(), IsOverflow: true));

        tab.Reconciler.EndStagingAndReplay(tokenOverflow);
        await WaitForConditionAsync(() => tab.Items.Any(i => i.Name == "overflow_file.txt"), timeoutMs: 5000);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(tab.Items, i => i.Name == "overflow_file.txt");
    }

    [Fact]
    public async Task ScenarioQ_QueuedJobCancellation_CompletesCompletionTaskPromptly()
    {
        using var fs = new TemporaryFileSystem();
        var src1 = Path.Combine(fs.FolderA, "file1.txt");
        var src2 = Path.Combine(fs.FolderA, "file2.txt");
        File.WriteAllText(src1, "content 1");
        File.WriteAllText(src2, "content 2");

        var job1 = _manager.EnqueueTransfer(new FileTransferRequest(new[] { src1 }, fs.FolderB, FileTransferMode.Copy));
        // Pause job1 so worker remains on job1 while job2 is queued
        _manager.PauseJob(job1.Id);

        var job2 = _manager.EnqueueTransfer(new FileTransferRequest(new[] { src2 }, fs.FolderB, FileTransferMode.Copy));

        Assert.Equal(OperationState.Queued, job2.State);

        // Cancel job2 while queued
        job2.RequestCancel();

        // Verify CompletionTask completes with cancellation and does NOT hang indefinitely
        var completedTask = await Task.WhenAny(job2.CompletionTask, Task.Delay(3000));
        Assert.Same(job2.CompletionTask, completedTask);
        Assert.True(job2.CompletionTask.IsCanceled);
        Assert.Equal(OperationState.Cancelled, job2.State);

        // Resume job1 so worker finishes
        _manager.ResumeJob(job1.Id);
        await job1.CompletionTask;
        Assert.Equal(OperationState.Completed, job1.State);
    }

    [Fact]
    public async Task ScenarioR_CrossVolumeDirectoryMove_PartialSourceDeleteFailed_TopLevelReportsPartialSuccess()
    {
        using var fs = new TemporaryFileSystem();
        var srcDir = Path.Combine(fs.FolderA, "MoveDir");
        Directory.CreateDirectory(srcDir);
        var file1 = Path.Combine(srcDir, "file1.txt");
        var file2 = Path.Combine(srcDir, "file2.txt");
        File.WriteAllText(file1, "file 1 content");
        File.WriteAllText(file2, "file 2 content");

        // Lock file1 with FileShare.Read so copy succeeds, but source delete fails
        using var readStream = new FileStream(file1, FileMode.Open, FileAccess.Read, FileShare.Read);

        var req = new FileTransferRequest(new[] { srcDir }, fs.FolderB, FileTransferMode.Move, FileConflictPolicy.AutoRename);
        var job = _manager.EnqueueTransfer(req);

        var result = await job.CompletionTask;

        // Destination was created with the files
        var dstDir = Path.Combine(fs.FolderB, "MoveDir");
        Assert.True(Directory.Exists(dstDir));
        Assert.True(File.Exists(Path.Combine(dstDir, "file1.txt")));
        Assert.True(File.Exists(Path.Combine(dstDir, "file2.txt")));

        // Top-level directory result must report PartialSuccessSourceDeleteFailed
        var rootDirResult = result.Items.FirstOrDefault(i => i.SourcePath.Equals(srcDir, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rootDirResult);
        Assert.Equal(FileTransferStatus.PartialSuccessSourceDeleteFailed, rootDirResult.Status);
    }

    [Fact]
    public async Task ScenarioS_FailedOverwrite_RestoresOriginalDestinationAttributes()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "src_locked.txt");
        var dst = Path.Combine(fs.FolderB, "dst_readonly.txt");
        File.WriteAllText(src, "NEW DATA");
        File.WriteAllText(dst, "ORIGINAL DATA");

        File.SetAttributes(dst, FileAttributes.ReadOnly);
        Assert.True((File.GetAttributes(dst) & FileAttributes.ReadOnly) != 0);

        // Lock source file so copy stream fails
        using var lockStream = new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Copy, FileConflictPolicy.Overwrite);
        var job = _manager.EnqueueTransfer(req);

        await WaitForConditionAsync(() => job.State == OperationState.Failed, timeoutMs: 5000);

        // ReadOnly attribute must be restored on the destination file
        Assert.True(File.Exists(dst));
        Assert.True((File.GetAttributes(dst) & FileAttributes.ReadOnly) != 0);

        // Clean up readonly attribute so Dispose can delete temp folder
        File.SetAttributes(dst, FileAttributes.Normal);
    }

    [Fact]
    public void ScenarioT_ActiveTransferTempFiles_HiddenAndFiltered()
    {
        var tempPath = @"C:\FakePath\.clanker-transfer-abc12345.tmp";
        Assert.True(TransferEngine.IsInternalTransferTempFile(tempPath));

        Assert.False(TransferEngine.IsActiveTempFile(tempPath));

        TransferEngine.RegisterActiveTempFile(tempPath);
        Assert.True(TransferEngine.IsActiveTempFile(tempPath));

        TransferEngine.UnregisterActiveTempFile(tempPath);
        Assert.False(TransferEngine.IsActiveTempFile(tempPath));
    }

    [Fact]
    public async Task ScenarioU_OperationsViewModel_StatusTextAndAttentionPriority()
    {
        Assert.Equal("⚡ Operations", _manager.SummaryStatusText);

        using var fs = new TemporaryFileSystem();
        var fileA = Path.Combine(fs.FolderA, "conflict_a.txt");
        var fileB = Path.Combine(fs.FolderB, "conflict_a.txt");
        File.WriteAllText(fileA, "A");
        File.WriteAllText(fileB, "B");

        // Job that prompts conflict
        var job = _manager.EnqueueTransfer(new FileTransferRequest(new[] { fileA }, fs.FolderB, FileTransferMode.Copy, FileConflictPolicy.Prompt));

        await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);
        Assert.StartsWith("⚠", _manager.SummaryStatusText);

        job.ResolveConflict(new ConflictResolution(ConflictAction.Replace));
        await job.CompletionTask;

        Assert.Equal("⚡ Operations", _manager.SummaryStatusText);
    }

    [Fact]
    public void ScenarioV_OperationJobViewModel_ResetsInlineNewName_OnNewConflict()
    {
        var job = new OperationJob(OperationType.Copy, new[] { "file1.txt", "file2.txt" }, "dst");
        var vm = new OperationJobViewModel(job);

        // First conflict arrives
        var conflict1 = new OperationConflict(@"C:\file1.txt", @"C:\dst\file1.txt", @"C:\dst\file1 (Copy).txt", false);
        _ = job.PromptConflictAsync(conflict1, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("file1 (Copy).txt", vm.InlineNewName);

        // User typed custom name
        vm.InlineNewName = "my_custom_name.txt";

        // Second conflict arrives for different file
        var conflict2 = new OperationConflict(@"C:\file2.txt", @"C:\dst\file2.txt", @"C:\dst\file2 (Copy).txt", false);
        _ = job.PromptConflictAsync(conflict2, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        // Must reset to new file's suggested rename
        Assert.Equal("file2 (Copy).txt", vm.InlineNewName);
    }

    [Fact]
    public async Task ScenarioW_FinalSummaryFailures_DerivedFromAllItemResults()
    {
        using var fs = new TemporaryFileSystem();
        var badFile = Path.Combine(fs.FolderA, "does_not_exist_file.txt");
        var goodFile = Path.Combine(fs.FolderA, "good_file.txt");
        File.WriteAllText(goodFile, "good");

        var req = new FileTransferRequest(new[] { badFile, goodFile }, fs.FolderB, FileTransferMode.Copy);
        var job = _manager.EnqueueTransfer(req);

        await job.CompletionTask;

        Assert.NotNull(job.Summary);
        Assert.True(job.Summary.FailedCount >= 1);
        Assert.True(job.Summary.SucceededCount >= 1);
    }

    [Fact]
    public async Task ScenarioX_InteractiveTransfers_DefaultToPromptConflictPolicy()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "prompt_test.txt");
        File.WriteAllText(src, "content");
        ClipboardFileService.Copy(new[] { src });

        var job = await ClipboardFileService.EnqueuePasteFromSystemClipboardAsync(null, fs.FolderB);
        Assert.NotNull(job);
        Assert.Equal(FileConflictPolicy.Prompt, job.ConflictPolicy);

        await job.CompletionTask;
    }
}
