using ClankerExplorer.AppLayer;
using ClankerExplorer.AppLayer.Operations;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.Services;

namespace ClankerExplorer.Tests;

public sealed class OverwriteRecycleTests
{
    [Theory]
    [InlineData(FileTransferMode.Copy, false)]
    [InlineData(FileTransferMode.Move, false)]
    [InlineData(FileTransferMode.Copy, true)]
    public async Task Overwrite_PreservesPreviousContents(FileTransferMode mode, bool nested)
    {
        using var fs = new TemporaryFileSystem();
        string relative = nested ? "versions/item.txt" : "item.txt";
        string source = fs.CreateFile("FolderA/" + relative, "new contents");
        string target = fs.CreateFile("FolderB/" + relative, "old contents");
        using var manager = new OperationManager();
        var service = new FileOperationService(operationManager: manager);
        string sourceItem = nested ? Path.GetDirectoryName(source)! : source;
        var job = manager.EnqueueTransfer(new FileTransferRequest(
            new[] { sourceItem }, fs.FolderB, mode, FileConflictPolicy.Overwrite));
        var result = await job.CompletionTask;

        Assert.True(result.Succeeded);
        Assert.Equal("new contents", File.ReadAllText(target));
        string bin = Path.Combine(Path.GetDirectoryName(target)!, ".clanker-recycle");
        Assert.True(Directory.Exists(bin));
        string recycled = Assert.Single(Directory.GetFiles(bin, "*.txt"));
        Assert.Equal("old contents", File.ReadAllText(recycled));
        Assert.Matches(@"^item_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(-\d+)?\.txt$", Path.GetFileName(recycled));
        Assert.Equal(mode == FileTransferMode.Copy, File.Exists(source));
        var entry = RecycleBinService.Instance.GetEntry(recycled)!;
        Assert.Contains(job.Events, e => e.Message.Contains(entry.OriginalPath) &&
            e.Message.Contains(recycled) && e.Message.Contains(Path.GetFullPath(source)) &&
            e.Message.Contains(entry.RecycledAt.ToString("O")));
    }

    [Fact]
    public async Task Overwrite_WhenRecyclingIsUnavailable_LeavesOriginalIntact()
    {
        using var fs = new TemporaryFileSystem();
        string source = fs.CreateFile("FolderA/item.txt", "new contents");
        string target = fs.CreateFile("FolderB/item.txt", "old contents");
        fs.CreateFile("FolderB/.clanker-recycle", "blocks recycle directory");
        using var manager = new OperationManager();
        var service = new FileOperationService(operationManager: manager);

        var result = await service.TransferAsync(new FileTransferRequest(
            new[] { source }, fs.FolderB, FileTransferMode.Copy, FileConflictPolicy.Overwrite));

        Assert.False(result.Succeeded);
        Assert.Equal("old contents", File.ReadAllText(target));
        Assert.Equal("new contents", File.ReadAllText(source));
    }
    [Fact]
    public async Task BatchOverwrite_OneRecycleFailureDoesNotLoseOtherVersions()
    {
        using var fs = new TemporaryFileSystem();
        string blockedSource = fs.CreateFile("FolderA/blocked/item.txt", "new blocked");
        string blockedTarget = fs.CreateFile("FolderB/blocked/item.txt", "old blocked");
        fs.CreateFile("FolderB/blocked/.clanker-recycle", "blocker");
        string goodSource = fs.CreateFile("FolderA/good/item.txt", "new good");
        string goodTarget = fs.CreateFile("FolderB/good/item.txt", "old good");
        using var manager = new OperationManager();
        var job = manager.EnqueueTransfer(new FileTransferRequest(
            new[] { Path.GetDirectoryName(blockedSource)!, Path.GetDirectoryName(goodSource)! },
            fs.FolderB, FileTransferMode.Copy, FileConflictPolicy.Overwrite));

        var result = await job.CompletionTask;

        Assert.False(result.Succeeded);
        Assert.Equal("old blocked", File.ReadAllText(blockedTarget));
        Assert.Equal("new good", File.ReadAllText(goodTarget));
        string saved = Assert.Single(Directory.GetFiles(RecycleBinService.GetBinDirectory(Path.GetDirectoryName(goodTarget)!), "*.txt"));
        Assert.Equal("old good", File.ReadAllText(saved));
    }

}
