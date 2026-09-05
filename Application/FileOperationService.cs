using System.Collections.Generic;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.AppLayer;

public sealed class FileOperationService : IFileOperationService
{
    private readonly ArchiveService _archiveService;
    public ClankerExplorer.AppLayer.Operations.IOperationManager Operations { get; }

    public FileOperationService(
        ArchiveService? archiveService = null,
        ClankerExplorer.AppLayer.Operations.IOperationManager? operationManager = null)
    {
        _archiveService = archiveService ?? ArchiveService.Instance;
        Operations = operationManager ?? ClankerExplorer.AppLayer.Operations.OperationManager.Instance;
    }

    public ClankerExplorer.AppLayer.Operations.OperationJob QueueTransfer(FileTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Operations.EnqueueTransfer(request);
    }

    public async Task<FileTransferResult> TransferAsync(
        FileTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sources = request.SourcePaths ?? Array.Empty<string>();
        if (sources.Count == 0)
        {
            return new FileTransferResult(Array.Empty<FileTransferItemResult>());
        }

        var normalizedRequest = request with { SourcePaths = sources };
        var job = QueueTransfer(normalizedRequest);
        using var reg = cancellationToken.Register(() => job.RequestCancel());
        return await job.CompletionTask.ConfigureAwait(false);
    }

    public async Task<FileOperationResult> CreateAsync(
        CreateItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var resultPath = Path.Combine(request.ParentPath, request.Name);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.IsDirectory)
                {
                    FileSystemService.Instance.CreateFolder(request.ParentPath, request.Name);
                }
                else
                {
                    FileSystemService.Instance.CreateFile(request.ParentPath, request.Name);
                }
            }, cancellationToken).ConfigureAwait(false);

            return SuccessfulOperation(request.Name, resultPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FailedOperation(request.Name, ex.Message);
        }
    }

    public async Task<FileOperationResult> RenameAsync(
        RenameItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var parentPath = Path.GetDirectoryName(request.SourcePath) ?? string.Empty;
            var resultPath = Path.Combine(parentPath, request.NewName);
            await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileSystemService.Instance.Rename(request.SourcePath, request.NewName);
                },
                cancellationToken).ConfigureAwait(false);

            return SuccessfulOperation(request.SourcePath, resultPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FailedOperation(request.SourcePath, ex.Message);
        }
    }

    public async Task<FileOperationResult> DeleteAsync(
        DeleteItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = new List<FileOperationItemResult>(request.Paths?.Count ?? 0);
        foreach (var path in request.Paths ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                results.Add(new FileOperationItemResult(
                    path,
                    null,
                    FileOperationStatus.Failed,
                    "The source path is empty."));
                continue;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                results.Add(new FileOperationItemResult(
                    path,
                    null,
                    FileOperationStatus.Failed,
                    "The source path does not exist."));
                continue;
            }

            try
            {
                await FileSystemService.Instance.DeleteAsync(
                    new[] { path },
                    request.Permanent,
                    cancellationToken).ConfigureAwait(false);
                results.Add(new FileOperationItemResult(path, null, FileOperationStatus.Succeeded));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new FileOperationItemResult(path, null, FileOperationStatus.Failed, ex.Message));
            }
        }

        return new FileOperationResult(results);
    }

    public async Task<ArchiveOperationResult> ExtractAsync(
        ArchiveExtractRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var result = await _archiveService.ExtractToAsync(
                request.ArchivePath,
                request.DestinationDirectory,
                request.Overwrite,
                cancellationToken).ConfigureAwait(false);
            return new ArchiveOperationResult(result.success, result.message, result.success ? request.DestinationDirectory : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ArchiveOperationResult(false, ex.Message);
        }
    }

    public async Task<ArchiveOperationResult> CreateZipAsync(
        ArchiveCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var result = await _archiveService.CreateZipAsync(
                request.SourcePath,
                request.DestinationZipPath,
                cancellationToken).ConfigureAwait(false);
            return new ArchiveOperationResult(result.success, result.message, result.targetPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ArchiveOperationResult(false, ex.Message, request.DestinationZipPath);
        }
    }

    public IReadOnlyList<BatchRenameItem> PreviewBatchRename(BatchRenamePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FileSystemService.Instance.PreviewBatchRename(request.TargetPaths, request.Rule);
    }

    public async Task<BatchRenameOperationResult> BatchRenameAsync(
        BatchRenameRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var result = await Task.Run(
                () => FileSystemService.Instance.ExecuteBatchRenameSafe(request.Items),
                cancellationToken).ConfigureAwait(false);
            return new BatchRenameOperationResult(result.success, result.message, result.renamedCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BatchRenameOperationResult(false, ex.Message, 0);
        }
    }

    private static FileOperationResult SuccessfulOperation(string sourcePath, string resultPath) =>
        new(new[]
        {
            new FileOperationItemResult(sourcePath, resultPath, FileOperationStatus.Succeeded)
        });

    private static FileOperationResult FailedOperation(string sourcePath, string message) =>
        new(new[]
        {
            new FileOperationItemResult(sourcePath, null, FileOperationStatus.Failed, message)
        });
}

