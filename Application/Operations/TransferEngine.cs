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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeTempFiles = new(PathComparer);

    public static bool IsInternalTransferScratch(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;

        const string prefix = ".clanker-transfer-";
        const string suffix = ".tmp";
        if (name.Length <= prefix.Length + suffix.Length) return false;
        if (!name.StartsWith(prefix, PathComparison) || !name.EndsWith(suffix, PathComparison)) return false;

        var guidSpan = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return Guid.TryParse(guidSpan, out _);
    }

    public static bool IsInternalRecoveryTemp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;

        // 1. exact .clanker-transfer-{GUID}.bak form
        const string bakPrefix = ".clanker-transfer-";
        const string bakSuffix = ".bak";
        if (name.Length > bakPrefix.Length + bakSuffix.Length &&
            name.StartsWith(bakPrefix, PathComparison) &&
            name.EndsWith(bakSuffix, PathComparison))
        {
            var guidSpan = name.AsSpan(bakPrefix.Length, name.Length - bakPrefix.Length - bakSuffix.Length);
            if (Guid.TryParse(guidSpan, out _))
            {
                return true;
            }
        }

        // 2. valid Clanker batch-rename .c_tmp_... form: .c_tmp_{Guid}_{originalName}
        const string renamePrefix = ".c_tmp_";
        if (name.StartsWith(renamePrefix, PathComparison))
        {
            var rest = name.AsSpan(renamePrefix.Length);
            int nextUnderscore = rest.IndexOf('_');
            if (nextUnderscore > 0)
            {
                var guidSpan = rest.Slice(0, nextUnderscore);
                var originalNameSpan = rest.Slice(nextUnderscore + 1);
                if (originalNameSpan.Length > 0 && Guid.TryParse(guidSpan, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsInternalClankerTemp(string? path)
    {
        return IsInternalTransferScratch(path) || IsInternalRecoveryTemp(path);
    }

    public static bool IsInternalTransferTempFile(string? path)
    {
        return IsInternalTransferScratch(path);
    }

    public static bool IsActiveTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (_activeTempFiles.ContainsKey(path)) return true;
        if (IsInternalClankerTemp(path) && OperationManager.Instance.HasActiveOperations)
        {
            return true;
        }
        return false;
    }

    public static void RegisterActiveTempFile(string path)
    {
        _activeTempFiles.TryAdd(path, 0);
    }

    public static void UnregisterActiveTempFile(string path)
    {
        _activeTempFiles.TryRemove(path, out _);
    }

    public static void DeleteLeftoverTransferScratchFiles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            if (!Directory.Exists(directory)) return;

            int deletedCount = 0;
            int failedCount = 0;

            // Enumerate files
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(directory, ".clanker-transfer-*.tmp", SearchOption.TopDirectoryOnly))
                {
                    if (!IsInternalTransferScratch(filePath)) continue;
                    if (_activeTempFiles.ContainsKey(filePath)) continue;

                    try
                    {
                        File.Delete(filePath);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Debug.WriteLine($"Failed to delete leftover scratch file '{filePath}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to enumerate files in '{directory}': {ex.Message}");
            }

            // Enumerate directories
            try
            {
                foreach (var dirPath in Directory.EnumerateDirectories(directory, ".clanker-transfer-*.tmp", SearchOption.TopDirectoryOnly))
                {
                    if (!IsInternalTransferScratch(dirPath)) continue;
                    if (_activeTempFiles.ContainsKey(dirPath)) continue;

                    try
                    {
                        Directory.Delete(dirPath, recursive: true);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Debug.WriteLine($"Failed to delete leftover scratch directory '{dirPath}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to enumerate directories in '{directory}': {ex.Message}");
            }

            if (deletedCount > 0 || failedCount > 0)
            {
                Debug.WriteLine($"DeleteLeftoverTransferScratchFiles in '{directory}': {deletedCount} deleted, {failedCount} failed.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DeleteLeftoverTransferScratchFiles error for '{directory}': {ex.Message}");
        }
    }

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
        int warningFiles = 0;

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

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory) || !Directory.Exists(request.DestinationDirectory))
        {
            var failedResults = request.SourcePaths.Select(s => new FileTransferItemResult(
                s,
                null,
                FileTransferStatus.Failed,
                "The destination directory does not exist.")).ToList();
            foreach (var r in failedResults)
            {
                job.AddError(r.SourcePath, r.ErrorMessage ?? "The destination directory does not exist.");
            }
            startTime.Stop();
            var failSummary = new OperationSummary(
                failedResults.Count,
                0,
                startTime.Elapsed,
                0,
                0,
                0,
                failedResults.Count,
                0);
            var res = new FileTransferResult(failedResults);
            job.Complete(res, failSummary);
            return res;
        }

        // Contextual sweep: clean abandoned disposable scratch in destination directory
        DeleteLeftoverTransferScratchFiles(request.DestinationDirectory);

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
                : (totalFiles > 0 ? Math.Clamp((double)processedFiles / totalFiles * 100.0, 0.0, 100.0) : 100.0);

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
                job.ConflictCount));
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
                processedFiles++;
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
                string? finalTarget = target;
                bool wasRenamed = false;
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
                    else if (request.ConflictPolicy == FileConflictPolicy.Skip)
                    {
                        skippedFiles++;
                        processedFiles++;
                        results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Skipped, "Skipped by conflict policy."));
                        continue;
                    }
                    else if (request.ConflictPolicy == FileConflictPolicy.AutoRename)
                    {
                        finalTarget = GetUniqueAutoRenamePath(target);
                        wasRenamed = true;
                    }
                    else if (request.ConflictPolicy == FileConflictPolicy.Overwrite)
                    {
                        finalTarget = target;
                    }
                    else // Prompt
                    {
                        ConflictResolution? currentResolution = appliedRule;
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            await job.WaitIfPausedAsync(ct).ConfigureAwait(false);

                            var resolution = currentResolution ?? await PromptConflictResolutionAsync(job, source, target, false, ct).ConfigureAwait(false);
                            if (resolution.ApplyToAllRemaining && appliedRule == null)
                            {
                                appliedRule = resolution;
                            }

                            if (resolution.Action == ConflictAction.Skip)
                            {
                                skippedFiles++;
                                processedFiles++;
                                results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Skipped, "Skipped by conflict resolution."));
                                finalTarget = null;
                                break;
                            }
                            else if (resolution.Action == ConflictAction.KeepBoth)
                            {
                                finalTarget = GetUniqueAutoRenamePath(target);
                                wasRenamed = true;
                                break;
                            }
                            else if (resolution.Action == ConflictAction.Rename)
                            {
                                var destDir = Path.GetDirectoryName(target) ?? request.DestinationDirectory;
                                if (!TryValidateCustomFileName(resolution.CustomNewName, destDir, out var validName, out var renameError))
                                {
                                    job.AddLog($"Invalid rename '{resolution.CustomNewName}': {renameError}", OperationLogLevel.Warning);
                                    currentResolution = null; // Re-prompt!
                                    appliedRule = null;
                                    continue;
                                }
                                finalTarget = Path.Combine(destDir, validName);
                                wasRenamed = true;
                                break;
                            }
                            else // Replace
                            {
                                finalTarget = target;
                                break;
                            }
                        }

                        if (finalTarget == null)
                        {
                            continue;
                        }
                    }
                }

                if (finalTarget == null)
                {
                    continue;
                }

                bool sameVolume = AreSameVolume(source, finalTarget);
                var (success, destFinal, status, error) = await TransferSingleFileAsync(
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
                    if (wasRenamed && status == FileTransferStatus.Succeeded)
                    {
                        status = FileTransferStatus.Renamed;
                    }
                    if (status == FileTransferStatus.Renamed)
                    {
                        renamedFiles++;
                    }
                    if (status == FileTransferStatus.PartialSuccessSourceDeleteFailed)
                    {
                        warningFiles++;
                        job.AddError(source, error ?? "File copied, but source could not be deleted.", isFatal: false);
                        job.AddLog($"Warning: {error}", OperationLogLevel.Warning);
                    }
                    results.Add(new FileTransferItemResult(source, destFinal, status, error));
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

                // Check if directory source is a symbolic link / reparse point
                var srcDirInfo = new DirectoryInfo(source);
                if ((srcDirInfo.Attributes & FileAttributes.ReparsePoint) != 0 || srcDirInfo.LinkTarget != null)
                {
                    if (srcDirInfo.LinkTarget == null)
                    {
                        results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, $"Directory reparse point {source} cannot be preserved: link target is unavailable."));
                        job.AddError(source, "Directory reparse point cannot be preserved: link target is unavailable.");
                        failedFiles++;
                        processedFiles++;
                        continue;
                    }

                    string? finalTargetDir = targetDir;
                    bool linkWasRenamed = false;

                    if (Directory.Exists(targetDir) || File.Exists(targetDir))
                    {
                        if (request.ConflictPolicy == FileConflictPolicy.Fail)
                        {
                            failedFiles++;
                            processedFiles++;
                            job.AddError(source, $"Destination already exists: {targetDir}");
                            results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, "Destination already exists."));
                            continue;
                        }
                        else if (request.ConflictPolicy == FileConflictPolicy.Skip)
                        {
                            skippedFiles++;
                            processedFiles++;
                            results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Skipped, "Skipped by conflict policy."));
                            continue;
                        }
                        else if (request.ConflictPolicy == FileConflictPolicy.AutoRename)
                        {
                            finalTargetDir = GetUniqueAutoRenamePath(targetDir);
                            linkWasRenamed = true;
                        }
                        else if (request.ConflictPolicy == FileConflictPolicy.Overwrite)
                        {
                            finalTargetDir = targetDir;
                        }
                        else // Prompt
                        {
                            ConflictResolution? currentResolution = appliedRule;
                            while (true)
                            {
                                ct.ThrowIfCancellationRequested();
                                await job.WaitIfPausedAsync(ct).ConfigureAwait(false);

                                var resolution = currentResolution ?? await PromptConflictResolutionAsync(job, source, targetDir, true, ct).ConfigureAwait(false);
                                if (resolution.ApplyToAllRemaining && appliedRule == null)
                                {
                                    appliedRule = resolution;
                                }

                                if (resolution.Action == ConflictAction.Skip)
                                {
                                    skippedFiles++;
                                    processedFiles++;
                                    results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Skipped, "Skipped by conflict resolution."));
                                    finalTargetDir = null;
                                    break;
                                }
                                else if (resolution.Action == ConflictAction.KeepBoth)
                                {
                                    finalTargetDir = GetUniqueAutoRenamePath(targetDir);
                                    linkWasRenamed = true;
                                    break;
                                }
                                else if (resolution.Action == ConflictAction.Rename)
                                {
                                    var parent = Path.GetDirectoryName(targetDir) ?? request.DestinationDirectory;
                                    if (!TryValidateCustomFileName(resolution.CustomNewName, parent, out var validName, out var renameError))
                                    {
                                        job.AddLog($"Invalid rename '{resolution.CustomNewName}': {renameError}", OperationLogLevel.Warning);
                                        currentResolution = null;
                                        appliedRule = null;
                                        continue;
                                    }
                                    finalTargetDir = Path.Combine(parent, validName);
                                    linkWasRenamed = true;
                                    break;
                                }
                                else // Replace
                                {
                                    finalTargetDir = targetDir;
                                    break;
                                }
                            }

                            if (finalTargetDir == null)
                            {
                                continue;
                            }
                        }
                    }

                    if (!SafeCreateOrReplaceDirectoryLink(finalTargetDir, srcDirInfo.LinkTarget, out var linkErr))
                    {
                        results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, $"Failed to preserve directory link {source}: {linkErr}"));
                        job.AddError(source, linkErr ?? "Failed to create directory link.");
                        failedFiles++;
                        processedFiles++;
                        continue;
                    }

                    var linkStatus = linkWasRenamed ? FileTransferStatus.Renamed : FileTransferStatus.Succeeded;
                    if (request.Mode == FileTransferMode.Move)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (job.CancellationToken.IsCancellationRequested)
                        {
                            linkStatus = FileTransferStatus.PartialSuccessSourceDeleteFailed;
                            results.Add(new FileTransferItemResult(source, finalTargetDir, linkStatus, "Operation was cancelled before source cleanup could complete."));
                        }
                        else
                        {
                            try
                            {
                                Directory.Delete(source, false);
                                results.Add(new FileTransferItemResult(source, finalTargetDir, linkStatus));
                            }
                            catch (Exception delEx)
                            {
                                linkStatus = FileTransferStatus.PartialSuccessSourceDeleteFailed;
                                results.Add(new FileTransferItemResult(source, finalTargetDir, linkStatus, $"Directory link copied, but source could not be deleted: {delEx.Message}"));
                            }
                        }
                    }
                    else
                    {
                        results.Add(new FileTransferItemResult(source, finalTargetDir, linkStatus));
                    }

                    if (linkStatus == FileTransferStatus.Renamed)
                    {
                        renamedFiles++;
                    }
                    if (linkStatus == FileTransferStatus.PartialSuccessSourceDeleteFailed)
                    {
                        warningFiles++;
                        job.AddError(source, "Directory link copied, but source could not be deleted.", isFatal: false);
                        job.AddLog($"Warning: Directory link copied, but source could not be deleted", OperationLogLevel.Warning);
                    }
                    succeededFiles++;
                    processedFiles++;
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

                        var (dirFiles, dirBytes) = CalculateTotals(new[] { source }, ct);
                        Directory.Move(source, targetDir);
                        results.Add(new FileTransferItemResult(source, targetDir, FileTransferStatus.Succeeded));
                        succeededFiles += (int)Math.Min(dirFiles, int.MaxValue);
                        processedFiles += dirFiles;
                        Interlocked.Add(ref transferredBytes, dirBytes);
                        ReportProgress(Path.GetFileName(source));
                        continue;
                    }
                    catch (Exception)
                    {
                        // Direct same-volume move failed (e.g. locked files inside) - fall back to recursive transfer
                    }
                }

                // Recursive transfer for copy or cross-volume move
                var (dirSuccess, hadPartialDelete, dirDest, dirErrors) = await TransferDirectoryRecursiveAsync(
                    job,
                    source,
                    targetDir,
                    request.Mode,
                    request.ConflictPolicy,
                    appliedRule,
                    newRule => appliedRule = newRule,
                    bytes =>
                    {
                        Interlocked.Add(ref transferredBytes, bytes);
                        ReportProgress(Path.GetFileName(source));
                    },
                    item =>
                    {
                        processedFiles++;
                        switch (item.Status)
                        {
                            case FileTransferStatus.Succeeded:
                                succeededFiles++;
                                break;
                            case FileTransferStatus.Renamed:
                                succeededFiles++;
                                renamedFiles++;
                                break;
                            case FileTransferStatus.Skipped:
                                skippedFiles++;
                                break;
                            case FileTransferStatus.Failed:
                                failedFiles++;
                                break;
                            case FileTransferStatus.PartialSuccessSourceDeleteFailed:
                                succeededFiles++;
                                warningFiles++;
                                break;
                        }
                    },
                    ct).ConfigureAwait(false);

                if (dirSuccess)
                {
                    if (hadPartialDelete)
                    {
                        warningFiles++;
                        results.Add(new FileTransferItemResult(source, dirDest, FileTransferStatus.PartialSuccessSourceDeleteFailed, "Directory copied, but one or more source items could not be deleted."));
                    }
                    else
                    {
                        results.Add(new FileTransferItemResult(source, dirDest, FileTransferStatus.Succeeded));
                    }
                }
                else
                {
                    failedFiles++;
                    results.Add(new FileTransferItemResult(source, null, FileTransferStatus.Failed, dirErrors.FirstOrDefault() ?? "Directory transfer failed."));
                }
            }

            ReportProgress(Path.GetFileName(source), force: true);
        }

        startTime.Stop();
        int failedResultsCount = results.Count(r => r.Status == FileTransferStatus.Failed);
        if (failedResultsCount > failedFiles)
        {
            failedFiles = failedResultsCount;
        }
        int renamedResultsCount = results.Count(r => r.Status == FileTransferStatus.Renamed);
        if (renamedResultsCount > renamedFiles)
        {
            renamedFiles = renamedResultsCount;
        }
        int warningResultsCount = results.Count(r => r.Status == FileTransferStatus.PartialSuccessSourceDeleteFailed);
        if (warningResultsCount > warningFiles)
        {
            warningFiles = warningResultsCount;
        }
        var summary = new OperationSummary(
            totalFiles,
            totalBytes,
            startTime.Elapsed,
            succeededFiles,
            skippedFiles,
            renamedFiles,
            failedFiles,
            warningFiles);

        var finalResult = new FileTransferResult(results);
        job.Complete(finalResult, summary);
        ReportProgress("Complete", force: true);

        return finalResult;
    }

    private static async Task<(bool success, bool hadPartialSourceDelete, string destinationPath, List<string> errors)> TransferDirectoryRecursiveAsync(
        OperationJob job,
        string sourceDir,
        string targetDir,
        FileTransferMode mode,
        FileConflictPolicy conflictPolicy,
        ConflictResolution? currentAppliedRule,
        Action<ConflictResolution> onRuleApplied,
        Action<long> onBytesTransferred,
        Action<FileTransferItemResult> onItemCompleted,
        CancellationToken ct)
    {
        var errors = new List<string>();
        bool hadPartialSourceDelete = false;

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
            return (false, false, targetDir, errors);
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
                else
                {
                    DeleteLeftoverTransferScratchFiles(currentDst);
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
                    bool wasRenamed = false;

                    if (File.Exists(destFile))
                    {
                        if (conflictPolicy == FileConflictPolicy.Fail)
                        {
                            errors.Add($"Destination already exists: {destFile}");
                            job.AddError(destFile, $"Destination already exists: {destFile}");
                            onItemCompleted(new FileTransferItemResult(file.FullName, null, FileTransferStatus.Failed, "Destination already exists."));
                            continue;
                        }
                        else if (conflictPolicy == FileConflictPolicy.Skip)
                        {
                            onItemCompleted(new FileTransferItemResult(file.FullName, null, FileTransferStatus.Skipped, "Skipped by conflict policy."));
                            continue;
                        }
                        else if (conflictPolicy == FileConflictPolicy.AutoRename)
                        {
                            destFile = GetUniqueAutoRenamePath(destFile);
                            wasRenamed = true;
                        }
                        else if (conflictPolicy == FileConflictPolicy.Overwrite)
                        {
                            // Overwrite destFile
                        }
                        else // Prompt
                        {
                            ConflictResolution? currentRes = currentAppliedRule;
                            bool skipFile = false;

                            while (true)
                            {
                                ct.ThrowIfCancellationRequested();
                                await job.WaitIfPausedAsync(ct).ConfigureAwait(false);

                                var resolution = currentRes ?? await PromptConflictResolutionAsync(job, file.FullName, destFile, false, ct).ConfigureAwait(false);
                                if (resolution.ApplyToAllRemaining && currentAppliedRule == null)
                                {
                                    currentAppliedRule = resolution;
                                    onRuleApplied(resolution);
                                }

                                if (resolution.Action == ConflictAction.Skip)
                                {
                                    skipFile = true;
                                    onItemCompleted(new FileTransferItemResult(file.FullName, null, FileTransferStatus.Skipped, "Skipped by conflict resolution."));
                                    break;
                                }
                                else if (resolution.Action == ConflictAction.KeepBoth)
                                {
                                    destFile = GetUniqueAutoRenamePath(destFile);
                                    wasRenamed = true;
                                    break;
                                }
                                else if (resolution.Action == ConflictAction.Rename)
                                {
                                    if (!TryValidateCustomFileName(resolution.CustomNewName, currentDst, out var validName, out var renameError))
                                    {
                                        job.AddLog($"Invalid rename '{resolution.CustomNewName}': {renameError}", OperationLogLevel.Warning);
                                        currentRes = null;
                                        currentAppliedRule = null;
                                        continue;
                                    }
                                    destFile = Path.Combine(currentDst, validName);
                                    wasRenamed = true;
                                    break;
                                }
                                else // Replace
                                {
                                    break;
                                }
                            }

                            if (skipFile)
                            {
                                continue;
                            }
                        }
                    }

                    var (fileSuccess, finalDest, status, fileError) = await TransferSingleFileAsync(
                        job,
                        file.FullName,
                        destFile,
                        mode,
                        sameVolumeMove: false,
                        onBytesTransferred,
                        ct).ConfigureAwait(false);

                    if (fileSuccess)
                    {
                        if (wasRenamed && status == FileTransferStatus.Succeeded)
                        {
                            status = FileTransferStatus.Renamed;
                        }
                        if (status == FileTransferStatus.PartialSuccessSourceDeleteFailed)
                        {
                            hadPartialSourceDelete = true;
                            job.AddError(file.FullName, fileError ?? "File copied, but source could not be deleted.", isFatal: false);
                            job.AddLog($"Warning: {fileError}", OperationLogLevel.Warning);
                        }
                        onItemCompleted(new FileTransferItemResult(file.FullName, finalDest, status, fileError));
                    }
                    else
                    {
                        errors.Add(fileError ?? $"Failed to transfer file {file.FullName}");
                        job.AddError(file.FullName, fileError ?? "File transfer failed");
                        onItemCompleted(new FileTransferItemResult(file.FullName, null, FileTransferStatus.Failed, fileError));
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to enumerate files in {currentSrc}: {ex.Message}");
            }

            // Enqueue subdirectories, guarding against reparse points and recursive loops
            try
            {
                foreach (var sub in dirInfo.GetDirectories())
                {
                    ct.ThrowIfCancellationRequested();
                    var targetSub = Path.Combine(currentDst, sub.Name);
                    var isReparse = (sub.Attributes & FileAttributes.ReparsePoint) != 0 || sub.LinkTarget != null;
                    if (isReparse)
                    {
                        if (sub.LinkTarget != null)
                        {
                            string targetDirForSub = targetSub;
                            bool subWasRenamed = false;

                            if (Directory.Exists(targetSub) || File.Exists(targetSub))
                            {
                                if (conflictPolicy == FileConflictPolicy.Fail)
                                {
                                    errors.Add($"Destination already exists: {targetSub}");
                                    job.AddError(targetSub, $"Destination already exists: {targetSub}");
                                    onItemCompleted(new FileTransferItemResult(sub.FullName, null, FileTransferStatus.Failed, "Destination already exists."));
                                    continue;
                                }
                                else if (conflictPolicy == FileConflictPolicy.Skip)
                                {
                                    onItemCompleted(new FileTransferItemResult(sub.FullName, null, FileTransferStatus.Skipped, "Skipped by conflict policy."));
                                    continue;
                                }
                                else if (conflictPolicy == FileConflictPolicy.AutoRename)
                                {
                                    targetDirForSub = GetUniqueAutoRenamePath(targetSub);
                                    subWasRenamed = true;
                                }
                                else if (conflictPolicy == FileConflictPolicy.Overwrite)
                                {
                                    targetDirForSub = targetSub;
                                }
                                else // Prompt
                                {
                                    ConflictResolution? currentRes = currentAppliedRule;
                                    bool skipSub = false;
                                    while (true)
                                    {
                                        ct.ThrowIfCancellationRequested();
                                        await job.WaitIfPausedAsync(ct).ConfigureAwait(false);

                                        var resolution = currentRes ?? await PromptConflictResolutionAsync(job, sub.FullName, targetSub, true, ct).ConfigureAwait(false);
                                        if (resolution.ApplyToAllRemaining && currentAppliedRule == null)
                                        {
                                            currentAppliedRule = resolution;
                                            onRuleApplied(resolution);
                                        }

                                        if (resolution.Action == ConflictAction.Skip)
                                        {
                                            skipSub = true;
                                            onItemCompleted(new FileTransferItemResult(sub.FullName, null, FileTransferStatus.Skipped, "Skipped by conflict resolution."));
                                            break;
                                        }
                                        else if (resolution.Action == ConflictAction.KeepBoth)
                                        {
                                            targetDirForSub = GetUniqueAutoRenamePath(targetSub);
                                            subWasRenamed = true;
                                            break;
                                        }
                                        else if (resolution.Action == ConflictAction.Rename)
                                        {
                                            if (!TryValidateCustomFileName(resolution.CustomNewName, currentDst, out var validName, out var renameError))
                                            {
                                                job.AddLog($"Invalid rename '{resolution.CustomNewName}': {renameError}", OperationLogLevel.Warning);
                                                currentRes = null;
                                                currentAppliedRule = null;
                                                continue;
                                            }
                                            targetDirForSub = Path.Combine(currentDst, validName);
                                            subWasRenamed = true;
                                            break;
                                        }
                                        else // Replace
                                        {
                                            targetDirForSub = targetSub;
                                            break;
                                        }
                                    }

                                    if (skipSub)
                                    {
                                        continue;
                                    }
                                }
                            }

                            if (!SafeCreateOrReplaceDirectoryLink(targetDirForSub, sub.LinkTarget, out var linkErr))
                            {
                                errors.Add($"Failed to preserve directory link {sub.FullName} -> {targetDirForSub}: {linkErr}");
                                job.AddError(sub.FullName, $"Failed to preserve directory link: {linkErr}");
                                onItemCompleted(new FileTransferItemResult(sub.FullName, null, FileTransferStatus.Failed, linkErr));
                                continue;
                            }

                            var subStatus = subWasRenamed ? FileTransferStatus.Renamed : FileTransferStatus.Succeeded;
                            if (mode == FileTransferMode.Move)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (job.CancellationToken.IsCancellationRequested)
                                {
                                    hadPartialSourceDelete = true;
                                    subStatus = FileTransferStatus.PartialSuccessSourceDeleteFailed;
                                    onItemCompleted(new FileTransferItemResult(sub.FullName, targetDirForSub, subStatus, "Operation was cancelled before source cleanup could complete."));
                                }
                                else
                                {
                                    try
                                    {
                                        Directory.Delete(sub.FullName, false);
                                        onItemCompleted(new FileTransferItemResult(sub.FullName, targetDirForSub, subStatus));
                                    }
                                    catch (Exception delEx)
                                    {
                                        hadPartialSourceDelete = true;
                                        subStatus = FileTransferStatus.PartialSuccessSourceDeleteFailed;
                                        job.AddError(sub.FullName, $"Failed to delete source directory link: {delEx.Message}", isFatal: false);
                                        onItemCompleted(new FileTransferItemResult(sub.FullName, targetDirForSub, subStatus, $"Directory link copied, but source could not be deleted: {delEx.Message}"));
                                    }
                                }
                            }
                            else
                            {
                                onItemCompleted(new FileTransferItemResult(sub.FullName, targetDirForSub, subStatus));
                            }
                            continue;
                        }
                        else
                        {
                            errors.Add($"Directory reparse point {sub.FullName} cannot be preserved: link target is unavailable.");
                            job.AddError(sub.FullName, "Directory reparse point cannot be preserved: link target is unavailable.");
                            onItemCompleted(new FileTransferItemResult(sub.FullName, null, FileTransferStatus.Failed, "Directory reparse point cannot be preserved: link target is unavailable."));
                            continue;
                        }
                    }

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

        // If move, clean up source directories in post-order only if no errors occurred
        if (mode == FileTransferMode.Move && errors.Count == 0)
        {
            sourceDirsInPostOrder.Reverse();
            foreach (var d in sourceDirsInPostOrder)
            {
                try
                {
                    if (Directory.Exists(d))
                    {
                        if (!Directory.EnumerateFileSystemEntries(d).Any())
                        {
                            Directory.Delete(d, recursive: false);
                        }
                        else
                        {
                            hadPartialSourceDelete = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    hadPartialSourceDelete = true;
                    job.AddLog($"Failed to delete source directory {d}: {ex.Message}", OperationLogLevel.Warning);
                }
            }
        }
        else if (mode == FileTransferMode.Move && errors.Count > 0)
        {
            hadPartialSourceDelete = true;
        }

        return (errors.Count == 0, hadPartialSourceDelete, targetDir, errors);
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

    private static bool SafeCreateOrReplaceDirectoryLink(string targetDir, string linkTarget, out string? error)
    {
        error = null;
        var parentDir = Path.GetDirectoryName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        ?? Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(parentDir, $".clanker-transfer-{Guid.NewGuid():N}.tmp");
        RegisterActiveTempFile(tempDir);

        try
        {
            // 1. Create temporary sibling link first
            Directory.CreateSymbolicLink(tempDir, linkTarget);

            // 2. Verify link creation succeeded
            var tempInfo = new DirectoryInfo(tempDir);
            if (!tempInfo.Exists && tempInfo.LinkTarget == null)
            {
                error = "Created temporary directory link could not be verified.";
                return false;
            }

            // 3. If target does not exist, move tempDir to targetDir
            if (!Directory.Exists(targetDir) && !File.Exists(targetDir))
            {
                Directory.Move(tempDir, targetDir);
                UnregisterActiveTempFile(tempDir);
                tempDir = null;
                return true;
            }

            // 4. Target exists - safe backup/replace
            var backupDir = Path.Combine(parentDir, $".clanker-transfer-{Guid.NewGuid():N}.bak");
            RegisterActiveTempFile(backupDir);
            try
            {
                if (Directory.Exists(targetDir))
                {
                    Directory.Move(targetDir, backupDir);
                }
                else
                {
                    File.Move(targetDir, backupDir);
                }
            }
            catch (Exception ex)
            {
                error = $"Cannot safely replace existing destination without destroying it first: {ex.Message}";
                return false;
            }

            // Move temp link to targetDir
            try
            {
                Directory.Move(tempDir, targetDir);
                UnregisterActiveTempFile(tempDir);
                tempDir = null;
            }
            catch (Exception ex)
            {
                // Rollback: restore backup
                try
                {
                    if (Directory.Exists(backupDir)) Directory.Move(backupDir, targetDir);
                    else if (File.Exists(backupDir)) File.Move(backupDir, targetDir);
                }
                catch { }
                error = $"Failed to move new directory link into destination: {ex.Message}";
                return false;
            }

            // Delete backup now that new link is in place
            try
            {
                if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
                else if (File.Exists(backupDir)) File.Delete(backupDir);
            }
            catch { }
            finally
            {
                UnregisterActiveTempFile(backupDir);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (tempDir != null)
            {
                UnregisterActiveTempFile(tempDir);
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: false);
                    else if (File.Exists(tempDir)) File.Delete(tempDir);
                }
                catch { }
            }
        }
    }

    private static async Task<(bool success, string? destinationPath, FileTransferStatus status, string? error)> TransferSingleFileAsync(
        OperationJob job,
        string sourceFile,
        string targetFile,
        FileTransferMode mode,
        bool sameVolumeMove,
        Action<long> onChunkTransferred,
        CancellationToken ct)
    {
        var targetDir = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // Check if source is a file symbolic link / reparse point
        try
        {
            var srcInfo = new FileInfo(sourceFile);
            if ((srcInfo.Attributes & FileAttributes.ReparsePoint) != 0 || srcInfo.LinkTarget != null)
            {
                if (srcInfo.LinkTarget != null)
                {
                    string? tempLink = null;
                    FileAttributes? symlinkTargetAttrs = null;
                    try
                    {
                        var destDirectory = targetDir ?? Directory.GetCurrentDirectory();
                        tempLink = Path.Combine(destDirectory, $".clanker-transfer-{Guid.NewGuid():N}.tmp");
                        RegisterActiveTempFile(tempLink);

                        File.CreateSymbolicLink(tempLink, srcInfo.LinkTarget);

                        var tempInfo = new FileInfo(tempLink);
                        if (!tempInfo.Exists && tempInfo.LinkTarget == null)
                        {
                            throw new IOException("Created temporary symbolic link could not be verified.");
                        }

                        ct.ThrowIfCancellationRequested();
                        if (job.CancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(job.CancellationToken);
                        }

                        if (File.Exists(targetFile))
                        {
                            try
                            {
                                symlinkTargetAttrs = File.GetAttributes(targetFile);
                                if ((symlinkTargetAttrs.Value & FileAttributes.ReadOnly) != 0)
                                {
                                    File.SetAttributes(targetFile, symlinkTargetAttrs.Value & ~FileAttributes.ReadOnly);
                                }
                            }
                            catch { }
                        }

                        try
                        {
                            File.Move(tempLink, targetFile, overwrite: true);
                            UnregisterActiveTempFile(tempLink);
                            tempLink = null;
                        }
                        catch
                        {
                            if (symlinkTargetAttrs.HasValue && File.Exists(targetFile))
                            {
                                try { File.SetAttributes(targetFile, symlinkTargetAttrs.Value); } catch { }
                            }
                            throw;
                        }

                        if (mode == FileTransferMode.Move)
                        {
                            if (ct.IsCancellationRequested || job.CancellationToken.IsCancellationRequested)
                            {
                                return (true, targetFile, FileTransferStatus.PartialSuccessSourceDeleteFailed, "Operation was cancelled before source cleanup could complete.");
                            }

                            try
                            {
                                File.Delete(sourceFile);
                            }
                            catch (Exception delEx)
                            {
                                return (true, targetFile, FileTransferStatus.PartialSuccessSourceDeleteFailed, $"Symbolic link created, but source link could not be deleted: {delEx.Message}");
                            }
                        }

                        onChunkTransferred(0);
                        return (true, targetFile, FileTransferStatus.Succeeded, null);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return (false, null, FileTransferStatus.Failed, $"Failed to preserve file symbolic link '{sourceFile}': {ex.Message}");
                    }
                    finally
                    {
                        if (tempLink != null)
                        {
                            UnregisterActiveTempFile(tempLink);
                            try { if (File.Exists(tempLink)) File.Delete(tempLink); } catch { }
                        }
                    }
                }
                else
                {
                    return (false, null, FileTransferStatus.Failed, $"File reparse point '{sourceFile}' cannot be preserved: link target is unavailable.");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        if (mode == FileTransferMode.Move && sameVolumeMove)
        {
            try
            {
                // Safe same-volume move replace:
                // NEVER do File.Delete(targetFile) before File.Move!
                if (File.Exists(targetFile))
                {
                    FileAttributes? targetAttrs = null;
                    try
                    {
                        targetAttrs = File.GetAttributes(targetFile);
                        if ((targetAttrs.Value & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(targetFile, targetAttrs.Value & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch { }

                    try
                    {
                        File.Move(sourceFile, targetFile, overwrite: true);
                    }
                    catch
                    {
                        // Restore target attributes if direct move failed
                        if (targetAttrs.HasValue && File.Exists(targetFile))
                        {
                            try { File.SetAttributes(targetFile, targetAttrs.Value); } catch { }
                        }

                        // Fallback: safe temp-copy + replace + source delete
                        var tempFile = Path.Combine(targetDir ?? Directory.GetCurrentDirectory(), $".clanker-transfer-{Guid.NewGuid():N}.tmp");
                        RegisterActiveTempFile(tempFile);
                        try
                        {
                            File.Copy(sourceFile, tempFile, overwrite: true);
                            try
                            {
                                File.Move(tempFile, targetFile, overwrite: true);
                            }
                            catch
                            {
                                if (targetAttrs.HasValue && File.Exists(targetFile))
                                {
                                    try { File.SetAttributes(targetFile, targetAttrs.Value); } catch { }
                                }
                                throw;
                            }

                            if (ct.IsCancellationRequested || job.CancellationToken.IsCancellationRequested)
                            {
                                return (true, targetFile, FileTransferStatus.PartialSuccessSourceDeleteFailed, "Operation was cancelled before source cleanup could complete.");
                            }

                            try
                            {
                                File.Delete(sourceFile);
                            }
                            catch (Exception delEx)
                            {
                                return (true, targetFile, FileTransferStatus.PartialSuccessSourceDeleteFailed, $"Destination copy succeeded, but source could not be deleted: {delEx.Message}");
                            }
                        }
                        finally
                        {
                            UnregisterActiveTempFile(tempFile);
                            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                        }
                    }
                }
                else
                {
                    try
                    {
                        File.Move(sourceFile, targetFile);
                    }
                    catch
                    {
                        var tempFile = Path.Combine(targetDir ?? Directory.GetCurrentDirectory(), $".clanker-transfer-{Guid.NewGuid():N}.tmp");
                        RegisterActiveTempFile(tempFile);
                        try
                        {
                            File.Copy(sourceFile, tempFile, overwrite: true);
                            File.Move(tempFile, targetFile, overwrite: true);

                            if (ct.IsCancellationRequested || job.CancellationToken.IsCancellationRequested)
                            {
                                return (true, targetFile, FileTransferStatus.PartialSuccessSourceDeleteFailed, "Operation was cancelled before source cleanup could complete.");
                            }

                            try
                            {
                                File.Delete(sourceFile);
                            }
                            catch (Exception delEx)
                            {
                                return (true, targetFile, FileTransferStatus.PartialSuccessSourceDeleteFailed, $"Destination copy succeeded, but source could not be deleted: {delEx.Message}");
                            }
                        }
                        finally
                        {
                            UnregisterActiveTempFile(tempFile);
                            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                        }
                    }
                }

                long fileLen = 0;
                try { fileLen = new FileInfo(targetFile).Length; } catch { }
                onChunkTransferred(fileLen);
                return (true, targetFile, FileTransferStatus.Succeeded, null);
            }
            catch (Exception ex)
            {
                return (false, null, FileTransferStatus.Failed, ex.Message);
            }
        }

        // Copy or cross-volume Move: Safe overwrite via temporary sibling
        string? tempPath = null;
        FileAttributes? originalTargetAttrs = null;
        try
        {
            var sourceInfo = new FileInfo(sourceFile);
            var destDirectory = targetDir ?? Directory.GetCurrentDirectory();
            tempPath = Path.Combine(destDirectory, $".clanker-transfer-{Guid.NewGuid():N}.tmp");
            RegisterActiveTempFile(tempPath);

            await using (var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (var destinationStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
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
                await destinationStream.FlushAsync(ct).ConfigureAwait(false);
            }

            // FINAL CANCELLATION CHECK BEFORE COMMIT
            ct.ThrowIfCancellationRequested();
            if (job.CancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(job.CancellationToken);
            }

            // Preserve file metadata on temp file before replacing destination
            try
            {
                File.SetAttributes(tempPath, sourceInfo.Attributes);
                File.SetCreationTimeUtc(tempPath, sourceInfo.CreationTimeUtc);
                File.SetLastWriteTimeUtc(tempPath, sourceInfo.LastWriteTimeUtc);
            }
            catch { }

            // Atomically/safely replace target with tempPath
            if (File.Exists(targetFile))
            {
                try
                {
                    originalTargetAttrs = File.GetAttributes(targetFile);
                    if ((originalTargetAttrs.Value & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(targetFile, originalTargetAttrs.Value & ~FileAttributes.ReadOnly);
                    }
                }
                catch { }
            }

            try
            {
                File.Move(tempPath, targetFile, overwrite: true);
                UnregisterActiveTempFile(tempPath);
                tempPath = null; // Successfully replaced target, no temp file to clean up
            }
            catch
            {
                if (originalTargetAttrs.HasValue && File.Exists(targetFile))
                {
                    try { File.SetAttributes(targetFile, originalTargetAttrs.Value); } catch { }
                }
                throw;
            }

            // If move, check cancellation before deleting source!
            if (mode == FileTransferMode.Move)
            {
                if (ct.IsCancellationRequested || job.CancellationToken.IsCancellationRequested)
                {
                    return (true, targetFile, FileTransferStatus.PartialSuccessSourceDeleteFailed, "Operation was cancelled before source cleanup could complete.");
                }

                try
                {
                    File.Delete(sourceFile);
                }
                catch (Exception delEx)
                {
                    return (true, targetFile, FileTransferStatus.PartialSuccessSourceDeleteFailed, $"Destination copy succeeded, but source could not be deleted: {delEx.Message}");
                }
            }

            return (true, targetFile, FileTransferStatus.Succeeded, null);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (tempPath != null)
                {
                    UnregisterActiveTempFile(tempPath);
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            }
            catch { }
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (tempPath != null)
                {
                    UnregisterActiveTempFile(tempPath);
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            }
            catch { }
            return (false, null, FileTransferStatus.Failed, ex.Message);
        }
    }

    public static bool TryValidateCustomFileName(string? customName, string targetDirectory, out string validName, out string? errorMessage)
    {
        validName = string.Empty;
        if (string.IsNullOrWhiteSpace(customName))
        {
            errorMessage = "File name cannot be empty.";
            return false;
        }

        var trimmed = customName.Trim();
        if (trimmed is "." or ".." || Path.IsPathRooted(trimmed))
        {
            errorMessage = "File name cannot be a rooted path or relative navigation ('.' or '..').";
            return false;
        }

        var invalidChars = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).Distinct().ToArray();
        if (trimmed.IndexOfAny(invalidChars) >= 0)
        {
            errorMessage = "File name contains invalid characters or path separators.";
            return false;
        }

        if (Path.GetFileName(trimmed) != trimmed)
        {
            errorMessage = "File name cannot contain directory components.";
            return false;
        }

        var candidatePath = Path.Combine(targetDirectory, trimmed);
        if (File.Exists(candidatePath) || Directory.Exists(candidatePath))
        {
            errorMessage = $"Destination already exists: {trimmed}";
            return false;
        }

        validName = trimmed;
        errorMessage = null;
        return true;
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
                            var isReparse = (sub.Attributes & FileAttributes.ReparsePoint) != 0 || sub.LinkTarget != null;
                            if (!isReparse)
                            {
                                q.Enqueue(sub.FullName);
                            }
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
