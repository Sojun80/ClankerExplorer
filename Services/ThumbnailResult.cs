using Avalonia.Media.Imaging;

namespace ClankerExplorer.Services;

public sealed record ThumbnailResult(
    ThumbnailRequest Request,
    Bitmap? Bitmap,
    bool FromMemoryCache,
    bool FromDiskCache,
    bool Success);
