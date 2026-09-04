using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services.Search;

/// <summary>
/// Native recursive filesystem search provider.
/// Streams results progressively, prevents symlink/junction cycles,
/// and handles inaccessible or locked directories gracefully.
/// </summary>
public sealed class NativeSearchProvider : ISearchProvider
{
    public string Id => "native";
    public string DisplayName => "Native Filesystem Search";
    public bool IsAvailable => true;

    public async IAsyncEnumerable<SearchResultItem> SearchAsync(
        SearchRequest request,
        IProgress<SearchProgressReport>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string query = request.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            yield break;
        }

        var roots = ResolveSearchRoots(request);
        if (roots.Count == 0)
        {
            yield break;
        }

        bool isRecursive = request.Scope != SearchScope.CurrentFolder;
        var comparison = request.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var visitedDirs = new HashSet<string>(PathCycleComparer.Instance);
        var dirQueue = new Queue<string>();

        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                dirQueue.Enqueue(root);
            }
        }

        int foldersSkipped = 0;
        int matchesFound = 0;
        bool hasPathSeparators = query.Contains(Path.DirectorySeparatorChar) || query.Contains(Path.AltDirectorySeparatorChar);

        // Run enumeration inside Task.Yield to ensure we don't hold the caller synchronously
        await Task.Yield();

        while (dirQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string currentDir = dirQueue.Dequeue();

            string normalizedDir;
            try
            {
                normalizedDir = Path.GetFullPath(currentDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                normalizedDir = currentDir;
            }

            if (!visitedDirs.Add(normalizedDir))
            {
                // Already visited (avoid cycles)
                continue;
            }

            DirectoryInfo dirInfo;
            try
            {
                dirInfo = new DirectoryInfo(currentDir);
                if (!dirInfo.Exists)
                {
                    continue;
                }
            }
            catch (Exception)
            {
                foldersSkipped++;
                progress?.Report(new SearchProgressReport(foldersSkipped, matchesFound, currentDir));
                continue;
            }

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dirInfo.EnumerateFileSystemInfos();
            }
            catch (Exception)
            {
                foldersSkipped++;
                progress?.Report(new SearchProgressReport(foldersSkipped, matchesFound, currentDir));
                continue;
            }

            IEnumerator<FileSystemInfo> enumerator;
            try
            {
                enumerator = entries.GetEnumerator();
            }
            catch (Exception)
            {
                foldersSkipped++;
                progress?.Report(new SearchProgressReport(foldersSkipped, matchesFound, currentDir));
                continue;
            }

            using (enumerator)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileSystemInfo entry;
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }
                        entry = enumerator.Current;
                    }
                    catch (Exception)
                    {
                        // File disappeared or permission denied during enumeration
                        foldersSkipped++;
                        progress?.Report(new SearchProgressReport(foldersSkipped, matchesFound, currentDir));
                        break;
                    }

                    if (entry == null) continue;

                    bool isDir;
                    bool isReparsePoint;
                    try
                    {
                        isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                        isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    }
                    catch
                    {
                        continue;
                    }

                    // Query match check:
                    // Always match on entry filename. If query contains path separators, also match against FullName.
                    bool isMatch = false;
                    try
                    {
                        if (entry.Name.Contains(query, comparison))
                        {
                            isMatch = true;
                        }
                        else if (hasPathSeparators && entry.FullName.Contains(query, comparison))
                        {
                            isMatch = true;
                        }
                    }
                    catch
                    {
                        // Ignore string comparison errors
                    }

                    if (isMatch)
                    {
                        SearchResultItem? item = null;
                        try
                        {
                            long size = 0;
                            if (!isDir && entry is FileInfo fi)
                            {
                                try { size = fi.Length; } catch { }
                            }

                            item = new SearchResultItem
                            {
                                Name = entry.Name,
                                FullPath = entry.FullName,
                                ParentPath = currentDir,
                                IsDirectory = isDir,
                                SizeBytes = size,
                                FormattedSize = isDir ? "<DIR>" : FileSystemService.FormatBytes(size),
                                Extension = isDir ? string.Empty : (Path.GetExtension(entry.Name) ?? string.Empty),
                                ModifiedTime = entry.LastWriteTime
                            };
                        }
                        catch
                        {
                            // Inaccessible metadata
                        }

                        if (item != null)
                        {
                            matchesFound++;
                            yield return item;
                        }
                    }

                    // Recurse into subdirectories if enabled and not a symlink/junction
                    if (isDir && isRecursive && !isReparsePoint)
                    {
                        try
                        {
                            dirQueue.Enqueue(entry.FullName);
                        }
                        catch
                        {
                            // Ignore path length / encoding issues
                        }
                    }
                }
            }
        }

        // Final progress report upon completion
        progress?.Report(new SearchProgressReport(foldersSkipped, matchesFound, null));
    }

    private static List<string> ResolveSearchRoots(SearchRequest request)
    {
        var roots = new List<string>();

        if (request.Scope == SearchScope.Everywhere)
        {
            if (request.CustomRoots != null && request.CustomRoots.Count > 0)
            {
                roots.AddRange(request.CustomRoots);
                return roots;
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        try
                        {
                            if (drive.IsReady && drive.DriveType != DriveType.CDRom)
                            {
                                roots.Add(drive.RootDirectory.FullName);
                            }
                        }
                        catch
                        {
                            // Drive inaccessible or offline
                        }
                    }
                }
                catch
                {
                    // Ignore drive enumeration failure
                }

                if (roots.Count == 0)
                {
                    roots.Add(@"C:\");
                }
            }
            else
            {
                roots.Add("/");
            }

            return roots;
        }

        // CurrentFolder or CurrentFolderAndSubfolders
        string startPath = request.CurrentFolder ?? string.Empty;
        if (string.IsNullOrWhiteSpace(startPath))
        {
            startPath = FileSystemService.DefaultRootPath;
        }

        if (!string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
        {
            roots.Add(startPath);
        }

        return roots;
    }
}
