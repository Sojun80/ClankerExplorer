using System.Collections.Generic;
using ClankerExplorer.Models;

namespace ClankerExplorer.AppLayer;

public enum FileTransferMode
{
    Copy,
    Move
}

public enum FileConflictPolicy
{
    AutoRename,
    Fail,
    Skip,
    Overwrite
}

public enum FileTransferStatus
{
    Succeeded,
    Failed,
    Skipped
}

public enum FileOperationStatus
{
    Succeeded,
    Failed
}

public sealed record FileTransferRequest(
    IReadOnlyList<string> SourcePaths,
    string DestinationDirectory,
    FileTransferMode Mode,
    FileConflictPolicy ConflictPolicy = FileConflictPolicy.AutoRename);

public sealed record FileTransferItemResult(
    string SourcePath,
    string? DestinationPath,
    FileTransferStatus Status,
    string? ErrorMessage = null);

public sealed record FileTransferResult(IReadOnlyList<FileTransferItemResult> Items)
{
    public bool Succeeded => Items.Count > 0 && Items.All(item => item.Status == FileTransferStatus.Succeeded);

    public IReadOnlyList<string> SuccessfulSourcePaths =>
        Items.Where(item => item.Status == FileTransferStatus.Succeeded)
            .Select(item => item.SourcePath)
            .ToArray();

    public IReadOnlyList<string> FailedPaths =>
        Items.Where(item => item.Status == FileTransferStatus.Failed)
            .Select(item => item.SourcePath)
            .ToArray();

    public IReadOnlyList<string> CreatedDestinationPaths =>
        Items.Where(item => item.Status == FileTransferStatus.Succeeded && item.DestinationPath != null)
            .Select(item => item.DestinationPath!)
            .ToArray();
}

public sealed record CreateItemRequest(string ParentPath, string Name, bool IsDirectory);

public sealed record RenameItemRequest(string SourcePath, string NewName);

public sealed record DeleteItemsRequest(IReadOnlyList<string> Paths, bool Permanent);

public sealed record FileOperationItemResult(
    string? SourcePath,
    string? ResultPath,
    FileOperationStatus Status,
    string? ErrorMessage = null);

public sealed record FileOperationResult(IReadOnlyList<FileOperationItemResult> Items)
{
    public bool Succeeded => Items.All(item => item.Status == FileOperationStatus.Succeeded);
}

public sealed record ArchiveExtractRequest(
    string ArchivePath,
    string DestinationDirectory,
    bool Overwrite = false);

public sealed record ArchiveCreateRequest(
    string SourcePath,
    string? DestinationZipPath = null);

public sealed record ArchiveOperationResult(
    bool Succeeded,
    string Message,
    string? DestinationPath = null);

public sealed record BatchRenamePreviewRequest(
    IReadOnlyList<string> TargetPaths,
    BatchRenameRule Rule);

public sealed record BatchRenameRequest(IReadOnlyList<BatchRenameItem> Items);

public sealed record BatchRenameOperationResult(
    bool Succeeded,
    string Message,
    int RenamedCount);

public abstract record FileCommand;

public sealed record TransferFilesCommand(FileTransferRequest Request) : FileCommand;

public sealed record CreateItemCommand(CreateItemRequest Request) : FileCommand;

public sealed record RenameItemCommand(RenameItemRequest Request) : FileCommand;

public sealed record DeleteItemsCommand(DeleteItemsRequest Request) : FileCommand;

public sealed record ExtractArchiveCommand(ArchiveExtractRequest Request) : FileCommand;

public sealed record CreateZipCommand(ArchiveCreateRequest Request) : FileCommand;

public sealed record BatchRenameCommand(BatchRenameRequest Request) : FileCommand;

public sealed record FileCommandResult(
    bool Succeeded,
    FileTransferResult? Transfer = null,
    FileOperationResult? Operation = null,
    ArchiveOperationResult? Archive = null,
    BatchRenameOperationResult? BatchRename = null,
    string? ErrorMessage = null);

public interface IFileCommandDispatcher
{
    Task<FileCommandResult> ExecuteAsync(
        FileCommand command,
        CancellationToken cancellationToken = default);
}

public interface IFileOperationService
{
    Task<FileTransferResult> TransferAsync(
        FileTransferRequest request,
        CancellationToken cancellationToken = default);

    Task<FileOperationResult> CreateAsync(
        CreateItemRequest request,
        CancellationToken cancellationToken = default);

    Task<FileOperationResult> RenameAsync(
        RenameItemRequest request,
        CancellationToken cancellationToken = default);

    Task<FileOperationResult> DeleteAsync(
        DeleteItemsRequest request,
        CancellationToken cancellationToken = default);

    Task<ArchiveOperationResult> ExtractAsync(
        ArchiveExtractRequest request,
        CancellationToken cancellationToken = default);

    Task<ArchiveOperationResult> CreateZipAsync(
        ArchiveCreateRequest request,
        CancellationToken cancellationToken = default);

    IReadOnlyList<BatchRenameItem> PreviewBatchRename(BatchRenamePreviewRequest request);

    Task<BatchRenameOperationResult> BatchRenameAsync(
        BatchRenameRequest request,
        CancellationToken cancellationToken = default);
}
