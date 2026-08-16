using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.Services.Preview;

public class ZipEntryItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long UncompressedSizeBytes { get; set; }
    public long CompressedSizeBytes { get; set; }
    public string FormattedSize => IsDirectory ? "<DIR>" : FileSystemService.FormatBytes(UncompressedSizeBytes);
    public string FormattedCompressedSize => IsDirectory ? "-" : FileSystemService.FormatBytes(CompressedSizeBytes);
    public string CompressionRatio
    {
        get
        {
            if (IsDirectory || UncompressedSizeBytes <= 0) return "-";
            double ratio = (1.0 - ((double)CompressedSizeBytes / UncompressedSizeBytes)) * 100.0;
            return $"{Math.Clamp(ratio, 0, 100):F0}%";
        }
    }
    public string ModifiedTime { get; set; } = string.Empty;
    public int Depth { get; set; }
    public string IndentPadding => new string(' ', Depth * 4);
    public IImage? Icon { get; set; }
}

public class ZipPreviewResult
{
    public bool Success { get; set; }
    public List<ZipEntryItem> Entries { get; set; } = new();
    public int TotalFileCount { get; set; }
    public int TotalFolderCount { get; set; }
    public long TotalUncompressedBytes { get; set; }
    public long TotalCompressedBytes { get; set; }
    public string FormattedTotalSize => FileSystemService.FormatBytes(TotalUncompressedBytes);
    public string FormattedTotalCompressedSize => FileSystemService.FormatBytes(TotalCompressedBytes);
    public string OverallRatio
    {
        get
        {
            if (TotalUncompressedBytes <= 0) return "0%";
            double ratio = (1.0 - ((double)TotalCompressedBytes / TotalUncompressedBytes)) * 100.0;
            return $"{Math.Clamp(ratio, 0, 100):F0}%";
        }
    }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Fast asynchronous preview reader for ZIP archives without extracting files.
/// </summary>
public class ZipPreviewService
{
    private static readonly Lazy<ZipPreviewService> _instance = new(() => new ZipPreviewService());
    public static ZipPreviewService Instance => _instance.Value;

    public bool IsZipFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asynchronously parses the central directory of a ZIP archive to build an entry list.
    /// </summary>
    public async Task<ZipPreviewResult> LoadZipPreviewAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return new ZipPreviewResult { Success = false, ErrorMessage = "Archive file not found." };
        }

        return await Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entries = new List<ZipEntryItem>();
                int fileCount = 0;
                int folderCount = 0;
                long totalUncompressed = 0;
                long totalCompressed = 0;

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

                var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string normalized = entry.FullName.Replace('\\', '/').Trim('/');
                    bool isDir = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

                    if (isDir)
                    {
                        folderSet.Add(normalized);
                    }
                    else
                    {
                        // Collect implicit parent directories if not already recorded
                        string[] parts = normalized.Split('/');
                        string currentPath = "";
                        for (int i = 0; i < parts.Length - 1; i++)
                        {
                            currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : $"{currentPath}/{parts[i]}";
                            folderSet.Add(currentPath);
                        }
                    }
                }

                // Add directory items
                foreach (var folder in folderSet.OrderBy(f => f))
                {
                    string folderName = folder.Contains('/') ? folder.Substring(folder.LastIndexOf('/') + 1) : folder;
                    int depth = folder.Count(c => c == '/');

                    entries.Add(new ZipEntryItem
                    {
                        Name = folderName + "/",
                        FullPath = folder,
                        IsDirectory = true,
                        UncompressedSizeBytes = 0,
                        CompressedSizeBytes = 0,
                        ModifiedTime = "-",
                        Depth = depth,
                        Icon = FileIconService.Instance.GetFolderIcon()
                    });
                    folderCount++;
                }

                // Add file items
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string normalized = entry.FullName.Replace('\\', '/').Trim('/');
                    bool isDir = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                    if (isDir) continue;

                    string fileName = entry.Name;
                    if (string.IsNullOrEmpty(fileName) && normalized.Contains('/'))
                    {
                        fileName = normalized.Substring(normalized.LastIndexOf('/') + 1);
                    }

                    int depth = normalized.Count(c => c == '/');

                    entries.Add(new ZipEntryItem
                    {
                        Name = fileName,
                        FullPath = normalized,
                        IsDirectory = false,
                        UncompressedSizeBytes = entry.Length,
                        CompressedSizeBytes = entry.CompressedLength,
                        ModifiedTime = entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        Depth = depth,
                        Icon = FileIconService.Instance.GetExtensionIcon(Path.GetExtension(fileName))
                    });

                    fileCount++;
                    totalUncompressed += entry.Length;
                    totalCompressed += entry.CompressedLength;
                }

                // Order by: directories first, then files, grouped hierarchically
                var sorted = entries
                    .OrderBy(e => e.FullPath.Contains('/') ? e.FullPath.Substring(0, e.FullPath.LastIndexOf('/')) : "")
                    .ThenByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new ZipPreviewResult
                {
                    Success = true,
                    Entries = sorted,
                    TotalFileCount = fileCount,
                    TotalFolderCount = folderCount,
                    TotalUncompressedBytes = totalUncompressed,
                    TotalCompressedBytes = totalCompressed
                };
            }
            catch (InvalidDataException)
            {
                return new ZipPreviewResult { Success = false, ErrorMessage = "The archive is corrupt or in an unsupported format." };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ZipPreviewResult { Success = false, ErrorMessage = $"Cannot open archive: {ex.Message}" };
            }
        }, cancellationToken);
    }
}
