using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClankerExplorer.Services.Metadata;

/// <summary>
/// Context passed to metadata providers during extraction.
/// Accumulates metadata items grouped into logical sections.
/// </summary>
public class MetadataExtractionContext
{
    private readonly List<SectionBuilder> _sections = new();
    private readonly object _lock = new();

    public string FilePath { get; }
    public string ItemName { get; }
    public bool IsDirectory { get; }
    public string Extension { get; }
    public long SizeBytes { get; }
    public string FormattedSize { get; }
    public DateTime ModifiedTimeUtc { get; }
    public string QuickTypeDisplay { get; set; } = string.Empty;

    public MetadataExtractionContext(string filePath)
    {
        FilePath = filePath;
        bool isDir = Directory.Exists(filePath);
        IsDirectory = isDir;

        if (isDir)
        {
            var di = new DirectoryInfo(filePath);
            ItemName = di.Name;
            Extension = string.Empty;
            SizeBytes = 0;
            FormattedSize = "<DIR>";
            ModifiedTimeUtc = di.LastWriteTimeUtc;
            QuickTypeDisplay = "File folder";
        }
        else if (File.Exists(filePath))
        {
            var fi = new FileInfo(filePath);
            ItemName = fi.Name;
            Extension = fi.Extension.ToLowerInvariant();
            SizeBytes = fi.Length;
            FormattedSize = FileSystemService.FormatBytes(fi.Length);
            ModifiedTimeUtc = fi.LastWriteTimeUtc;
            QuickTypeDisplay = string.IsNullOrEmpty(Extension) ? "File" : $"{Extension.TrimStart('.').ToUpperInvariant()} File";
        }
        else
        {
            ItemName = Path.GetFileName(filePath);
            Extension = Path.GetExtension(filePath).ToLowerInvariant();
            SizeBytes = 0;
            FormattedSize = "—";
            ModifiedTimeUtc = DateTime.UtcNow;
            QuickTypeDisplay = "Unknown";
        }
    }

    public void AddItem(
        string sectionTitle,
        string sectionIcon,
        string key,
        string value,
        string? secondaryValue = null,
        bool isCopyable = true,
        bool isMonospace = false,
        string? badge = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        lock (_lock)
        {
            var section = _sections.FirstOrDefault(s => string.Equals(s.Title, sectionTitle, StringComparison.OrdinalIgnoreCase));
            if (section == null)
            {
                section = new SectionBuilder(sectionTitle, sectionIcon);
                _sections.Add(section);
            }

            section.Items.Add(new MetadataItem(key, value, secondaryValue, isCopyable, isMonospace, badge));
        }
    }

    public void AddItems(string sectionTitle, string sectionIcon, IEnumerable<MetadataItem> items)
    {
        lock (_lock)
        {
            var section = _sections.FirstOrDefault(s => string.Equals(s.Title, sectionTitle, StringComparison.OrdinalIgnoreCase));
            if (section == null)
            {
                section = new SectionBuilder(sectionTitle, sectionIcon);
                _sections.Add(section);
            }

            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.Value))
                {
                    section.Items.Add(item);
                }
            }
        }
    }

    public IReadOnlyList<MetadataSection> BuildSections()
    {
        lock (_lock)
        {
            return _sections
                .Where(s => s.Items.Count > 0)
                .Select(s => new MetadataSection(s.Title, s.Icon, s.Items))
                .ToList();
        }
    }

    private class SectionBuilder
    {
        public string Title { get; }
        public string Icon { get; }
        public List<MetadataItem> Items { get; } = new();

        public SectionBuilder(string title, string icon)
        {
            Title = title;
            Icon = icon;
        }
    }
}
