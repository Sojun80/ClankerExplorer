using System;
using Avalonia.Media;
using ClankerExplorer.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClankerExplorer.Models;

/// <summary>
/// Represents a search result item produced by an ISearchProvider.
/// </summary>
public partial class SearchResultItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private string _parentPath = string.Empty;

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private long _sizeBytes;

    [ObservableProperty]
    private string _formattedSize = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedModifiedTime))]
    private DateTime _modifiedTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemTypeDisplay))]
    private string _extension = string.Empty;

    public string FormattedModifiedTime => FileItem.FormatSmartDateTime(ModifiedTime);

    public string ItemTypeDisplay => IsDirectory
        ? "File folder"
        : (!string.IsNullOrEmpty(Extension) ? $"{Extension.TrimStart('.').ToUpperInvariant()} File" : "File");

    private IImage? _fileIcon;
    public IImage? FileIcon
    {
        get
        {
            _fileIcon ??= FileIconService.Instance.GetFileIcon(ToFileItem(), isLarge: false);
            return _fileIcon;
        }
    }

    public bool HasFileIcon => FileIcon != null;

    /// <summary>
    /// Creates a compatible FileItem instance for existing navigation and inspection APIs.
    /// </summary>
    public FileItem ToFileItem() => new()
    {
        Name = Name,
        FullPath = FullPath,
        ParentPath = ParentPath,
        IsDirectory = IsDirectory,
        SizeBytes = SizeBytes,
        FormattedSize = FormattedSize,
        Extension = Extension,
        ModifiedTime = ModifiedTime
    };
}
