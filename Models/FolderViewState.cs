using System;
using System.Collections.Generic;

namespace ClankerExplorer.Models;

public sealed class FolderViewState
{
    public string ViewMode { get; set; } = "Details";
    public double ThumbnailSize { get; set; } = 144;
    public string SortColumn { get; set; } = "Name";
    public bool SortAscending { get; set; } = true;

    public bool SmartColumnSizing { get; set; } = true;
    public bool ShowColumnExt { get; set; } = true;
    public bool ShowColumnSize { get; set; } = true;
    public bool ShowColumnDateModified { get; set; } = true;
    public bool ShowColumnDateCreated { get; set; } = true;
    public bool ShowColumnDateAccessed { get; set; }
    public bool ShowColumnAttributes { get; set; } = true;
    public bool ShowColumnItemType { get; set; }
    public bool ShowColumnPermissions { get; set; }
    public bool ShowColumnOwnerGroup { get; set; }

    public double ColumnWidthName { get; set; } = 280;
    public double ColumnWidthExt { get; set; } = 65;
    public double ColumnWidthSize { get; set; } = 95;
    public double ColumnWidthDateModified { get; set; } = 150;
    public double ColumnWidthDateCreated { get; set; } = 150;
    public double ColumnWidthDateAccessed { get; set; } = 150;
    public double ColumnWidthItemType { get; set; } = 110;
    public double ColumnWidthAttributes { get; set; } = 90;
    public double ColumnWidthPermissions { get; set; } = 110;
    public double ColumnWidthOwnerGroup { get; set; } = 110;
    public List<string> ColumnOrder { get; set; } = new();

    public double DetailsHorizontalOffset { get; set; }
    public double DetailsVerticalOffset { get; set; }
    public double ThumbnailVerticalOffset { get; set; }
    public string? DetailsTopItemPath { get; set; }
    public string? ThumbnailTopItemPath { get; set; }
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    public FolderViewState Clone()
    {
        var clone = (FolderViewState)MemberwiseClone();
        clone.ColumnOrder = ColumnOrder == null ? new List<string>() : new List<string>(ColumnOrder);
        return clone;
    }
}
