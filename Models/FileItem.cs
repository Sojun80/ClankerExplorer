using System;
using System.IO;
using Avalonia.Media;
using ClankerExplorer.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClankerExplorer.Models;

public partial class FileItem : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string ParentPath { get; set; } = string.Empty;

    private bool _isDirectory;
    public bool IsDirectory
    {
        get => _isDirectory;
        set
        {
            _isDirectory = value;
            _sizeBarFill = -1;
            _sizeBarBrush = null;
        }
    }

    public bool IsSymbolicLink { get; set; }

    private long _sizeBytes;
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            _sizeBytes = value;
            _sizeBarFill = -1;
            _sizeBarBrush = null;
        }
    }

    // Size Visualization Bar (Logarithmic Fill + Pre-allocated Shared SolidColorBrush)
    private double _sizeBarFill = -1;
    public double SizeBarFill
    {
        get
        {
            if (_sizeBarFill < 0)
            {
                _sizeBarFill = FileSizeVisualizerHelper.CalculateFill(_sizeBytes, _isDirectory);
            }
            return _sizeBarFill;
        }
    }

    public double SizeBarFillPercent => SizeBarFill * 100.0;
    public bool HasSizeBar => !_isDirectory && _sizeBytes > 0 && SizeBarFillPercent > 0.001;

    private IBrush? _sizeBarBrush;
    public IBrush SizeBarBrush
    {
        get
        {
            _sizeBarBrush ??= FileSizeVisualizerHelper.GetBrush(SizeBarFill);
            return _sizeBarBrush;
        }
    }

    public string FormattedSize { get; set; } = string.Empty;
    public DateTime ModifiedTime { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime AccessedTime { get; set; }
    public string FormattedModifiedTime => FormatSmartDateTime(ModifiedTime);
    public string FormattedCreatedTime => FormatSmartDateTime(CreatedTime);
    public string FormattedAccessedTime => FormatSmartDateTime(AccessedTime);

    public static string FormatSmartDateTime(DateTime dt)
    {
        if (dt == DateTime.MinValue || dt == default) return "—";

        var now = DateTime.Now;
        var today = DateTime.Today;
        var date = dt.Date;
        string timeStr = dt.ToString("h:mm tt"); // e.g. "8:31 AM", "4:15 PM"

        var elapsed = now - dt;

        // If modified recently (within the last 60 minutes today)
        if (date == today && elapsed.TotalSeconds >= -30 && elapsed.TotalMinutes < 60)
        {
            if (elapsed.TotalSeconds < 60)
            {
                return "Just now";
            }

            int exactMinutes = (int)elapsed.TotalMinutes;
            if (exactMinutes <= 10)
            {
                return exactMinutes == 1 ? "1 min ago" : $"{exactMinutes} mins ago";
            }

            // 11 to 59 minutes: Round to nearest 5 minutes (About 15 mins ago, About 20 mins ago, etc.)
            int roundedMinutes = (int)(Math.Round(elapsed.TotalMinutes / 5.0) * 5);
            if (roundedMinutes < 60)
            {
                return $"About {roundedMinutes} mins ago";
            }
        }

        if (date == today)
        {
            return $"Today at {timeStr}";
        }

        if (date == today.AddDays(-1))
        {
            return $"Yesterday at {timeStr}";
        }

        // Beyond yesterday: "8/7/26 at 4:40 PM"
        return $"{dt.Month}/{dt.Day}/{dt:yy} at {timeStr}";
    }
    
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

    // Thumbnail State
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private IImage? _thumbnailImage;

    public bool HasThumbnail => ThumbnailImage != null;

    [ObservableProperty]
    private bool _isThumbnailLoading;

    // Visual Helpers
    public string IconKind => IsDirectory ? "Folder" : GetFileIconKind(Extension);
    public string SizeDisplay => IsDirectory ? "—" : FormattedSize;
    public string ExtensionDisplay => IsDirectory ? "" : (string.IsNullOrEmpty(Extension) ? "—" : Extension.TrimStart('.').ToUpperInvariant());
    public bool HasExtensionBadge => !IsDirectory && !string.IsNullOrEmpty(Extension);
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
