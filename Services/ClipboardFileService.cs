using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using ClankerExplorer.AppLayer;
using ClankerExplorer.AppLayer.Operations;

namespace ClankerExplorer.Services;

public static class ClipboardFileService
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly List<string> _storedPaths = new();
    private static bool _isCut = false;
    private static readonly object _lock = new();
    private static readonly IFileOperationService _fileOperationService = new FileOperationService();

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

    /// <summary>
    /// Copies file/folder paths to both internal state and the system clipboard (Windows OLE CF_HDROP compatible).
    /// </summary>
    public static async Task CopyToSystemClipboardAsync(IClipboard? clipboard, IStorageProvider? storageProvider, IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(PathComparer).ToList();
        if (list.Count == 0) return;

        Copy(list);

        if (clipboard != null)
        {
            var dataObject = new DataObject();
            var storageItems = new List<IStorageItem>();

            if (storageProvider != null)
            {
                foreach (var p in list)
                {
                    try
                    {
                        var fileUri = new Uri(Path.GetFullPath(p));
                        if (Directory.Exists(p))
                        {
                            var f = storageProvider.TryGetFolderFromPathAsync(fileUri).GetAwaiter().GetResult();
                            if (f != null) storageItems.Add(f);
                        }
                        else if (File.Exists(p))
                        {
                            var f = storageProvider.TryGetFileFromPathAsync(fileUri).GetAwaiter().GetResult();
                            if (f != null) storageItems.Add(f);
                        }
                    }
                    catch { }
                }
            }

            if (storageItems.Count > 0)
            {
                dataObject.Set(DataFormats.Files, storageItems);
            }
            dataObject.Set(DataFormats.FileNames, list);
            dataObject.Set(DataFormats.Text, string.Join(Environment.NewLine, list));

            try
            {
                await clipboard.SetDataObjectAsync(dataObject);
            }
            catch
            {
                try { await clipboard.SetTextAsync(string.Join(Environment.NewLine, list)); } catch { }
            }
        }
    }

    /// <summary>
    /// Cuts file/folder paths to both internal state and the system clipboard.
    /// </summary>
    public static async Task CutToSystemClipboardAsync(IClipboard? clipboard, IStorageProvider? storageProvider, IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(PathComparer).ToList();
        if (list.Count == 0) return;

        Cut(list);

        if (clipboard != null)
        {
            var dataObject = new DataObject();
            var storageItems = new List<IStorageItem>();

            if (storageProvider != null)
            {
                foreach (var p in list)
                {
                    try
                    {
                        var fileUri = new Uri(Path.GetFullPath(p));
                        if (Directory.Exists(p))
                        {
                            var f = storageProvider.TryGetFolderFromPathAsync(fileUri).GetAwaiter().GetResult();
                            if (f != null) storageItems.Add(f);
                        }
                        else if (File.Exists(p))
                        {
                            var f = storageProvider.TryGetFileFromPathAsync(fileUri).GetAwaiter().GetResult();
                            if (f != null) storageItems.Add(f);
                        }
                    }
                    catch { }
                }
            }

            if (storageItems.Count > 0)
            {
                dataObject.Set(DataFormats.Files, storageItems);
            }
            dataObject.Set(DataFormats.FileNames, list);
            dataObject.Set(DataFormats.Text, string.Join(Environment.NewLine, list));

            try
            {
                await clipboard.SetDataObjectAsync(dataObject);
            }
            catch
            {
                try { await clipboard.SetTextAsync(string.Join(Environment.NewLine, list)); } catch { }
            }
        }
    }

    /// <summary>
    /// Extracts file and folder paths from the system clipboard, supporting DataFormats.Files, DataFormats.FileNames, and text paths.
    /// </summary>
    public static async Task<List<string>> ExtractPathsFromClipboardAsync(IClipboard? clipboard)
    {
        var paths = new HashSet<string>(PathComparer);
        if (clipboard == null) return paths.ToList();

        try
        {
            var filesData = await clipboard.GetDataAsync(DataFormats.Files);
            if (filesData is IEnumerable<IStorageItem> storageItems)
            {
                foreach (var item in storageItems)
                {
                    if (item?.Path != null)
                    {
                        string localPath = item.Path.LocalPath;
                        if (!string.IsNullOrEmpty(localPath) && (File.Exists(localPath) || Directory.Exists(localPath)))
                        {
                            paths.Add(localPath);
                        }
                    }
                }
            }
            else if (filesData is IEnumerable<string> strItems)
            {
                foreach (var p in strItems)
                {
                    if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                    {
                        paths.Add(p);
                    }
                }
            }
        }
        catch { }

        if (paths.Count > 0) return paths.ToList();

        try
        {
            var fileNames = await clipboard.GetDataAsync(DataFormats.FileNames);
            if (fileNames is IEnumerable<string> strList)
            {
                foreach (var p in strList)
                {
                    if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                    {
                        paths.Add(p);
                    }
                }
            }
        }
        catch { }

        if (paths.Count > 0) return paths.ToList();

        try
        {
            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var clean = line.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(clean) && (File.Exists(clean) || Directory.Exists(clean)))
                    {
                        paths.Add(clean);
                    }
                }
            }
        }
        catch { }

        return paths.ToList();
    }

    public static async Task<OperationJob?> EnqueuePasteFromSystemClipboardAsync(
        IClipboard? clipboard,
        string destinationDirectory,
        FileConflictPolicy conflictPolicy = FileConflictPolicy.Prompt)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) return null;

        List<string> sources = await ExtractPathsFromClipboardAsync(clipboard);
        bool isCutMode = IsCutMode;

        if (sources.Count == 0)
        {
            lock (_lock)
            {
                sources = _storedPaths.ToList();
            }
        }

        if (sources.Count == 0) return null;

        var request = new FileTransferRequest(
            sources,
            destinationDirectory,
            isCutMode ? FileTransferMode.Move : FileTransferMode.Copy,
            conflictPolicy);

        var job = _fileOperationService.QueueTransfer(request);

        _ = job.CompletionTask.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result != null && isCutMode)
            {
                lock (_lock)
                {
                    foreach (var succ in t.Result.SuccessfulSourcePaths)
                    {
                        var normalizedSucc = succ.TrimEnd('\\', '/');
                        _storedPaths.RemoveAll(p => string.Equals(p.TrimEnd('\\', '/'), normalizedSucc, PathComparison));
                    }

                    if (_storedPaths.Count == 0)
                    {
                        _isCut = false;
                    }
                }
                ClipboardChanged?.Invoke();
            }
        });

        return job;
    }

    public static async Task<(int successCount, List<string> failedPaths, List<string> createdDestinationPaths)> PasteFromSystemClipboardAsync(
        IClipboard? clipboard,
        string destinationDirectory,
        FileConflictPolicy conflictPolicy = FileConflictPolicy.AutoRename,
        CancellationToken cancellationToken = default)
    {
        var job = await EnqueuePasteFromSystemClipboardAsync(clipboard, destinationDirectory, conflictPolicy).ConfigureAwait(false);
        if (job == null)
        {
            return (0, new List<string>(), new List<string>());
        }

        using var reg = cancellationToken.Register(() => job.RequestCancel());
        var result = await job.CompletionTask.ConfigureAwait(false);
        return (result.SuccessfulSourcePaths.Count, result.FailedPaths.ToList(), result.CreatedDestinationPaths.ToList());
    }

    public static async Task<(int successCount, List<string> failedPaths, List<string> createdDestinationPaths)> PasteAsync(
        string destinationDirectory,
        FileConflictPolicy conflictPolicy = FileConflictPolicy.AutoRename,
        CancellationToken cancellationToken = default)
    {
        return await PasteFromSystemClipboardAsync(null, destinationDirectory, conflictPolicy, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<(List<string> successfulSourcePaths, List<string> failedPaths, List<string> createdDestinationPaths)> TransferFilesAsync(
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        bool isMove,
        CancellationToken cancellationToken = default)
    {
        var sources = sourcePaths?.ToList() ?? new List<string>();
        var request = new FileTransferRequest(
            sources,
            destinationDirectory,
            isMove ? FileTransferMode.Move : FileTransferMode.Copy,
            isMove ? FileConflictPolicy.Fail : FileConflictPolicy.AutoRename);
        var job = _fileOperationService.QueueTransfer(request);
        using var reg = cancellationToken.Register(() => job.RequestCancel());
        var result = await job.CompletionTask.ConfigureAwait(false);

        return (
            result.SuccessfulSourcePaths.ToList(),
            result.FailedPaths.ToList(),
            result.CreatedDestinationPaths.ToList());
    }
}
