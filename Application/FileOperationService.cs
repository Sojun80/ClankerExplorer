using System.Collections.Generic;
using System.Diagnostics;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.AppLayer;

public sealed class FileOperationService : IFileOperationService
{
    private readonly ArchiveService _archiveService;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public FileOperationService(ArchiveService? archiveService = null)
    {
        _archiveService = archiveService ?? ArchiveService.Instance;
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

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory) ||
            !Directory.Exists(request.DestinationDirectory))
        {
            return new FileTransferResult(
                sources.Select(source => new FileTransferItemResult(
                    source,
                    null,
                    FileTransferStatus.Failed,
                    "The destination directory does not exist.")).ToArray());
        }

        var normalizedRequest = request with { SourcePaths = sources };
        return await Task.Run(
            () => TransferCore(normalizedRequest, cancellationToken),
            cancellationToken).ConfigureAwait(false);
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

    private static FileTransferResult TransferCore(
        FileTransferRequest request,
        CancellationToken cancellationToken)
    {
        var results = new List<FileTransferItemResult>(request.SourcePaths.Count);

        foreach (var source in request.SourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(source))
            {
                results.Add(new FileTransferItemResult(
                    source ?? string.Empty,
                    null,
                    FileTransferStatus.Failed,
                    "The source path is empty."));
                continue;
            }

            try
            {
                if (File.Exists(source))
                {
                    results.Add(TransferFile(source, request));
                }
                else if (Directory.Exists(source))
                {
                    results.Add(TransferDirectory(source, request, cancellationToken));
                }
                else
                {
                    results.Add(new FileTransferItemResult(
                        source,
                        null,
                        FileTransferStatus.Failed,
                        "The source path does not exist."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to transfer {source}: {ex.Message}");
                results.Add(new FileTransferItemResult(
                    source,
                    null,
                    FileTransferStatus.Failed,
                    ex.Message));
            }
        }

        return new FileTransferResult(results);
    }

    private static FileTransferItemResult TransferFile(
        string source,
        FileTransferRequest request)
    {
        var target = Path.Combine(request.DestinationDirectory, Path.GetFileName(source));
        if (request.Mode == FileTransferMode.Move && PathsEqual(source, target))
        {
            return new FileTransferItemResult(source, target, FileTransferStatus.Succeeded);
        }

        var resolution = ResolveDestination(target, request.ConflictPolicy);
        if (resolution.Status != null)
        {
            return new FileTransferItemResult(source, null, resolution.Status.Value, resolution.ErrorMessage);
        }

        target = resolution.Path!;
        if (request.Mode == FileTransferMode.Move)
        {
            File.Move(source, target, request.ConflictPolicy == FileConflictPolicy.Overwrite);
        }
        else
        {
            File.Copy(source, target, request.ConflictPolicy == FileConflictPolicy.Overwrite);
        }

        return new FileTransferItemResult(source, target, FileTransferStatus.Succeeded);
    }

    private static FileTransferItemResult TransferDirectory(
        string source,
        FileTransferRequest request,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(
            request.DestinationDirectory,
            Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

        if (request.Mode == FileTransferMode.Move && PathsEqual(source, target))
        {
            return new FileTransferItemResult(source, target, FileTransferStatus.Succeeded);
        }

        if (IsDescendantOf(target, source))
        {
            return new FileTransferItemResult(
                source,
                null,
                FileTransferStatus.Failed,
                "A directory cannot be transferred into one of its own descendants.");
        }

        var resolution = ResolveDestination(target, request.ConflictPolicy);
        if (resolution.Status != null)
        {
            return new FileTransferItemResult(source, null, resolution.Status.Value, resolution.ErrorMessage);
        }

        target = resolution.Path!;
        if (request.Mode == FileTransferMode.Move)
        {
            if (request.ConflictPolicy == FileConflictPolicy.Overwrite && Directory.Exists(target))
            {
                return new FileTransferItemResult(
                    source,
                    null,
                    FileTransferStatus.Failed,
                    "Overwriting an existing directory during a move is not supported.");
            }

            Directory.Move(source, target);
            return new FileTransferItemResult(source, target, FileTransferStatus.Succeeded);
        }

        var (success, errors) = CopyDirectorySafe(
            source,
            target,
            request.ConflictPolicy == FileConflictPolicy.Overwrite,
            cancellationToken);

        return success
            ? new FileTransferItemResult(source, target, FileTransferStatus.Succeeded)
            : new FileTransferItemResult(
                source,
                null,
                FileTransferStatus.Failed,
                errors.FirstOrDefault() ?? "The directory could not be copied.");
    }

    private static DestinationResolution ResolveDestination(
        string target,
        FileConflictPolicy conflictPolicy)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return new DestinationResolution(target, null, null);
        }

        return conflictPolicy switch
        {
            FileConflictPolicy.AutoRename => new DestinationResolution(GetNonConflictingPath(target), null, null),
            FileConflictPolicy.Skip => new DestinationResolution(null, FileTransferStatus.Skipped, null),
            FileConflictPolicy.Fail => new DestinationResolution(
                null,
                FileTransferStatus.Failed,
                "The destination already exists."),
            FileConflictPolicy.Overwrite => new DestinationResolution(target, null, null),
            _ => new DestinationResolution(null, FileTransferStatus.Failed, "Unknown conflict policy.")
        };
    }

    private static string GetNonConflictingPath(string target)
    {
        var directory = Path.GetDirectoryName(target) ?? string.Empty;
        var name = Path.GetFileName(target);
        var extension = Path.GetExtension(name);
        var baseName = Path.GetFileNameWithoutExtension(name);
        var candidate = target;
        var counter = 1;

        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            var suffix = counter == 1 ? " (Copy)" : $" (Copy {counter})";
            candidate = Path.Combine(directory, $"{baseName}{suffix}{extension}");
            counter++;
        }

        return candidate;
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                PathComparison);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDescendantOf(string targetPath, string basePath)
    {
        try
        {
            var fullTarget = Path.GetFullPath(targetPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullBase = Path.GetFullPath(basePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullTarget.StartsWith(fullBase, PathComparison);
        }
        catch
        {
            return false;
        }
    }

    private static (bool success, List<string> errors) CopyDirectorySafe(
        string sourceDir,
        string destDir,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (IsDescendantOf(destDir, sourceDir))
        {
            errors.Add($"Cannot copy directory {sourceDir} into its own descendant {destDir}");
            return (false, errors);
        }

        var rootInfo = new DirectoryInfo(sourceDir);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0 || rootInfo.LinkTarget != null)
        {
            try
            {
                if (rootInfo.LinkTarget != null)
                {
                    Directory.CreateSymbolicLink(destDir, rootInfo.LinkTarget);
                    return (true, errors);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to copy symlink {sourceDir}: {ex.Message}");
                return (false, errors);
            }
        }

        var visitedDirs = new HashSet<string>(PathComparer);
        var workQueue = new Queue<(string source, string destination)>();
        workQueue.Enqueue((Path.GetFullPath(sourceDir), Path.GetFullPath(destDir)));

        while (workQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentSource, currentDestination) = workQueue.Dequeue();
            if (!visitedDirs.Add(currentSource)) continue;

            try
            {
                Directory.CreateDirectory(currentDestination);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to create directory {currentDestination}: {ex.Message}");
                continue;
            }

            var directory = new DirectoryInfo(currentSource);
            try
            {
                foreach (var file in directory.GetFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        file.CopyTo(Path.Combine(currentDestination, file.Name), overwrite);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to copy file {file.FullName}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to enumerate files from {currentSource}: {ex.Message}");
            }

            try
            {
                foreach (var subdirectory in directory.GetDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var isReparsePoint =
                            (subdirectory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                            subdirectory.LinkTarget != null;

                        var targetDirectory = Path.Combine(currentDestination, subdirectory.Name);
                        if (isReparsePoint)
                        {
                            if (subdirectory.LinkTarget != null)
                            {
                                Directory.CreateSymbolicLink(targetDirectory, subdirectory.LinkTarget);
                            }
                            continue;
                        }

                        if (!IsDescendantOf(targetDirectory, subdirectory.FullName))
                        {
                            workQueue.Enqueue((subdirectory.FullName, targetDirectory));
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to process directory {subdirectory.FullName}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to enumerate subdirectories from {currentSource}: {ex.Message}");
            }
        }

        return (errors.Count == 0, errors);
    }

    private sealed record DestinationResolution(
        string? Path,
        FileTransferStatus? Status,
        string? ErrorMessage);
}
