using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public enum ThumbnailPriority
{
    Visible = 0,
    Prefetch = 1
}

public sealed class ThumbnailRequest
{
    public FileItem Item { get; }
    public string Path { get; }
    public long FileSize { get; }
    public DateTime ModifiedTime { get; }
    public int TargetSize { get; }
    public ThumbnailPriority Priority { get; }
    public int Generation { get; }
    public CancellationToken CancellationToken { get; }
    public TaskCompletionSource<Bitmap?> Completion { get; }
    public string Key { get; }

    public ThumbnailRequest(
        FileItem item,
        int targetSize,
        ThumbnailPriority priority,
        int generation,
        CancellationToken cancellationToken,
        TaskCompletionSource<Bitmap?> completion,
        string key)
    {
        Item = item;
        Path = item.FullPath;
        FileSize = item.SizeBytes;
        ModifiedTime = item.ModifiedTime;
        TargetSize = targetSize;
        Priority = priority;
        Generation = generation;
        CancellationToken = cancellationToken;
        Completion = completion;
        Key = key;
    }
}
