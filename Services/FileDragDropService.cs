using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace ClankerExplorer.Services;

/// <summary>
/// Service providing Windows Shell-compliant file drag-and-drop resolution,
/// path extraction from OLE DataObjects, volume-aware Move/Copy determination,
/// and destination validity checks.
/// </summary>
public static class FileDragDropService
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full) ?? string.Empty;
            if (string.Equals(full, root, PathComparison))
                return full;
            return full.TrimEnd('\\', '/');
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    /// <summary>
    /// Extracts filesystem paths from an IDataObject, supporting DataFormats.Files,
    /// DataFormats.FileNames, and newline-delimited text representations.
    /// </summary>
    public static List<string> ExtractPaths(IDataObject? data)
    {
        if (data == null) return new List<string>();

        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // 1. Storage items from DataFormats.Files
        var files = data.GetFiles();
        if (files != null)
        {
            foreach (var item in files)
            {
                if (item?.Path != null)
                {
                    string localPath = item.Path.LocalPath;
                    if (!string.IsNullOrEmpty(localPath) && (File.Exists(localPath) || Directory.Exists(localPath)))
                    {
                        paths.Add(NormalizePath(localPath));
                    }
                }
            }
        }

        // 2. DataFormats.FileNames or string collection
        if (data.Contains(DataFormats.FileNames))
        {
            var fileNames = data.Get(DataFormats.FileNames);
            if (fileNames is IEnumerable<string> strList)
            {
                foreach (var p in strList)
                {
                    if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                    {
                        paths.Add(NormalizePath(p));
                    }
                }
            }
        }

        // 3. Fallback: Parse plain text lines as paths
        if (paths.Count == 0 && data.Contains(DataFormats.Text))
        {
            var text = data.GetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in lines)
                {
                    var clean = raw.Trim().Trim('"', '\'');
                    if (Uri.TryCreate(clean, UriKind.Absolute, out var uri) && uri.IsFile)
                    {
                        clean = uri.LocalPath;
                    }

                    if (!string.IsNullOrEmpty(clean) && (File.Exists(clean) || Directory.Exists(clean)))
                    {
                        paths.Add(NormalizePath(clean));
                    }
                }
            }
        }

        return paths.ToList();
    }

    /// <summary>
    /// Determines the standard Windows default drag effect (same volume = Move, different volume = Copy).
    /// </summary>
    public static DragDropEffects GetDefaultEffect(IEnumerable<string> sourcePaths, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) return DragDropEffects.None;

        string destRoot = Path.GetPathRoot(destinationDirectory) ?? string.Empty;

        foreach (var source in sourcePaths)
        {
            string sourceRoot = Path.GetPathRoot(source) ?? string.Empty;
            if (!string.Equals(sourceRoot, destRoot, PathComparison))
            {
                // Crossing volume boundary -> default to Copy
                return DragDropEffects.Copy;
            }
        }

        // Same volume -> default to Move
        return DragDropEffects.Move;
    }

    /// <summary>
    /// Resolves the effective DragDropEffects considering modifier keys (Ctrl = Copy, Shift = Move)
    /// and rejecting invalid operations (moving a folder into itself or descendant).
    /// </summary>
    public static DragDropEffects ResolveEffect(
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        KeyModifiers keyModifiers)
    {
        var sources = sourcePaths?.ToList() ?? new List<string>();
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return DragDropEffects.None;
        }

        string normalizedDest = destinationDirectory.TrimEnd('\\', '/');

        // Check if destination is invalid (e.g. source is parent of destination or source == destination)
        foreach (var source in sources)
        {
            string normalizedSource = source.TrimEnd('\\', '/');

            // Cannot drop onto itself or into its immediate containing directory (for move)
            if (string.Equals(normalizedSource, normalizedDest, PathComparison))
            {
                return DragDropEffects.None;
            }

            // Cannot drop a directory into its own subdirectory
            if (Directory.Exists(normalizedSource))
            {
                if (IsDescendantOf(normalizedDest, normalizedSource))
                {
                    return DragDropEffects.None;
                }
            }
        }

        // KeyModifier overrides:
        // Ctrl = Copy, Shift = Move
        if (keyModifiers.HasFlag(KeyModifiers.Control))
        {
            return DragDropEffects.Copy;
        }

        if (keyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return DragDropEffects.Move;
        }

        return GetDefaultEffect(sources, destinationDirectory);
    }

    private static bool IsDescendantOf(string targetPath, string basePath)
    {
        var normTarget = targetPath.TrimEnd('\\', '/');
        var normBase = basePath.TrimEnd('\\', '/');

        if (normTarget.Length <= normBase.Length) return false;

        if (normTarget.StartsWith(normBase, PathComparison))
        {
            char nextChar = normTarget[normBase.Length];
            return nextChar == '\\' || nextChar == '/';
        }

        return false;
    }

    private static readonly System.Reflection.ConstructorInfo? BclStorageFileCtor =
        typeof(StorageProviderExtensions).Assembly
            .GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile")?
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault();

    private static readonly System.Reflection.ConstructorInfo? BclStorageFolderCtor =
        typeof(StorageProviderExtensions).Assembly
            .GetType("Avalonia.Platform.Storage.FileIO.BclStorageFolder")?
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault();

    /// <summary>
    /// Synchronously resolves local filesystem paths into Avalonia storage items
    /// so the OLE drag gesture can enter DragDrop.DoDragDrop immediately without
    /// yielding the thread and losing the mouse input message frame.
    /// </summary>
    public static List<IStorageItem> ResolveStorageItems(IStorageProvider? storageProvider, IEnumerable<string> paths)
    {
        var items = new List<IStorageItem>();
        if (paths == null) return items;

        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;

            try
            {
                IStorageItem? item = null;
                var fileUri = new Uri(Path.GetFullPath(p));

                if (storageProvider != null)
                {
                    if (Directory.Exists(p))
                    {
                        item = storageProvider.TryGetFolderFromPathAsync(fileUri).GetAwaiter().GetResult();
                    }
                    else if (File.Exists(p))
                    {
                        item = storageProvider.TryGetFileFromPathAsync(fileUri).GetAwaiter().GetResult();
                    }
                }

                // Fallback to direct BclStorageItem instantiation if storageProvider is null or returned null
                if (item == null)
                {
                    if (Directory.Exists(p) && BclStorageFolderCtor != null)
                    {
                        item = BclStorageFolderCtor.Invoke(new object[] { new DirectoryInfo(p) }) as IStorageItem;
                    }
                    else if (File.Exists(p) && BclStorageFileCtor != null)
                    {
                        item = BclStorageFileCtor.Invoke(new object[] { new FileInfo(p) }) as IStorageItem;
                    }
                }

                if (item != null)
                {
                    items.Add(item);
                }
            }
            catch { }
        }

        return items;
    }
}
