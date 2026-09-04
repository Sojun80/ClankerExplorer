using System;
using System.Collections.Generic;
using System.Linq;

namespace ClankerExplorer.Services.Metadata;

/// <summary>
/// A single metadata key-value entry.
/// </summary>
public class MetadataItem
{
    public string Key { get; }
    public string Value { get; }
    public string? SecondaryValue { get; }
    public bool IsCopyable { get; }
    public bool IsMonospace { get; }
    public string? Badge { get; }

    public MetadataItem(string key, string value, string? secondaryValue = null, bool isCopyable = true, bool isMonospace = false, string? badge = null)
    {
        Key = key;
        Value = value;
        SecondaryValue = secondaryValue;
        IsCopyable = isCopyable;
        IsMonospace = isMonospace;
        Badge = badge;
    }
}

/// <summary>
/// A logical section grouping related metadata items (e.g. General, Media, Dates, Attributes).
/// </summary>
public class MetadataSection
{
    public string Title { get; }
    public string Icon { get; }
    public IReadOnlyList<MetadataItem> Items { get; }
    public bool HasItems => Items.Count > 0;
    public bool IsExpanded { get; set; } = true;

    public MetadataSection(string title, string icon, IEnumerable<MetadataItem> items)
    {
        Title = title;
        Icon = icon;
        Items = items.ToList();
    }
}

/// <summary>
/// Complete aggregated metadata representation for a filesystem item.
/// </summary>
public class FileMetadata
{
    public string FilePath { get; }
    public string ItemName { get; }
    public bool IsDirectory { get; }
    public long SizeBytes { get; }
    public string FormattedSize { get; }
    public DateTime ModifiedTimeUtc { get; }
    public string QuickTypeDisplay { get; }
    public IReadOnlyList<MetadataSection> Sections { get; }

    public FileMetadata(
        string filePath,
        string itemName,
        bool isDirectory,
        long sizeBytes,
        string formattedSize,
        DateTime modifiedTimeUtc,
        string quickTypeDisplay,
        IEnumerable<MetadataSection> sections)
    {
        FilePath = filePath;
        ItemName = itemName;
        IsDirectory = isDirectory;
        SizeBytes = sizeBytes;
        FormattedSize = formattedSize;
        ModifiedTimeUtc = modifiedTimeUtc;
        QuickTypeDisplay = quickTypeDisplay;
        Sections = sections.Where(s => s.HasItems).ToList();
    }

    public MetadataSection? FindSection(string title) =>
        Sections.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase));

    public string? GetItemValue(string sectionTitle, string key)
    {
        var section = FindSection(sectionTitle);
        var item = section?.Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
        return item?.Value;
    }
}
