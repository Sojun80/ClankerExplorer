using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClankerExplorer.AppLayer;
using ClankerExplorer.AppLayer.Operations;
using ClankerExplorer.Models;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class OperationManagerTests : IDisposable
{
    private readonly OperationManager _manager = new();

    public void Dispose()
    {
        _manager.Dispose();
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs = 5000, int pollIntervalMs = 20)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(pollIntervalMs);
        }
        return condition();
    }

    [Fact]
    public async Task EnqueueTransfer_ChunkedCopy_TransfersContentAndUpdatesHistory()
    {
        using var fs = new TemporaryFileSystem();
        var sourceFile = Path.Combine(fs.FolderA, "large_source.bin");
        // 300 KB file (exceeds 128KB chunk size to test multiple chunk loops)
        var sourceBytes = new byte[300 * 1024];
        new Random(42).NextBytes(sourceBytes);
        File.WriteAllBytes(sourceFile, sourceBytes);

        var request = new FileTransferRequest(
            new[] { sourceFile },
            fs.FolderB,
            FileTransferMode.Copy);

        var job = _manager.EnqueueTransfer(request);

        Assert.NotNull(job);
        Assert.Equal(OperationType.Copy, job.Type);

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(completed, $"Job did not complete in time. Current state: {job.State}");

        var destFile = Path.Combine(fs.FolderB, "large_source.bin");
        Assert.True(File.Exists(destFile));
        Assert.Equal(sourceBytes, File.ReadAllBytes(destFile));
        Assert.Equal(sourceBytes.Length, job.Progress.TransferredBytes);
        Assert.Equal(100.0, job.Progress.Percentage);

        // Job should be in history now
        Assert.Contains(job, _manager.HistoryJobs);
        Assert.DoesNotContain(job, _manager.ActiveJobs);
    }

    [Fact]
    public async Task EnqueueTransfer_SameVolumeMove_PerformsFastAtomicMove()
    {
        using var fs = new TemporaryFileSystem();
        var sourceFile = Path.Combine(fs.FolderA, "move_target.txt");
        File.WriteAllText(sourceFile, "move content 123");

        var request = new FileTransferRequest(
            new[] { sourceFile },
            fs.FolderB,
            FileTransferMode.Move);

        var job = _manager.EnqueueTransfer(request);

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(completed, $"Job did not complete in time. State: {job.State}");

        var destFile = Path.Combine(fs.FolderB, "move_target.txt");
        Assert.True(File.Exists(destFile));
        Assert.False(File.Exists(sourceFile));
        Assert.Equal("move content 123", File.ReadAllText(destFile));
    }

    [Fact]
    public async Task Job_PauseAndResume_ResumesTransferToCompletion()
    {
        using var fs = new TemporaryFileSystem();
        var sourceFile = Path.Combine(fs.FolderA, "pause_resume.bin");
        // 2 MB file to provide plenty of time to pause during transfer
        var sourceBytes = new byte[2 * 1024 * 1024];
        new Random(99).NextBytes(sourceBytes);
        File.WriteAllBytes(sourceFile, sourceBytes);

        var request = new FileTransferRequest(
            new[] { sourceFile },
            fs.FolderB,
            FileTransferMode.Copy);

        var job = _manager.EnqueueTransfer(request);

        // Pause job
        job.RequestPause();
        Assert.Equal(OperationState.Paused, job.State);

        // Resume after short delay
        await Task.Delay(50);
        job.RequestResume();
        Assert.Equal(OperationState.Running, job.State);

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 7000);
        Assert.True(completed, $"Job did not complete. State: {job.State}");

        var destFile = Path.Combine(fs.FolderB, "pause_resume.bin");
        Assert.True(File.Exists(destFile));
        Assert.Equal(sourceBytes.Length, new FileInfo(destFile).Length);
    }

    [Fact]
    public async Task Job_Cancel_CleansUpPartialFileAndMarksCancelled()
    {
        using var fs = new TemporaryFileSystem();
        var sourceFile = Path.Combine(fs.FolderA, "cancel_test.bin");
        var sourceBytes = new byte[3 * 1024 * 1024];
        new Random(77).NextBytes(sourceBytes);
        File.WriteAllBytes(sourceFile, sourceBytes);

        var request = new FileTransferRequest(
            new[] { sourceFile },
            fs.FolderB,
            FileTransferMode.Copy);

        var job = _manager.EnqueueTransfer(request);

        // Cancel job
        job.RequestCancel();

        bool isDone = await WaitForConditionAsync(() =>
            job.State is OperationState.Cancelled or OperationState.Completed, timeoutMs: 5000);

        Assert.True(isDone);
        Assert.Equal(OperationState.Cancelled, job.State);
    }

    [Fact]
    public async Task Conflict_Replace_OverwritesExistingFile()
    {
        using var fs = new TemporaryFileSystem();
        var sourceFile = Path.Combine(fs.FolderA, "conflict.txt");
        var destFile = Path.Combine(fs.FolderB, "conflict.txt");

        File.WriteAllText(sourceFile, "NEW CONTENT");
        File.WriteAllText(destFile, "OLD CONTENT");

        var request = new FileTransferRequest(
            new[] { sourceFile },
            fs.FolderB,
            FileTransferMode.Copy);

        var job = _manager.EnqueueTransfer(request);

        // Should pause and signal NeedsAttention
        bool needsAttention = await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);
        Assert.True(needsAttention, $"Job should pause for conflict attention. State: {job.State}");
        Assert.NotNull(job.CurrentConflict);
        Assert.Equal("conflict.txt", Path.GetFileName(job.CurrentConflict.DestinationPath));
        Assert.Equal(1, _manager.NeedsAttentionCount);

        // Resolve by Replace
        job.ResolveConflict(new ConflictResolution(ConflictAction.Replace));

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(completed, $"Job should complete after resolution. State: {job.State}");
        Assert.Equal(0, _manager.NeedsAttentionCount);
        Assert.Equal("NEW CONTENT", File.ReadAllText(destFile));
    }

    [Fact]
    public async Task Conflict_Skip_PreservesExistingDestinationFile()
    {
        using var fs = new TemporaryFileSystem();
        var sourceFile = Path.Combine(fs.FolderA, "conflict_skip.txt");
        var destFile = Path.Combine(fs.FolderB, "conflict_skip.txt");

        File.WriteAllText(sourceFile, "NEW SOURCE CONTENT");
        File.WriteAllText(destFile, "ORIGINAL DEST CONTENT");

        var request = new FileTransferRequest(
            new[] { sourceFile },
            fs.FolderB,
            FileTransferMode.Copy);

        var job = _manager.EnqueueTransfer(request);

        bool needsAttention = await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);
        Assert.True(needsAttention);

        // Resolve by Skip
        job.ResolveConflict(new ConflictResolution(ConflictAction.Skip));

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(completed);
        Assert.Equal("ORIGINAL DEST CONTENT", File.ReadAllText(destFile));
    }

    [Fact]
    public async Task Conflict_KeepBoth_CreatesIncrementedFilename()
    {
        using var fs = new TemporaryFileSystem();
        var sourceFile = Path.Combine(fs.FolderA, "keep_both.txt");
        var destFile = Path.Combine(fs.FolderB, "keep_both.txt");

        File.WriteAllText(sourceFile, "INCOMING FILE");
        File.WriteAllText(destFile, "EXISTING FILE");

        var request = new FileTransferRequest(
            new[] { sourceFile },
            fs.FolderB,
            FileTransferMode.Copy);

        var job = _manager.EnqueueTransfer(request);

        bool needsAttention = await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);
        Assert.True(needsAttention);

        // Resolve by KeepBoth
        job.ResolveConflict(new ConflictResolution(ConflictAction.KeepBoth));

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(completed);

        // Original file intact
        Assert.Equal("EXISTING FILE", File.ReadAllText(destFile));

        // Numbered/copy file created
        var expectedNumbered = Path.Combine(fs.FolderB, "keep_both (Copy).txt");
        Assert.True(File.Exists(expectedNumbered), $"Expected {expectedNumbered} to exist");
        Assert.Equal("INCOMING FILE", File.ReadAllText(expectedNumbered));
    }

    [Fact]
    public async Task Conflict_Rename_WritesUnderCustomName()
    {
        using var fs = new TemporaryFileSystem();
        var sourceFile = Path.Combine(fs.FolderA, "rename_source.txt");
        var destFile = Path.Combine(fs.FolderB, "rename_source.txt");

        File.WriteAllText(sourceFile, "CONTENT TO RENAME");
        File.WriteAllText(destFile, "EXISTING BEFORE RENAME");

        var request = new FileTransferRequest(
            new[] { sourceFile },
            fs.FolderB,
            FileTransferMode.Copy);

        var job = _manager.EnqueueTransfer(request);

        bool needsAttention = await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);
        Assert.True(needsAttention);

        // Resolve by Rename
        job.ResolveConflict(new ConflictResolution(ConflictAction.Rename, CustomNewName: "custom_target.txt"));

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(completed);

        var renamedDest = Path.Combine(fs.FolderB, "custom_target.txt");
        Assert.True(File.Exists(renamedDest));
        Assert.Equal("CONTENT TO RENAME", File.ReadAllText(renamedDest));
        Assert.Equal("EXISTING BEFORE RENAME", File.ReadAllText(destFile));
    }

    [Fact]
    public async Task Conflict_ApplyToRemaining_AppliesResolutionToSubsequentConflicts()
    {
        using var fs = new TemporaryFileSystem();
        var src1 = Path.Combine(fs.FolderA, "f1.txt");
        var src2 = Path.Combine(fs.FolderA, "f2.txt");
        var dst1 = Path.Combine(fs.FolderB, "f1.txt");
        var dst2 = Path.Combine(fs.FolderB, "f2.txt");

        File.WriteAllText(src1, "NEW 1");
        File.WriteAllText(src2, "NEW 2");
        File.WriteAllText(dst1, "OLD 1");
        File.WriteAllText(dst2, "OLD 2");

        var request = new FileTransferRequest(
            new[] { src1, src2 },
            fs.FolderB,
            FileTransferMode.Copy);

        var job = _manager.EnqueueTransfer(request);

        bool needsAttention = await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);
        Assert.True(needsAttention);

        // Resolve first with ApplyToAllRemaining = true and Skip
        job.ResolveConflict(new ConflictResolution(ConflictAction.Skip, ApplyToAllRemaining: true));

        bool completed = await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.True(completed);

        // Both destinations should have their original content preserved
        Assert.Equal("OLD 1", File.ReadAllText(dst1));
        Assert.Equal("OLD 2", File.ReadAllText(dst2));
    }

    [Fact]
    public async Task ErrorResilience_FailedJobDoesNotCrashQueueOrPreventNextJob()
    {
        using var fs = new TemporaryFileSystem();
        var missingSource = Path.Combine(fs.FolderA, "does_not_exist.txt");
        var validSource = Path.Combine(fs.FolderA, "valid_file.txt");
        File.WriteAllText(validSource, "valid content");

        // Enqueue 2 jobs: first fails, second should succeed
        var req1 = new FileTransferRequest(new[] { missingSource }, fs.FolderB, FileTransferMode.Copy);
        var req2 = new FileTransferRequest(new[] { validSource }, fs.FolderB, FileTransferMode.Copy);

        var job1 = _manager.EnqueueTransfer(req1);
        var job2 = _manager.EnqueueTransfer(req2);

        bool job2Done = await WaitForConditionAsync(() => job2.State == OperationState.Completed, timeoutMs: 6000);
        Assert.True(job2Done, $"Job 2 should complete successfully. State: {job2.State}");

        Assert.Equal(OperationState.Failed, job1.State);
        Assert.NotEmpty(job1.Errors);
        Assert.True(File.Exists(Path.Combine(fs.FolderB, "valid_file.txt")));
    }

    [Fact]
    public async Task HistoryManagement_ClearCompletedRemovesJobs()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "history_test.txt");
        File.WriteAllText(src, "history test");

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Copy);
        var job = _manager.EnqueueTransfer(req);
        await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);

        Assert.Contains(job, _manager.HistoryJobs);

        // Clear completed
        _manager.ClearCompleted();
        Assert.Empty(_manager.HistoryJobs);
    }

    [Fact]
    public async Task OperationsViewModel_WiresCommandsAndResolutions()
    {
        using var fs = new TemporaryFileSystem();
        var src = Path.Combine(fs.FolderA, "vm_conflict.txt");
        var dst = Path.Combine(fs.FolderB, "vm_conflict.txt");
        File.WriteAllText(src, "VM NEW");
        File.WriteAllText(dst, "VM OLD");

        var vm = new OperationsViewModel(_manager);
        Assert.NotNull(vm.ActiveJobs);
        Assert.NotNull(vm.HistoryJobs);

        var req = new FileTransferRequest(new[] { src }, fs.FolderB, FileTransferMode.Copy);
        var job = _manager.EnqueueTransfer(req);
        await WaitForConditionAsync(() => job.State == OperationState.NeedsAttention, timeoutMs: 5000);

        var jobVm = new OperationJobViewModel(job);
        jobVm.ResolveReplace();

        await WaitForConditionAsync(() => job.State == OperationState.Completed, timeoutMs: 5000);
        Assert.Equal("VM NEW", File.ReadAllText(dst));
    }
}
