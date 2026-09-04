using ClankerExplorer.AppLayer;
using ClankerExplorer.Models;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Tests;

public sealed class FileOperationServiceTests
{
    private readonly IFileOperationService _service = new FileOperationService();

    [Fact]
    public async Task TransferAsync_ReturnsTypedResultsAndCreatedPaths()
    {
        using var fs = new TemporaryFileSystem();
        var source = Path.Combine(fs.FolderA, "alpha.txt");

        var result = await _service.TransferAsync(new FileTransferRequest(
            new[] { source },
            fs.FolderB,
            FileTransferMode.Copy,
            FileConflictPolicy.AutoRename));

        var item = Assert.Single(result.Items);
        Assert.Equal(FileTransferStatus.Succeeded, item.Status);
        Assert.Equal(source, item.SourcePath);
        Assert.Equal(Path.Combine(fs.FolderB, "alpha.txt"), item.DestinationPath);
        Assert.Equal("alpha", File.ReadAllText(item.DestinationPath!));
    }

    [Fact]
    public async Task TransferAsync_ReportsMissingSourceWithoutThrowing()
    {
        using var fs = new TemporaryFileSystem();
        var missing = Path.Combine(fs.Root, "missing.txt");

        var result = await _service.TransferAsync(new FileTransferRequest(
            new[] { missing },
            fs.FolderB,
            FileTransferMode.Copy,
            FileConflictPolicy.AutoRename));

        var item = Assert.Single(result.Items);
        Assert.Equal(FileTransferStatus.Failed, item.Status);
        Assert.Equal(missing, item.SourcePath);
        Assert.Contains("does not exist", item.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplorerPaneDrop_UsesInjectedApplicationService()
    {
        using var fs = new TemporaryFileSystem();
        var operationService = new RecordingFileOperationService();
        var source = Path.Combine(fs.FolderA, "alpha.txt");
        using var pane = new ExplorerPaneViewModel(
            "left",
            fs.FolderB,
            fileOperationService: operationService);
        await pane.SelectedTab!.RefreshAsync();

        await pane.ExecuteDropAsync(new[] { source }, fs.FolderB, isMove: false);

        Assert.NotNull(operationService.Request);
        Assert.Equal(FileTransferMode.Copy, operationService.Request!.Mode);
        Assert.Equal(fs.FolderB, operationService.Request.DestinationDirectory);
        Assert.Equal(new[] { source }, operationService.Request.SourcePaths);
    }

    [Fact]
    public async Task CreateAndRenameAsync_UseApplicationResults()
    {
        using var fs = new TemporaryFileSystem();

        var create = await _service.CreateAsync(new CreateItemRequest(
            fs.FolderB,
            "created.txt",
            IsDirectory: false));
        Assert.True(create.Succeeded);

        var createdPath = Path.Combine(fs.FolderB, "created.txt");
        var rename = await _service.RenameAsync(new RenameItemRequest(createdPath, "renamed.txt"));

        Assert.True(rename.Succeeded);
        Assert.True(File.Exists(Path.Combine(fs.FolderB, "renamed.txt")));
    }

    [Fact]
    public async Task DeleteAsync_ReportsPerItemResults()
    {
        using var fs = new TemporaryFileSystem();
        var existing = fs.CreateFile("FolderB/remove.txt");
        var missing = Path.Combine(fs.FolderB, "missing.txt");

        var result = await _service.DeleteAsync(new DeleteItemsRequest(
            new[] { existing, missing },
            Permanent: true));

        Assert.Equal(FileOperationStatus.Succeeded, result.Items[0].Status);
        Assert.Equal(FileOperationStatus.Failed, result.Items[1].Status);
        Assert.False(File.Exists(existing));
    }

    [Fact]
    public async Task CreateZipAndExtractAsync_RoundTripsAFile()
    {
        using var fs = new TemporaryFileSystem();
        var source = Path.Combine(fs.FolderA, "alpha.txt");
        var zipPath = Path.Combine(fs.FolderB, "alpha.zip");
        var destination = fs.CreateDirectory("FolderB/extracted");

        var create = await _service.CreateZipAsync(new ArchiveCreateRequest(source, zipPath));
        Assert.True(create.Succeeded, create.Message);
        Assert.True(File.Exists(zipPath));

        var extract = await _service.ExtractAsync(new ArchiveExtractRequest(zipPath, destination));

        Assert.True(extract.Succeeded, extract.Message);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(destination, "alpha.txt")));
    }

    [Fact]
    public async Task CommandDispatcher_ExecutesTypedTransferCommandWithoutUi()
    {
        using var fs = new TemporaryFileSystem();
        var source = Path.Combine(fs.FolderA, "alpha.txt");
        var dispatcher = new FileCommandDispatcher(_service);

        var result = await dispatcher.ExecuteAsync(new TransferFilesCommand(new FileTransferRequest(
            new[] { source },
            fs.FolderB,
            FileTransferMode.Copy)));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Transfer);
        Assert.Equal(Path.Combine(fs.FolderB, "alpha.txt"), result.Transfer!.CreatedDestinationPaths.Single());
    }

    [Fact]
    public async Task BatchRenameAsync_PreservesPreviewAndExecutesThroughApplicationService()
    {
        using var fs = new TemporaryFileSystem();
        var source = Path.Combine(fs.FolderA, "alpha.txt");
        var rule = new BatchRenameRule
        {
            Mode = "prefix_suffix",
            Prefix = "renamed-"
        };

        var preview = _service.PreviewBatchRename(new BatchRenamePreviewRequest(new[] { source }, rule));
        var dispatcher = new FileCommandDispatcher(_service);
        var commandResult = await dispatcher.ExecuteAsync(new BatchRenameCommand(new BatchRenameRequest(preview)));

        Assert.True(commandResult.Succeeded, commandResult.ErrorMessage);
        Assert.Equal(1, commandResult.BatchRename!.RenamedCount);
        Assert.True(File.Exists(Path.Combine(fs.FolderA, "renamed-alpha.txt")));
    }

    private sealed class RecordingFileOperationService : IFileOperationService
    {
        public FileTransferRequest? Request { get; private set; }

        public ClankerExplorer.AppLayer.Operations.OperationJob QueueTransfer(FileTransferRequest request)
        {
            Request = request;
            return ClankerExplorer.AppLayer.Operations.OperationManager.Instance.EnqueueTransfer(request);
        }

        public Task<FileTransferResult> TransferAsync(
            FileTransferRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var destination = Path.Combine(request.DestinationDirectory, Path.GetFileName(request.SourcePaths[0]));
            return Task.FromResult(new FileTransferResult(new[]
            {
                new FileTransferItemResult(
                    request.SourcePaths[0],
                    destination,
                    FileTransferStatus.Succeeded)
            }));
        }

        public Task<FileOperationResult> CreateAsync(
            CreateItemRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileOperationResult> RenameAsync(
            RenameItemRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileOperationResult> DeleteAsync(
            DeleteItemsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ArchiveOperationResult> ExtractAsync(
            ArchiveExtractRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ArchiveOperationResult> CreateZipAsync(
            ArchiveCreateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<BatchRenameItem> PreviewBatchRename(BatchRenamePreviewRequest request) =>
            throw new NotSupportedException();

        public Task<BatchRenameOperationResult> BatchRenameAsync(
            BatchRenameRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
