using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClankerExplorer.Models;

public partial class FileItem : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string ParentPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public bool IsSymbolicLink { get; set; }
    public long SizeBytes { get; set; }
    public string FormattedSize { get; set; } = string.Empty;
    public DateTime ModifiedTime { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime AccessedTime { get; set; }
    public string FormattedModifiedTime => ModifiedTime == DateTime.MinValue ? "—" : ModifiedTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string FormattedCreatedTime => CreatedTime == DateTime.MinValue ? "—" : CreatedTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string FormattedAccessedTime => AccessedTime == DateTime.MinValue ? "—" : AccessedTime.ToString("yyyy-MM-dd HH:mm:ss");
    
    // Windows Attributes & Linux Permissions
    public bool IsHidden { get; set; }
    public bool IsSystem { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsArchive { get; set; }
    public string AttributesString { get; set; } = string.Empty;
    public string PermissionsString { get; set; } = string.Empty;
    public string OwnerGroupString { get; set; } = string.Empty;

    // Cut State & Dimming
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isCut;

    public double RowOpacity => IsCut ? 0.45 : 1.0;

    // Visual Helpers
    public string IconKind => IsDirectory ? "Folder" : GetFileIconKind(Extension);
    public string SizeDisplay => IsDirectory ? "<DIR>" : FormattedSize;
    public string ExtensionDisplay => IsDirectory ? "<DIR>" : (string.IsNullOrEmpty(Extension) ? "—" : Extension);
    public string ItemTypeDisplay => IsDirectory ? "File folder" : GetItemTypeDescription(Extension);

    private static string GetFileIconKind(string ext)
    {
        var lower = ext.ToLowerInvariant();
        return lower switch
        {
            ".cs" or ".ts" or ".js" or ".json" or ".py" or ".rs" or ".cpp" or ".h" or ".html" or ".css" or ".xaml" or ".xml" or ".sql" => "Code",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" or ".webp" or ".ico" => "Image",
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" => "Audio",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "Video",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" => "Archive",
            ".exe" or ".dll" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".sys" => "Binary",
            _ => "Document"
        };
    }

    private static string GetItemTypeDescription(string ext)
    {
        var lower = ext.ToLowerInvariant();
        return lower switch
        {
            ".txt" => "Text Document",
            ".log" => "Log File",
            ".json" => "JSON File",
            ".xml" or ".axaml" or ".xaml" => "XML/XAML Document",
            ".cs" => "C# Source File",
            ".js" => "JavaScript File",
            ".ts" => "TypeScript File",
            ".py" => "Python Script",
            ".cpp" or ".c" or ".h" or ".hpp" => "C/C++ Source File",
            ".html" or ".htm" => "HTML Document",
            ".css" => "Cascading Style Sheet",
            ".md" => "Markdown Document",
            ".zip" => "ZIP Archive",
            ".7z" => "7-Zip Archive",
            ".rar" => "RAR Archive",
            ".tar" or ".gz" or ".tgz" => "Tarball Archive",
            ".exe" => "Application",
            ".dll" => "Application Extension",
            ".png" => "PNG Image",
            ".jpg" or ".jpeg" => "JPEG Image",
            ".svg" => "SVG Image",
            ".pdf" => "PDF Document",
            ".mp3" or ".wav" or ".flac" => "Audio File",
            ".mp4" or ".mkv" or ".avi" => "Video File",
            _ => string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} File"
        };
    }
}
