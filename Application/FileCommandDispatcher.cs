namespace ClankerExplorer.AppLayer;

public sealed class FileCommandDispatcher : IFileCommandDispatcher
{
    private readonly IFileOperationService _fileOperations;

    public FileCommandDispatcher(IFileOperationService? fileOperations = null)
    {
        _fileOperations = fileOperations ?? new FileOperationService();
    }

    public async Task<FileCommandResult> ExecuteAsync(
        FileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            return command switch
            {
                TransferFilesCommand transfer => await ExecuteTransferAsync(transfer, cancellationToken).ConfigureAwait(false),
                CreateItemCommand create => await ExecuteCreateAsync(create, cancellationToken).ConfigureAwait(false),
                RenameItemCommand rename => await ExecuteRenameAsync(rename, cancellationToken).ConfigureAwait(false),
                DeleteItemsCommand delete => await ExecuteDeleteAsync(delete, cancellationToken).ConfigureAwait(false),
                ExtractArchiveCommand extract => await ExecuteExtractAsync(extract, cancellationToken).ConfigureAwait(false),
                CreateZipCommand createZip => await ExecuteCreateZipAsync(createZip, cancellationToken).ConfigureAwait(false),
                BatchRenameCommand batchRename => await ExecuteBatchRenameAsync(batchRename, cancellationToken).ConfigureAwait(false),
                _ => new FileCommandResult(false, ErrorMessage: $"Unsupported file command: {command.GetType().Name}.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FileCommandResult(false, ErrorMessage: ex.Message);
        }
    }

    private async Task<FileCommandResult> ExecuteTransferAsync(
        TransferFilesCommand command,
        CancellationToken cancellationToken)
    {
        var job = _fileOperations.QueueTransfer(command.Request);
        using var reg = cancellationToken.Register(() => job.RequestCancel());
        var result = await job.CompletionTask.ConfigureAwait(false);
        return new FileCommandResult(result.Succeeded, Transfer: result);
    }

    private async Task<FileCommandResult> ExecuteCreateAsync(
        CreateItemCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileOperations.CreateAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return new FileCommandResult(result.Succeeded, Operation: result);
    }

    private async Task<FileCommandResult> ExecuteRenameAsync(
        RenameItemCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileOperations.RenameAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return new FileCommandResult(result.Succeeded, Operation: result);
    }

    private async Task<FileCommandResult> ExecuteDeleteAsync(
        DeleteItemsCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileOperations.DeleteAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return new FileCommandResult(result.Succeeded, Operation: result);
    }

    private async Task<FileCommandResult> ExecuteExtractAsync(
        ExtractArchiveCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileOperations.ExtractAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return new FileCommandResult(result.Succeeded, Archive: result);
    }

    private async Task<FileCommandResult> ExecuteCreateZipAsync(
        CreateZipCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileOperations.CreateZipAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return new FileCommandResult(result.Succeeded, Archive: result);
    }

    private async Task<FileCommandResult> ExecuteBatchRenameAsync(
        BatchRenameCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _fileOperations.BatchRenameAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return new FileCommandResult(
            result.Succeeded,
            BatchRename: result,
            ErrorMessage: result.Succeeded ? null : result.Message);
    }
}
