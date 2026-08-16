using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClankerExplorer.Services;

public static class ClipboardFileService
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly List<string> _storedPaths = new();
    private static bool _isCut = false;
    private static readonly object _lock = new();

    public static event Action? ClipboardChanged;

    public static bool IsCutMode
    {
        get { lock (_lock) { return _isCut; } }
    }

    public static IReadOnlyList<string> StoredPaths
    {
        get { lock (_lock) { return _storedPaths.ToList(); } }
    }

    public static bool IsPathCut(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        lock (_lock)
        {
            if (!_isCut) return false;
            var normalized = path.TrimEnd('\\', '/');
            return _storedPaths.Any(p => string.Equals(p.TrimEnd('\\', '/'), normalized, PathComparison));
        }
    }

    public static void Copy(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            _storedPaths.Clear();
            _storedPaths.AddRange(paths);
            _isCut = false;
        }
        ClipboardChanged?.Invoke();
    }

    public static void Cut(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            _storedPaths.Clear();
            _storedPaths.AddRange(paths);
            _isCut = true;
        }
        ClipboardChanged?.Invoke();
    }

    public static bool CanPaste
    {
        get { lock (_lock) { return _storedPaths.Count > 0; } }
    }

    public static async Task<(int successCount, List<string> failedPaths, List<string> createdDestinationPaths)> PasteAsync(string destinationDirectory, CancellationToken cancellationToken = default)
    {
        List<string> sources;
        bool isCutMode;

        lock (_lock)
        {
            sources = _storedPaths.ToList();
            isCutMode = _isCut;
        }

        if (!Directory.Exists(destinationDirectory)) return (0, sources, new List<string>());

        var successfulPaths = new List<string>();
        var failedPaths = new List<string>();
        var createdDestinationPaths = new List<string>();

        await Task.Run(() =>
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (File.Exists(source))
                    {
                        var fileName = Path.GetFileName(source);
                        var dest = GetNonConflictingPath(destinationDirectory, fileName, isCutMode);

                        if (isCutMode)
                        {
                            File.Move(source, dest);
                        }
                        else
                        {
                            File.Copy(source, dest, false);
                        }
                        successfulPaths.Add(source);
                        createdDestinationPaths.Add(dest);
                    }
                    else if (Directory.Exists(source))
                    {
                        var dirName = Path.GetFileName(source.TrimEnd('\\', '/'));
                        var dest = GetNonConflictingPath(destinationDirectory, dirName, isCutMode);

                        if (IsDescendantOf(dest, source))
                        {
                            Debug.WriteLine($"Cannot paste directory {source} into its own subdirectory {dest}");
                            failedPaths.Add(source);
                            continue;
                        }

                        if (isCutMode)
                        {
                            Directory.Move(source, dest);
                            successfulPaths.Add(source);
                            createdDestinationPaths.Add(dest);
                        }
                        else
                        {
                            var (success, count, errors) = CopyDirectorySafe(source, dest, cancellationToken);
                            if (success)
                            {
                                successfulPaths.Add(source);
                                createdDestinationPaths.Add(dest);
                            }
                            else
                            {
                                failedPaths.Add(source);
                            }
                        }
                    }
                    else
                    {
                        failedPaths.Add(source);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to paste {source}: {ex.Message}");
                    failedPaths.Add(source);
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            if (isCutMode)
            {
                // Only remove successfully moved items from cut list; keep failed ones
                foreach (var succ in successfulPaths)
                {
                    _storedPaths.RemoveAll(p => string.Equals(p, succ, PathComparison));
                }

                if (_storedPaths.Count == 0)
                {
                    _isCut = false;
                }
            }
        }

        ClipboardChanged?.Invoke();
        return (successfulPaths.Count, failedPaths, createdDestinationPaths);
    }

    private static string GetNonConflictingPath(string dir, string name, bool isMove)
    {
        var target = Path.Combine(dir, name);
        if (isMove || (!File.Exists(target) && !Directory.Exists(target)))
        {
            return target;
        }

        // For copy operations, generate non-conflicting unique name e.g. "file (Copy).ext"
        var ext = Path.GetExtension(name);
        var baseName = Path.GetFileNameWithoutExtension(name);
        int counter = 1;

        while (File.Exists(target) || Directory.Exists(target))
        {
            string newName = counter == 1 ? $"{baseName} (Copy){ext}" : $"{baseName} (Copy {counter}){ext}";
            target = Path.Combine(dir, newName);
            counter++;
        }

        return target;
    }

    private static bool IsDescendantOf(string targetPath, string basePath)
    {
        try
        {
            var fullTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullTarget.StartsWith(fullBase, PathComparison);
        }
        catch
        {
            return false;
        }
    }

    private static (bool success, int filesCopied, List<string> errors) CopyDirectorySafe(string sourceDir, string destDir, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        int filesCopied = 0;

        if (IsDescendantOf(destDir, sourceDir))
        {
            errors.Add($"Cannot copy directory {sourceDir} into its own descendant {destDir}");
            return (false, 0, errors);
        }

        // Handle symlink at root source
        var rootInfo = new DirectoryInfo(sourceDir);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0 || rootInfo.LinkTarget != null)
        {
            try
            {
                if (rootInfo.LinkTarget != null)
                {
                    Directory.CreateSymbolicLink(destDir, rootInfo.LinkTarget);
                    return (true, 1, errors);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to copy symlink {sourceDir}: {ex.Message}");
                return (false, 0, errors);
            }
        }

        var visitedDirs = new HashSet<string>(PathComparer);
        var workQueue = new Queue<(string src, string dst)>();
        workQueue.Enqueue((Path.GetFullPath(sourceDir), Path.GetFullPath(destDir)));

        while (workQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentSrc, currentDst) = workQueue.Dequeue();

            if (!visitedDirs.Add(currentSrc))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(currentDst);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to create directory {currentDst}: {ex.Message}");
                continue;
            }

            var dirInfo = new DirectoryInfo(currentSrc);

            // Copy files
            try
            {
                foreach (var file in dirInfo.GetFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var targetFilePath = Path.Combine(currentDst, file.Name);
                        file.CopyTo(targetFilePath, true);
                        filesCopied++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to copy file {file.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to enumerate files from {currentSrc}: {ex.Message}");
            }

            // Enqueue subdirectories
            try
            {
                foreach (var subDir in dirInfo.GetDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        bool isReparsePoint = (subDir.Attributes & FileAttributes.ReparsePoint) != 0 || subDir.LinkTarget != null;
                        if (isReparsePoint)
                        {
                            try
                            {
                                if (subDir.LinkTarget != null)
                                {
                                    var targetSubDir = Path.Combine(currentDst, subDir.Name);
                                    Directory.CreateSymbolicLink(targetSubDir, subDir.LinkTarget);
                                    filesCopied++;
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Failed to link subfolder {subDir.FullName}: {ex.Message}");
                            }
                            continue;
                        }

                        var targetDir = Path.Combine(currentDst, subDir.Name);
                        if (!IsDescendantOf(targetDir, subDir.FullName))
                        {
                            workQueue.Enqueue((subDir.FullName, targetDir));
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to process directory {subDir.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to enumerate subdirectories from {currentSrc}: {ex.Message}");
            }
        }

        bool success = errors.Count == 0;
        return (success, filesCopied, errors);
    }
}
