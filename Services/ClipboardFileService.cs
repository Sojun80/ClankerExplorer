using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClankerExplorer.Services;

public static class ClipboardFileService
{
    private static readonly List<string> _storedPaths = new();
    private static bool _isCut = false;

    public static event Action? ClipboardChanged;

    public static bool IsCutMode => _isCut;
    public static IReadOnlyList<string> StoredPaths => _storedPaths;

    public static bool IsPathCut(string path)
    {
        if (!_isCut || string.IsNullOrEmpty(path)) return false;
        var normalized = path.TrimEnd('\\', '/');
        return _storedPaths.Any(p => string.Equals(p.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static void Copy(IEnumerable<string> paths)
    {
        _storedPaths.Clear();
        _storedPaths.AddRange(paths);
        _isCut = false;
        ClipboardChanged?.Invoke();
    }

    public static void Cut(IEnumerable<string> paths)
    {
        _storedPaths.Clear();
        _storedPaths.AddRange(paths);
        _isCut = true;
        ClipboardChanged?.Invoke();
    }

    public static bool CanPaste => _storedPaths.Count > 0;

    public static void Paste(string destinationDirectory)
    {
        if (!Directory.Exists(destinationDirectory)) return;

        var sources = _storedPaths.ToList();
        foreach (var source in sources)
        {
            try
            {
                if (File.Exists(source))
                {
                    var dest = Path.Combine(destinationDirectory, Path.GetFileName(source));
                    if (_isCut)
                    {
                        File.Move(source, dest, true);
                    }
                    else
                    {
                        File.Copy(source, dest, true);
                    }
                }
                else if (Directory.Exists(source))
                {
                    var dest = Path.Combine(destinationDirectory, Path.GetFileName(source));
                    if (_isCut)
                    {
                        // Check if destination is inside source before moving
                        if (IsDescendantOf(dest, source))
                        {
                            Debug.WriteLine($"Cannot move {source} into its own subdirectory {dest}");
                            continue;
                        }
                        Directory.Move(source, dest);
                    }
                    else
                    {
                        CopyDirectorySafe(source, dest);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to paste {source}: {ex.Message}");
            }
        }

        if (_isCut)
        {
            _storedPaths.Clear();
            _isCut = false;
        }

        ClipboardChanged?.Invoke();
    }

    private static bool IsDescendantOf(string targetPath, string basePath)
    {
        try
        {
            var fullTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullTarget.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectorySafe(string sourceDir, string destDir)
    {
        // 1. Guard against copying a folder into itself or its own descendants
        if (IsDescendantOf(destDir, sourceDir))
        {
            Debug.WriteLine($"Cannot copy directory {sourceDir} into its own descendant {destDir}");
            return;
        }

        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workQueue = new Queue<(string src, string dst)>();
        workQueue.Enqueue((Path.GetFullPath(sourceDir), Path.GetFullPath(destDir)));

        while (workQueue.Count > 0)
        {
            var (currentSrc, currentDst) = workQueue.Dequeue();

            if (!visitedDirs.Add(currentSrc))
            {
                // Prevent infinite cycle in case of symbolic link loop
                continue;
            }

            try
            {
                Directory.CreateDirectory(currentDst);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create directory {currentDst}: {ex.Message}");
                continue;
            }

            var dirInfo = new DirectoryInfo(currentSrc);

            // Copy files in current directory
            try
            {
                foreach (var file in dirInfo.GetFiles())
                {
                    try
                    {
                        var targetFilePath = Path.Combine(currentDst, file.Name);
                        file.CopyTo(targetFilePath, true);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to copy file {file.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read files from {currentSrc}: {ex.Message}");
            }

            // Enqueue subdirectories (skipping reparse points / symlinks to prevent traversal outside tree)
            try
            {
                foreach (var subDir in dirInfo.GetDirectories())
                {
                    try
                    {
                        // Check for reparse points / junctions / symlinks
                        bool isReparsePoint = (subDir.Attributes & FileAttributes.ReparsePoint) != 0 || subDir.LinkTarget != null;
                        if (isReparsePoint)
                        {
                            // Skip traversing into linked directory trees
                            Debug.WriteLine($"Skipping traversal into reparse point / link {subDir.FullName}");
                            continue;
                        }

                        var targetSubDir = Path.Combine(currentDst, subDir.Name);
                        
                        // Guard: ensure targetSubDir is not sourceDir
                        if (!IsDescendantOf(targetSubDir, subDir.FullName))
                        {
                            workQueue.Enqueue((subDir.FullName, targetSubDir));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to process directory {subDir.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read subdirectories from {currentSrc}: {ex.Message}");
            }
        }
    }
}
