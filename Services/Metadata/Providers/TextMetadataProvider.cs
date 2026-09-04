using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClankerExplorer.Services.Metadata.Providers;

/// <summary>
/// Extracts metadata for text documents and code source files: encoding, BOM, newline style, and line count.
/// Implemented to be memory-bounded and inexpensive.
/// </summary>
public class TextMetadataProvider : IMetadataProvider
{
    public int Order => 10;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".tsv", ".log", ".ini", ".cfg", ".config",
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".html", ".htm", ".css", ".scss", ".sass",
        ".xaml", ".axaml", ".yaml", ".yml", ".sql", ".sh", ".bat", ".cmd", ".ps1",
        ".cpp", ".c", ".h", ".hpp", ".rs", ".go", ".java", ".kt", ".swift", ".php",
        ".rb", ".lua", ".r", ".dart", ".proto", ".toml", ".env", ".gitignore", ".editorconfig"
    };

    public bool CanHandle(MetadataExtractionContext context)
    {
        if (context.IsDirectory) return false;
        if (TextExtensions.Contains(context.Extension)) return true;
        // Also handle files without extension if small (< 512KB)
        return string.IsNullOrEmpty(context.Extension) && context.SizeBytes > 0 && context.SizeBytes < 512 * 1024;
    }

    public Task ProvideMetadataAsync(MetadataExtractionContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            string path = context.FilePath;
            if (!File.Exists(path)) return;

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length == 0)
                {
                    context.AddItem("Text Details", "📝", "Encoding", "Empty file", isCopyable: false);
                    context.AddItem("Text Details", "📝", "Lines", "0 lines", isCopyable: true, isMonospace: true);
                    return;
                }

                // 1. BOM Detection
                byte[] bom = new byte[4];
                int bomRead = fs.Read(bom, 0, 4);

                string encoding = "UTF-8";
                string bomStatus = "None";

                if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                {
                    encoding = "UTF-8";
                    bomStatus = "Present (UTF-8 BOM)";
                }
                else if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                {
                    encoding = "UTF-16 LE (Unicode)";
                    bomStatus = "Present (UTF-16 LE BOM)";
                }
                else if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                {
                    encoding = "UTF-16 BE (Big Endian)";
                    bomStatus = "Present (UTF-16 BE BOM)";
                }
                else if (bomRead >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00)
                {
                    encoding = "UTF-32 LE";
                    bomStatus = "Present (UTF-32 LE BOM)";
                }
                else
                {
                    // Inspect first chunk to verify ASCII / UTF-8
                    fs.Seek(0, SeekOrigin.Begin);
                    byte[] sample = new byte[Math.Min(fs.Length, 16384)];
                    int sampleRead = fs.Read(sample, 0, sample.Length);

                    if (IsPureAscii(sample, sampleRead))
                    {
                        encoding = "ASCII / UTF-8";
                    }
                    else if (IsValidUtf8(sample, sampleRead))
                    {
                        encoding = "UTF-8 (no BOM)";
                    }
                    else
                    {
                        encoding = "ANSI / Windows-1252";
                    }
                }

                context.AddItem("Text Details", "📝", "Encoding", encoding, isCopyable: true);
                if (bomStatus != "None")
                {
                    context.AddItem("Text Details", "📝", "Byte Order Mark", bomStatus, isCopyable: true, badge: "BOM");
                }

                // 2. Line Count and Newline Style
                fs.Seek(0, SeekOrigin.Begin);
                byte[] buffer = new byte[64 * 1024];
                long newlineCount = 0;
                long crlfCount = 0;
                long lfCount = 0;
                long crCount = 0;
                bool prevWasCr = false;
                bool endsWithNewline = false;

                int bytesRead;
                long totalScanned = 0;
                const long maxScanBytes = 4 * 1024 * 1024; // Scan up to 4MB for exact line count

                while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalScanned += bytesRead;

                    for (int i = 0; i < bytesRead; i++)
                    {
                        byte b = buffer[i];
                        if (b == (byte)'\r')
                        {
                            if (prevWasCr) crCount++;
                            prevWasCr = true;
                        }
                        else if (b == (byte)'\n')
                        {
                            newlineCount++;
                            endsWithNewline = true;
                            if (prevWasCr)
                            {
                                crlfCount++;
                                prevWasCr = false;
                            }
                            else
                            {
                                lfCount++;
                            }
                        }
                        else
                        {
                            endsWithNewline = false;
                            if (prevWasCr)
                            {
                                crCount++;
                                prevWasCr = false;
                            }
                        }
                    }

                    if (totalScanned >= maxScanBytes && fs.Position < fs.Length)
                    {
                        break;
                    }
                }

                if (prevWasCr) crCount++;

                long lineCount = endsWithNewline ? newlineCount : (newlineCount + 1);
                if (lineCount < 1) lineCount = 1;

                string newlineStyle;
                if (crlfCount > 0 && lfCount == 0 && crCount == 0)
                {
                    newlineStyle = "CRLF (Windows \\r\\n)";
                }
                else if (lfCount > 0 && crlfCount == 0 && crCount == 0)
                {
                    newlineStyle = "LF (Unix / macOS \\n)";
                }
                else if (crCount > 0 && crlfCount == 0 && lfCount == 0)
                {
                    newlineStyle = "CR (Classic Mac \\r)";
                }
                else if (crlfCount + lfCount + crCount > 0)
                {
                    newlineStyle = $"Mixed (CRLF: {crlfCount}, LF: {lfCount})";
                }
                else
                {
                    newlineStyle = "Single line";
                }

                context.AddItem("Text Details", "📝", "Line Endings", newlineStyle, isCopyable: true);

                string lineCountDisplay = totalScanned < fs.Length
                    ? $"{lineCount:N0}+ lines (sampled)"
                    : $"{lineCount:N0} {(lineCount == 1 ? "line" : "lines")}";
                context.AddItem("Text Details", "📝", "Line Count", lineCountDisplay, isCopyable: true, isMonospace: true);
            }
            catch { }
        }, cancellationToken);
    }

    private static bool IsPureAscii(byte[] data, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (data[i] >= 0x80) return false;
        }
        return true;
    }

    private static bool IsValidUtf8(byte[] bytes, int length)
    {
        int i = 0;
        while (i < length)
        {
            if (bytes[i] <= 0x7F)
            {
                i += 1;
            }
            else if ((bytes[i] & 0xE0) == 0xC0)
            {
                if (i + 1 >= length || (bytes[i + 1] & 0xC0) != 0x80) return false;
                i += 2;
            }
            else if ((bytes[i] & 0xF0) == 0xE0)
            {
                if (i + 2 >= length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80) return false;
                i += 3;
            }
            else if ((bytes[i] & 0xF8) == 0xF0)
            {
                if (i + 3 >= length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80 || (bytes[i + 3] & 0xC0) != 0x80) return false;
                i += 4;
            }
            else
            {
                return false;
            }
        }
        return true;
    }
}
