using System;
using System.Collections.Generic;

namespace ClankerExplorer.Models;

public class QuickAccessItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string IconKind { get; set; } = "Folder";
    public bool IsCustom { get; set; }
}

public class WslDistroItem
{
    public string Name { get; set; } = string.Empty;
    public string DistroName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string HomePath { get; set; } = string.Empty;
}

public class FrequentFolderItem
{
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int VisitCount { get; set; }
    public DateTime LastVisited { get; set; } = DateTime.Now;

    public string FormattedTime => LastVisited.Date == DateTime.Today
        ? LastVisited.ToString("h:mm tt")
        : LastVisited.ToString("MMM d");
}

public class BatchRenameRule
{
    public string Mode { get; set; } = "replace"; // replace, prefix_suffix, numbering, change_case
    public string FindText { get; set; } = string.Empty;
    public string ReplaceText { get; set; } = string.Empty;
    public bool IsRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public int StartNumber { get; set; } = 1;
    public int Padding { get; set; } = 3;
    public string CaseMode { get; set; } = "lower"; // lower, upper, title
}

public class BatchRenameItem
{
    public string OriginalPath { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string NewName { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
    public bool WillChange => !string.Equals(OriginalName, NewName, StringComparison.Ordinal);
    public bool HasConflict { get; set; }
    public string StatusText => HasConflict ? "Conflict" : (WillChange ? "Rename" : "Unchanged");
}

public class HexRow
{
    public string Offset { get; set; } = string.Empty;
    public string HexBytes { get; set; } = string.Empty;
    public string AsciiText { get; set; } = string.Empty;
}

public class FilePreviewData
{
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FormattedSize { get; set; } = string.Empty;
    public string ModifiedTime { get; set; } = string.Empty;
    public string PreviewType { get; set; } = "text"; // "text", "binary", "image", "directory", "error"
    public string TextContent { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public List<HexRow>? HexRows { get; set; }
    public string? ErrorMessage { get; set; }
}

public class HashResult
{
    public string Sha256 { get; set; } = string.Empty;
    public string Md5 { get; set; } = string.Empty;
}
