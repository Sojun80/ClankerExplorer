using System.Collections.Generic;

namespace ClankerExplorer.Models;

/// <summary>
/// A lightweight logical row used to give Avalonia a vertically virtualizable
/// thumbnail surface. Only the cells in realized rows receive visual controls.
/// </summary>
public sealed class ThumbnailRow
{
    public ThumbnailRow(IReadOnlyList<FileItem> items) => Items = items;

    public IReadOnlyList<FileItem> Items { get; }
}
