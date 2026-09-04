using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.AppLayer;

namespace ClankerExplorer.AppLayer.Operations;

public sealed class TransferEngine
{
    private const int BufferSize = 128 * 1024; // 128 KB chunk buffer
    private const int ProgressThrottleMilliseconds = 100; // 100ms progress throttle

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<FileTransferResult> ExecuteTransferAsync(
        OperationJob job,
        FileTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(request);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.CancellationToken);
        var ct = linkedCts.Token;

        var results = new List<FileTransferItemResult>();
        var startTime = Stopwatch.StartNew();

        int succeededFiles = 0;
        int skippedFiles = 0;
        int renamedFiles = 0;
        int failedFiles = 0;

        ConflictResolution? appliedRule = null;
        if (request.ConflictPolicy == FileConflictPolicy.Overwrite)
        {
            appliedRule = new ConflictResolution(ConflictAction.Replace, ApplyToAllRemaining: true);
        }
        else if (request.ConflictPolicy == FileConflictPolicy.Skip)
        {
            appliedRule = new ConflictResolution(ConflictAction.Skip, ApplyToAllRemaining: true);
        }
        else if (request.ConflictPolicy == FileConflictPolicy.AutoRename)
        {
            appliedRule = new ConflictResolution(ConflictAction.KeepBoth, ApplyToAllRemaining: true);
        }

        // Calculate totals across all sources
        var (totalFiles, totalBytes) = await Task.Run(() => CalculateTotals(request.SourcePaths, ct), ct).ConfigureAwait(false);
        long transferredBytes = 0;
        long processedFiles = 0;

        var lastProgressTime = Stopwatch.StartNew();
        long lastProgressBytes = 0;
        double currentSpeed = 0;

        void ReportProgress(string currentItem, bool force = false)
        {
            var elapsedMs = lastProgressTime.ElapsedMilliseconds;
            if (!force && elapsedMs < ProgressThrottleMilliseconds)
            {
                return;
            }

            var nowBytes = Interlocked.Read(ref transferredBytes);
            if (elapsedMs > 0)
            {
                var bytesInPeriod = nowBytes - lastProgressBytes;
                var periodSpeed = bytesInPeriod / (elapsedMs / 1000.0);
                currentSpeed = currentSpeed <= 0 ? periodSpeed : (currentSpeed * 0.7) + (periodSpeed * 0.3);
                lastProgressTime.Restart();
                lastProgressBytes = nowBytes;
            }

            double percentage = totalBytes > 0
                ? Math.Clamp((double)nowBytes / totalBytes * 100.0, 0.0, 100.0)
                : (totalFiles > 0 ? Math.Clamp((double)processedFiles / totalFiles * 100.0, 0.0, 100.0) : 0.0);

            TimeSpan? remaining = null;
            if (currentSpeed > 0 && totalBytes > nowBytes)
            {
                var remSec = (totalBytes - nowBytes) / currentSpeed;
                if (remSec is >= 0 and <= 86400 * 7)
                {
                    remaining = TimeSpan.FromSeconds(remSec);
                }
            }

            job.UpdateProgress(new OperationProgress(
                job.Type.ToString(),
                currentItem,
                totalFiles,
                processedFiles,
                totalBytes,
                nowBytes,
                percentage,
                currentSpeed,
                startTime.Elapsed,
                remaining,
                job.State,
                job.Errors.Count,
                0));
        }

        ReportProgress("Starting transfer...", force: true);

        foreach (var source in request.SourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(source))
            {
                results.Add(new FileTransferItemResult(source ?? string.Empty, null, FileTransferStatus.Failed, "The source path is empty."));
                continue;
            }

