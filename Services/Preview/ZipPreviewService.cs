using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public string CompressionRatio
    {
        get
        {
            if (IsDirectory || UncompressedSizeBytes <= 0) return "-";
            double ratio = (1.0 - ((double)CompressedSizeBytes / UncompressedSizeBytes)) * 100.0;
            return $"{Math.Clamp(ratio, 0, 100):F0}%";
        }
    }
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
/// Fast asynchronous preview reader for ZIP, RAR, 7Z and compressed archives without extraction.
/// </summary>
public class ZipPreviewService
{
    private static readonly Lazy<ZipPreviewService> _instance = new(() => new ZipPreviewService());
    public static ZipPreviewService Instance => _instance.Value;

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".cab", ".iso", ".wim"
    };

    public bool IsArchiveFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return ArchiveExtensions.Contains(ext);
    }

    /// <summary>
    /// Asynchronously parses archive metadata to build entry list with Name, Size, and Compression %.
    /// </summary>
    public async Task<ZipPreviewResult> LoadArchivePreviewAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return new ZipPreviewResult { Success = false, ErrorMessage = "Archive file not found." };
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".zip")
        {
            return await LoadZipEntriesAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return await LoadExternalArchiveEntriesAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ZipPreviewResult> LoadZipEntriesAsync(string filePath, CancellationToken cancellationToken)
    {
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
                        string[] parts = normalized.Split('/');
                        string currentPath = "";
                        for (int i = 0; i < parts.Length - 1; i++)
                        {
                            currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : $"{currentPath}/{parts[i]}";
                            folderSet.Add(currentPath);
                        }
                    }
                }

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
                        Depth = depth,
                        Icon = FileIconService.Instance.GetFolderIcon()
                    });
                    folderCount++;
                }

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
                        Depth = depth,
                        Icon = FileIconService.Instance.GetExtensionIcon(Path.GetExtension(fileName))
                    });

                    fileCount++;
                    totalUncompressed += entry.Length;
                    totalCompressed += entry.CompressedLength;
                }

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

    private async Task<ZipPreviewResult> LoadExternalArchiveEntriesAsync(string filePath, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Check for 7z CLI in Program Files
                string cli = @"C:\Program Files\7-Zip\7z.exe";
                if (!File.Exists(cli)) cli = @"C:\Program Files (x86)\7-Zip\7z.exe";

                if (!File.Exists(cli))
                {
                    // Fallback to basic file info
                    var fi = new FileInfo(filePath);
                    return new ZipPreviewResult
                    {
                        Success = true,
                        Entries = new List<ZipEntryItem>
                        {
                            new()
                            {
                                Name = Path.GetFileName(filePath),
                                FullPath = Path.GetFileName(filePath),
                                IsDirectory = false,
                                UncompressedSizeBytes = fi.Length,
                                CompressedSizeBytes = fi.Length,
                                Icon = FileIconService.Instance.GetExtensionIcon(Path.GetExtension(filePath))
                            }
                        },
                        TotalFileCount = 1,
                        TotalUncompressedBytes = fi.Length,
                        TotalCompressedBytes = fi.Length
                    };
                }

                var psi = new ProcessStartInfo
                {
                    FileName = cli,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("l");
                psi.ArgumentList.Add("-slt");
                psi.ArgumentList.Add(filePath);

                using var proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("Failed to spawn 7z reader");

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                var entries = new List<ZipEntryItem>();
                int fileCount = 0;
                int folderCount = 0;
                long totalUncompressed = 0;
                long totalCompressed = 0;

                var blocks = output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var block in blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var line in lines)
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            string key = line.Substring(0, eq).Trim();
                            string val = line.Substring(eq + 1).Trim();
                            dict[key] = val;
                        }
                    }

                    if (dict.TryGetValue("Path", out var path) && !string.IsNullOrEmpty(path))
                    {
                        bool isDir = dict.TryGetValue("Folder", out var f) && f == "+";
                        long size = dict.TryGetValue("Size", out var sz) && long.TryParse(sz, out var sVal) ? sVal : 0;
                        long packed = dict.TryGetValue("Packed Size", out var psz) && long.TryParse(psz, out var pVal) ? pVal : size;

                        string name = path.Contains('\\') ? path.Substring(path.LastIndexOf('\\') + 1) : path;
                        int depth = path.Count(c => c == '\\');

                        entries.Add(new ZipEntryItem
                        {
                            Name = isDir ? name + "/" : name,
                            FullPath = path.Replace('\\', '/'),
                            IsDirectory = isDir,
                            UncompressedSizeBytes = size,
                            CompressedSizeBytes = packed,
                            Depth = depth,
                            Icon = isDir ? FileIconService.Instance.GetFolderIcon() : FileIconService.Instance.GetExtensionIcon(Path.GetExtension(name))
                        });

                        if (isDir) folderCount++;
                        else
                        {
                            fileCount++;
                            totalUncompressed += size;
                            totalCompressed += packed;
                        }
                    }
                }

                return new ZipPreviewResult
                {
                    Success = true,
                    Entries = entries,
                    TotalFileCount = fileCount,
                    TotalFolderCount = folderCount,
                    TotalUncompressedBytes = totalUncompressed,
                    TotalCompressedBytes = totalCompressed
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ZipPreviewResult { Success = false, ErrorMessage = ex.Message };
            }
        }, cancellationToken);
    }
}
