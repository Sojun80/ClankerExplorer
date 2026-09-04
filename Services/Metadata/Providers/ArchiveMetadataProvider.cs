using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.Services.Preview;

namespace ClankerExplorer.Services.Metadata.Providers;

/// <summary>
/// Extracts archive metadata: archive type, entry count, packed/unpacked sizes, compression ratio, encryption.
/// </summary>
public class ArchiveMetadataProvider : IMetadataProvider
{
    public int Order => 10;

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".cab", ".iso", ".wim"
    };

    public bool CanHandle(MetadataExtractionContext context)
    {
        return !context.IsDirectory && ArchiveExtensions.Contains(context.Extension);
    }

    public async Task ProvideMetadataAsync(MetadataExtractionContext context, CancellationToken cancellationToken)
    {
        string path = context.FilePath;
        if (!File.Exists(path)) return;

        string ext = context.Extension.ToLowerInvariant();
        string archiveType = GetArchiveTypeName(ext);
        context.AddItem("Archive", "📦", "Archive Type", archiveType, isCopyable: true);

        if (ext == ".zip")
        {
            await ExtractZipMetadataAsync(context, path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ExtractExternalArchiveMetadataAsync(context, path, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task ExtractZipMetadataAsync(MetadataExtractionContext context, string path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                int fileCount = 0;
                int folderCount = 0;
                long totalUncompressed = 0;
                long totalCompressed = 0;
                bool isEncrypted = false;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                // Quick scan of ZIP headers to check bit 0 (encryption flag)
                try
                {
                    using var reader = new BinaryReader(stream, System.Text.Encoding.Default, leaveOpen: true);
                    while (stream.Position < stream.Length - 30)
                    {
                        uint sig = reader.ReadUInt32();
                        if (sig == 0x04034B50) // Local file header signature
                        {
                            reader.ReadUInt16(); // version needed
                            ushort generalFlag = reader.ReadUInt16();
                            if ((generalFlag & 0x0001) != 0)
                            {
                                isEncrypted = true;
                                break;
                            }
                            // Skip to next or stop early after checking first few headers
                            stream.Seek(22, SeekOrigin.Current); // skip remaining local header
                            uint cSize = reader.ReadUInt32();
                            stream.Seek(cSize + reader.ReadUInt16() + reader.ReadUInt16(), SeekOrigin.Current);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                catch { }

                stream.Seek(0, SeekOrigin.Begin);
                cancellationToken.ThrowIfCancellationRequested();

                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool isDir = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                    if (isDir)
                    {
                        folderCount++;
                    }
                    else
                    {
                        fileCount++;
                        totalUncompressed += entry.Length;
                        totalCompressed += entry.CompressedLength;
                    }
                }

                int totalItems = fileCount + folderCount;
                string entriesDisplay = folderCount > 0
                    ? $"{totalItems:N0} items ({fileCount:N0} files, {folderCount:N0} folders)"
                    : $"{fileCount:N0} {(fileCount == 1 ? "file" : "files")}";

                context.AddItem("Archive", "📦", "Entries", entriesDisplay, isCopyable: true, isMonospace: true);

                if (totalUncompressed > 0)
                {
                    context.AddItem("Archive", "📦", "Unpacked Size", $"{FileSystemService.FormatBytes(totalUncompressed)} ({totalUncompressed:N0} bytes)", isCopyable: true, isMonospace: true);
                    context.AddItem("Archive", "📦", "Packed Size", $"{FileSystemService.FormatBytes(totalCompressed)} ({totalCompressed:N0} bytes)", isCopyable: true, isMonospace: true);

                    double ratio = (1.0 - ((double)totalCompressed / totalUncompressed)) * 100.0;
                    context.AddItem("Archive", "📦", "Compression Ratio", $"{Math.Clamp(ratio, 0, 100):F0}% space saved", isCopyable: true, isMonospace: true);
                }

                context.AddItem("Archive", "📦", "Encryption", isEncrypted ? "Password Protected (Encrypted)" : "None", isCopyable: false, badge: isEncrypted ? "Encrypted" : null);
            }
            catch { }
        }, cancellationToken);
    }

    private async Task ExtractExternalArchiveMetadataAsync(MetadataExtractionContext context, string path, CancellationToken cancellationToken)
    {
        try
        {
            var zipResult = await ZipPreviewService.Instance.LoadArchivePreviewAsync(path, cancellationToken).ConfigureAwait(false);
            if (zipResult.Success && zipResult.TotalFileCount > 0)
            {
                int totalItems = zipResult.TotalFileCount + zipResult.TotalFolderCount;
                string entriesDisplay = zipResult.TotalFolderCount > 0
                    ? $"{totalItems:N0} items ({zipResult.TotalFileCount:N0} files, {zipResult.TotalFolderCount:N0} folders)"
                    : $"{zipResult.TotalFileCount:N0} {(zipResult.TotalFileCount == 1 ? "file" : "files")}";

                context.AddItem("Archive", "📦", "Entries", entriesDisplay, isCopyable: true, isMonospace: true);

                if (zipResult.TotalUncompressedBytes > 0)
                {
                    context.AddItem("Archive", "📦", "Unpacked Size", $"{FileSystemService.FormatBytes(zipResult.TotalUncompressedBytes)} ({zipResult.TotalUncompressedBytes:N0} bytes)", isCopyable: true, isMonospace: true);
                    context.AddItem("Archive", "📦", "Packed Size", $"{FileSystemService.FormatBytes(zipResult.TotalCompressedBytes)} ({zipResult.TotalCompressedBytes:N0} bytes)", isCopyable: true, isMonospace: true);
                    context.AddItem("Archive", "📦", "Compression Ratio", $"{zipResult.OverallRatio} space saved", isCopyable: true, isMonospace: true);
                }
            }
        }
        catch { }
    }

    private static string GetArchiveTypeName(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".zip" => "ZIP Compressed Archive (.zip)",
            ".7z" => "7-Zip Archive (.7z)",
            ".rar" => "RAR Compressed Archive (.rar)",
            ".tar" => "Tape Archive (.tar)",
            ".gz" or ".tgz" => "GZip Compressed Tarball",
            ".bz2" or ".tbz2" => "BZip2 Compressed Tarball",
            ".xz" or ".txz" => "XZ Compressed Archive",
            ".cab" => "Microsoft Cabinet Archive (.cab)",
            ".iso" => "Optical Disc Image (.iso)",
            ".wim" => "Windows Imaging Format (.wim)",
            _ => $"{ext.ToUpperInvariant().TrimStart('.')} Archive"
        };
    }
}