            if (!File.Exists(source) && !Directory.Exists(source))
            {
                results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, "The source path does not exist."));
                job.AddError(source, "The source path does not exist.");
                failedFiles++;
                continue;
            }

            if (File.Exists(source))
            {
                var target = Path.Combine(request.DestinationDirectory, Path.GetFileName(source));
                if (request.Mode == FileTransferMode.Move && PathsEqual(source, target))
                {
                    results.Add(new FileTransferItemResult(source, target, FileTransferStatus.Succeeded));
                    succeededFiles++;
                    processedFiles++;
                    continue;
                }

                // Check conflict
                string finalTarget = target;
                if (File.Exists(target))
                {
                    if (request.ConflictPolicy == FileConflictPolicy.Fail)
                    {
                        failedFiles++;
                        processedFiles++;
                        job.AddError(source, $"Destination already exists: {target}");
                        results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, "Destination already exists."));
                        continue;
                    }

                    ConflictResolution resolution = appliedRule ?? await PromptConflictResolutionAsync(job, source, target, false, ct).ConfigureAwait(false);
                    if (resolution.ApplyToAllRemaining && appliedRule == null)
                    {
                        appliedRule = resolution;
                    }

                    switch (resolution.Action)
                    {
                        case ConflictAction.Skip:
                            skippedFiles++;
                            processedFiles++;
                            results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Skipped, "Skipped by conflict resolution."));
                            continue;

                        case ConflictAction.KeepBoth:
                            finalTarget = GetUniqueAutoRenamePath(target);
                            renamedFiles++;
                            break;

                        case ConflictAction.Rename:
                            var destDir = Path.GetDirectoryName(target) ?? request.DestinationDirectory;
                            finalTarget = !string.IsNullOrWhiteSpace(resolution.CustomNewName)
                                ? Path.Combine(destDir, resolution.CustomNewName)
                                : GetUniqueAutoRenamePath(target);
                            renamedFiles++;
                            break;

                        case ConflictAction.Replace:
                            // Overwrite
                            break;
                    }
                }

                bool sameVolume = AreSameVolume(source, finalTarget);
                var (success, destFinal, error) = await TransferSingleFileAsync(
                    job,
                    source,
                    finalTarget,
                    request.Mode,
                    sameVolume,
                    progressBytes =>
                    {
                        Interlocked.Add(ref transferredBytes, progressBytes);
                        ReportProgress(Path.GetFileName(source));
                    },
                    ct).ConfigureAwait(false);

                processedFiles++;
                if (success)
                {
                    succeededFiles++;
                    results.Add(new FileTransferItemResult(source, destFinal, FileTransferStatus.Succeeded));
                }
                else
                {
                    failedFiles++;
                    job.AddError(source, error ?? "File transfer failed.");
                    results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, error));
                }
            }
            else if (Directory.Exists(source))
            {
                var baseDirName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var targetDir = Path.Combine(request.DestinationDirectory, baseDirName);

                if (request.Mode == FileTransferMode.Move && PathsEqual(source, targetDir))
                {
                    results.Add(new FileTransferItemResult(source, targetDir, FileTransferStatus.Succeeded));
                    succeededFiles++;
                    continue;
                }

                if (IsDescendantOf(targetDir, source))
                {
                    results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, "A directory cannot be transferred into one of its own descendants."));
                    job.AddError(source, "A directory cannot be transferred into one of its own descendants.");
                    failedFiles++;
                    continue;
                }

                bool sameVolume = AreSameVolume(source, targetDir);
                if (request.Mode == FileTransferMode.Move && sameVolume)
                {
                    try
                    {
                        if (Directory.Exists(targetDir))
                        {
                            results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, "Overwriting an existing directory during a move is not supported."));
                            job.AddError(source, "Overwriting an existing directory during a move is not supported.");
                            failedFiles++;
                            continue;
                        }

                        Directory.Move(source, targetDir);
                        results.Add(new FileTransferItemResult(source, targetDir, FileTransferStatus.Succeeded));
                        succeededFiles++;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, ex.Message));
                        job.AddError(source, ex.Message);
                        failedFiles++;
                        continue;
                    }
                }

                // Recursive transfer for copy or cross-volume move
                var (dirSuccess, dirDest, dirErrors) = await TransferDirectoryRecursiveAsync(
                    job,
                    source,
                    targetDir,
                    request.Mode,
                    appliedRule,
                    newRule => appliedRule = newRule,
                    bytes =>
                    {
                        Interlocked.Add(ref transferredBytes, bytes);
                        ReportProgress(Path.GetFileName(source));
                    },
                    () =>
                    {
                        Interlocked.Increment(ref processedFiles);
                        Interlocked.Increment(ref succeededFiles);
                    },
                    ct).ConfigureAwait(false);

                if (dirSuccess)
                {
                    results.Add(new FileTransferItemResult(source, dirDest, FileTransferStatus.Succeeded));
                }
                else
                {
                    results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, dirErrors.FirstOrDefault() ?? "Directory transfer failed."));
                }
            }

            ReportProgress(Path.GetFileName(source), force: true);
        }

        startTime.Stop();
        var summary = new OperationSummary(
            totalFiles,
            totalBytes,
            startTime.Elapsed,
            succeededFiles,
            skippedFiles,
            renamedFiles,
            failedFiles);

        var finalResult = new FileTransferResult(results);
        job.Complete(finalResult, summary);
        ReportProgress("Complete", force: true);

        return finalResult;
    }

    private static async Task<(bool success, string destinationPath, List<string> errors)> TransferDirectoryRecursiveAsync(
        OperationJob job,
        string sourceDir,
        string targetDir,
        FileTransferMode mode,
        ConflictResolution? currentAppliedRule,
        Action<ConflictResolution> onRuleApplied,
        Action<long> onBytesTransferred,
        Action onFileSucceeded,
        CancellationToken ct)
    {
        var errors = new List<string>();

        try
        {
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to create directory {targetDir}: {ex.Message}");
            return (false, targetDir, errors);
        }

        var queue = new Queue<(string src, string dst)>();
        var sourceDirsInPostOrder = new List<string>();
        queue.Enqueue((Path.GetFullPath(sourceDir), Path.GetFullPath(targetDir)));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(ct).ConfigureAwait(false);

            var (currentSrc, currentDst) = queue.Dequeue();
            sourceDirsInPostOrder.Add(currentSrc);

            try
            {
                if (!Directory.Exists(currentDst))
                {
                    Directory.CreateDirectory(currentDst);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to create {currentDst}: {ex.Message}");
                continue;
            }

            var dirInfo = new DirectoryInfo(currentSrc);

            // Transfer files in current directory
            try
            {
                foreach (var file in dirInfo.GetFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(ct).ConfigureAwait(false);

                    var destFile = Path.Combine(currentDst, file.Name);

                    if (File.Exists(destFile))
                    {
                        ConflictResolution resolution = currentAppliedRule ?? await PromptConflictResolutionAsync(job, file.FullName, destFile, false, ct).ConfigureAwait(false);
                        if (resolution.ApplyToAllRemaining && currentAppliedRule == null)
                        {
                            currentAppliedRule = resolution;
                            onRuleApplied(resolution);
                        }

                        if (resolution.Action == ConflictAction.Skip)
                        {
                            continue;
                        }
                        else if (resolution.Action == ConflictAction.KeepBoth)
                        {
                            destFile = GetUniqueAutoRenamePath(destFile);
                        }
                        else if (resolution.Action == ConflictAction.Rename && !string.IsNullOrWhiteSpace(resolution.CustomNewName))
                        {
                            destFile = Path.Combine(currentDst, resolution.CustomNewName);
                        }
                    }

                    var (fileSuccess, _, fileError) = await TransferSingleFileAsync(
                        job,
                        file.FullName,
                        destFile,
                        mode,
                        sameVolumeMove: false,
                        onBytesTransferred,
                        ct).ConfigureAwait(false);

                    if (fileSuccess)
                    {
                        onFileSucceeded();
                    }
                    else
                    {
                        errors.Add(fileError ?? $"Failed to transfer file {file.FullName}");
                        job.AddError(file.FullName, fileError ?? "File transfer failed");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to enumerate files in {currentSrc}: {ex.Message}");
            }

            // Enqueue subdirectories
            try
            {
                foreach (var sub in dirInfo.GetDirectories())
                {
                    ct.ThrowIfCancellationRequested();
                    var targetSub = Path.Combine(currentDst, sub.Name);
                    if (!IsDescendantOf(targetSub, sub.FullName))
                    {
                        queue.Enqueue((sub.FullName, targetSub));
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to enumerate subdirectories in {currentSrc}: {ex.Message}");
            }
        }

        // If move, clean up source directories in post-order
        if (mode == FileTransferMode.Move && errors.Count == 0)
        {
            sourceDirsInPostOrder.Reverse();
            foreach (var d in sourceDirsInPostOrder)
            {
                try
                {
                    if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                    {
                        Directory.Delete(d, recursive: false);
                    }
                }
                catch { }
            }
        }

        return (errors.Count == 0, targetDir, errors);
    }

    private static async Task<ConflictResolution> PromptConflictResolutionAsync(
        OperationJob job,
        string sourcePath,
        string destinationPath,
        bool isDirectory,
        CancellationToken ct)
    {
        var suggestedPath = GetUniqueAutoRenamePath(destinationPath);
        var conflict = new OperationConflict(sourcePath, destinationPath, suggestedPath, isDirectory);
        return await job.PromptConflictAsync(conflict, ct).ConfigureAwait(false);
    }

    private static async Task<(bool success, string? destinationPath, string? error)> TransferSingleFileAsync(
        OperationJob job,
        string sourceFile,
        string targetFile,
        FileTransferMode mode,
        bool sameVolumeMove,
        Action<long> onChunkTransferred,
        CancellationToken ct)
    {
        if (mode == FileTransferMode.Move && sameVolumeMove)
        {
            try
            {
                var targetDir = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                if (File.Exists(targetFile))
                {
                    File.Delete(targetFile);
                }
                File.Move(sourceFile, targetFile);

                long fileLen = 0;
                try { fileLen = new FileInfo(targetFile).Length; } catch { }
                onChunkTransferred(fileLen);
                return (true, targetFile, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        try
        {
            var targetDir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var sourceInfo = new FileInfo(sourceFile);

            await using (var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (var destinationStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                byte[] buffer = new byte[BufferSize];
                int bytesRead;

                while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(ct).ConfigureAwait(false);

                    await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    onChunkTransferred(bytesRead);
                }
            }

            // Preserve file metadata
            try
            {
                File.SetAttributes(targetFile, sourceInfo.Attributes);
                File.SetCreationTimeUtc(targetFile, sourceInfo.CreationTimeUtc);
                File.SetLastWriteTimeUtc(targetFile, sourceInfo.LastWriteTimeUtc);
            }
            catch { }

            // If move, delete source file now that copy fully succeeded
            if (mode == FileTransferMode.Move)
            {
                try
                {
                    File.Delete(sourceFile);
                }
                catch (Exception delEx)
                {
                    return (true, targetFile, $"File copied, but source could not be deleted: {delEx.Message}");
                }
            }

            return (true, targetFile, null);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (File.Exists(targetFile))
                {
                    File.Delete(targetFile);
                }
            }
            catch { }
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(targetFile))
                {
                    File.Delete(targetFile);
                }
            }
            catch { }
            return (false, null, ex.Message);
        }
    }

    private static (long totalFiles, long totalBytes) CalculateTotals(IReadOnlyList<string> sourcePaths, CancellationToken ct)
    {
        long files = 0;
        long bytes = 0;

        foreach (var path in sourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (File.Exists(path))
            {
                files++;
                try { bytes += new FileInfo(path).Length; } catch { }
            }
            else if (Directory.Exists(path))
            {
                var q = new Queue<string>();
                q.Enqueue(path);

                while (q.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var cur = q.Dequeue();
                    try
                    {
                        var di = new DirectoryInfo(cur);
                        foreach (var f in di.GetFiles())
                        {
                            files++;
                            try { bytes += f.Length; } catch { }
                        }
                        foreach (var sub in di.GetDirectories())
                        {
                            q.Enqueue(sub.FullName);
                        }
                    }
                    catch { }
                }
            }
        }

        return (files, bytes);
    }

    private static string GetUniqueAutoRenamePath(string target)
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

    public static bool AreSameVolume(string path1, string path2)
    {
        try
        {
            var root1 = Path.GetPathRoot(Path.GetFullPath(path1));
            var root2 = Path.GetPathRoot(Path.GetFullPath(path2));
            if (string.IsNullOrEmpty(root1) || string.IsNullOrEmpty(root2)) return false;
            return string.Equals(root1.TrimEnd('\\', '/'), root2.TrimEnd('\\', '/'), PathComparison);
        }
        catch
        {
            return false;
        }
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
}
