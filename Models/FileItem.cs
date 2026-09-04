using System;
using System.IO;
using Avalonia.Media;
using ClankerExplorer.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClankerExplorer.Models;

public partial class FileItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExtensionDisplay))]
    [NotifyPropertyChangedFor(nameof(HasExtensionBadge))]
    [NotifyPropertyChangedFor(nameof(ItemTypeDisplay))]
    [NotifyPropertyChangedFor(nameof(IconKind))]
    private string _extension = string.Empty;

    partial void OnExtensionChanged(string value)
    {
        _fileIcon = null;
        _largeIcon = null;
        OnPropertyChanged(nameof(FileIcon));
        OnPropertyChanged(nameof(LargeIcon));
        OnPropertyChanged(nameof(HasFileIcon));
        OnPropertyChanged(nameof(HasLargeIcon));
    }

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private string _parentPath = string.Empty;

    private bool _isDirectory;
    public bool IsDirectory
    {
        get => _isDirectory;
        set
        {
            if (SetProperty(ref _isDirectory, value))
            {
                _sizeBarFill = -1;
                _sizeBarBrush = null;
                OnPropertyChanged(nameof(SizeDisplay));
                OnPropertyChanged(nameof(FormattedSize));
                OnPropertyChanged(nameof(SizeBarFill));
                OnPropertyChanged(nameof(SizeBarFillPercent));
                OnPropertyChanged(nameof(HasSizeBar));
                OnPropertyChanged(nameof(SizeBarBrush));
                OnPropertyChanged(nameof(ExtensionDisplay));
                OnPropertyChanged(nameof(HasExtensionBadge));
                OnPropertyChanged(nameof(ItemTypeDisplay));
                OnPropertyChanged(nameof(IconKind));
            }
        }
    }

    [ObservableProperty]
    private bool _isSymbolicLink;

    private long _sizeBytes;
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value))
            {
                _sizeBarFill = -1;
                _sizeBarBrush = null;
                OnPropertyChanged(nameof(SizeBarFill));
                OnPropertyChanged(nameof(SizeBarFillPercent));
                OnPropertyChanged(nameof(HasSizeBar));
                OnPropertyChanged(nameof(SizeBarBrush));
                OnPropertyChanged(nameof(SizeDisplay));
            }
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private string _formattedSize = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedModifiedTime))]
    private DateTime _modifiedTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedCreatedTime))]
    private DateTime _createdTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedAccessedTime))]
    private DateTime _accessedTime;

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
    [ObservableProperty]
    private bool _isHidden;

    [ObservableProperty]
    private bool _isSystem;

    [ObservableProperty]
    private bool _isReadOnly;

    [ObservableProperty]
    private bool _isArchive;

    [ObservableProperty]
    private string _attributesString = string.Empty;

    [ObservableProperty]
    private string _permissionsString = string.Empty;

    [ObservableProperty]
    private string _ownerGroupString = string.Empty;

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

    [ObservableProperty]
    private bool _isThumbnailSelected;

    // Inline Rename State
    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    // Drop Target Hover State
    [ObservableProperty]
    private bool _isDragOver;

    // File / Folder Icon (Windows-associated / extension cached)
    private IImage? _fileIcon;
    public IImage? FileIcon
    {
        get
        {
            _fileIcon ??= FileIconService.Instance.GetFileIcon(this, isLarge: false);
            return _fileIcon;
        }
        set => _fileIcon = value;
    }

    public bool HasFileIcon => FileIcon != null;

    // High-Resolution Large File / Folder Icon (Jumbo 256x256 for Thumbnail / Grid View)
    private IImage? _largeIcon;
    public IImage? LargeIcon
    {
        get
        {
            _largeIcon ??= FileIconService.Instance.GetFileIcon(this, isLarge: true);
            return _largeIcon;
        }
        set => _largeIcon = value;
    }

    public bool HasLargeIcon => LargeIcon != null;

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
