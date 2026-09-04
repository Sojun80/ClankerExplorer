using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services.Metadata.Providers;

/// <summary>
/// Core filesystem metadata provider.
/// Gathers name, path, type, logical size, timestamps, attributes, owner, and symlink/junction targets.
/// </summary>
public class FileSystemMetadataProvider : IMetadataProvider
{
    public int Order => 0; // Core provider runs first

    public bool CanHandle(MetadataExtractionContext context) => true; // Handles all files and directories

    public Task ProvideMetadataAsync(MetadataExtractionContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            string path = context.FilePath;
            bool isDir = context.IsDirectory;

            FileSystemInfo fsi;
            if (isDir)
            {
                fsi = new DirectoryInfo(path);
            }
            else if (File.Exists(path))
            {
                fsi = new FileInfo(path);
            }
            else
            {
                return;
            }

            // 1. General Section
            string fullPath = fsi.FullName;
            string parentDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string ext = isDir ? string.Empty : fsi.Extension;
            string typeDesc = isDir ? "File folder" : GetDetailedTypeDescription(ext);
            context.QuickTypeDisplay = typeDesc;

            context.AddItem("General", "📁", "Name", fsi.Name, isCopyable: true);
            context.AddItem("General", "📁", "Type", typeDesc, isCopyable: true);
            context.AddItem("General", "📁", "Location", parentDir, isCopyable: true);
            context.AddItem("General", "📁", "Full Path", fullPath, isCopyable: true, isMonospace: true);

            if (!isDir && fsi is FileInfo fi)
            {
                long size = fi.Length;
                string sizeFormatted = $"{FileSystemService.FormatBytes(size)} ({size:N0} bytes)";
                context.AddItem("General", "📁", "Size", sizeFormatted, isCopyable: true, isMonospace: true);
            }
            else
            {
                context.AddItem("General", "📁", "Size", "Folder", secondaryValue: null, isCopyable: false);
            }

            // Link / Reparse target where applicable
            try
            {
                if (fsi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    string? target = fsi.LinkTarget;
                    if (string.IsNullOrEmpty(target) && fsi is DirectoryInfo di)
                    {
                        var resolved = di.ResolveLinkTarget(returnFinalTarget: false);
                        target = resolved?.FullName;
                    }
                    else if (string.IsNullOrEmpty(target) && fsi is FileInfo fileInfo)
                    {
                        var resolved = fileInfo.ResolveLinkTarget(returnFinalTarget: false);
                        target = resolved?.FullName;
                    }

                    if (!string.IsNullOrEmpty(target))
                    {
                        context.AddItem("General", "📁", "Link Target", target, isCopyable: true, isMonospace: true, badge: "Symlink");
                    }
                }
            }
            catch { }

            // 2. Dates Section
            try
            {
                DateTime created = fsi.CreationTime;
                DateTime modified = fsi.LastWriteTime;
                DateTime accessed = fsi.LastAccessTime;

                context.AddItem("Dates", "📅", "Modified", FormatDate(modified), secondaryValue: FileItem.FormatSmartDateTime(modified), isCopyable: true, isMonospace: true);
                context.AddItem("Dates", "📅", "Created", FormatDate(created), secondaryValue: FileItem.FormatSmartDateTime(created), isCopyable: true, isMonospace: true);
                context.AddItem("Dates", "📅", "Accessed", FormatDate(accessed), secondaryValue: FileItem.FormatSmartDateTime(accessed), isCopyable: true, isMonospace: true);
            }
            catch { }

            // 3. Attributes & Security Section
            try
            {
                var attr = fsi.Attributes;
                string attrSummary = attr.ToString();
                context.AddItem("Attributes", "⚙️", "Flags", attrSummary, isCopyable: true, isMonospace: true);

                string readOnly = attr.HasFlag(FileAttributes.ReadOnly) ? "Yes" : "No";
                string hidden = attr.HasFlag(FileAttributes.Hidden) ? "Yes" : "No";
                string system = attr.HasFlag(FileAttributes.System) ? "Yes" : "No";
                string archive = attr.HasFlag(FileAttributes.Archive) ? "Yes" : "No";

                context.AddItem("Attributes", "⚙️", "Read-only", readOnly, isCopyable: false);
                context.AddItem("Attributes", "⚙️", "Hidden", hidden, isCopyable: false);
                if (attr.HasFlag(FileAttributes.System))
                {
                    context.AddItem("Attributes", "⚙️", "System", system, isCopyable: false, badge: "System");
                }
                if (attr.HasFlag(FileAttributes.Compressed))
                {
                    context.AddItem("Attributes", "⚙️", "Compressed", "Yes", isCopyable: false, badge: "NTFS Compressed");
                }
                if (attr.HasFlag(FileAttributes.Encrypted))
                {
                    context.AddItem("Attributes", "⚙️", "Encrypted", "Yes", isCopyable: false, badge: "EFS Encrypted");
                }

                // Windows Owner
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        FileSecurity? sec = null;
                        if (!isDir && fsi is FileInfo fileI)
                        {
                            sec = fileI.GetAccessControl();
                        }
                        else if (isDir && fsi is DirectoryInfo dirI)
                        {
                            var dirSec = dirI.GetAccessControl();
                            var owner = dirSec.GetOwner(typeof(NTAccount))?.Value;
                            if (!string.IsNullOrEmpty(owner))
                            {
                                context.AddItem("Attributes", "⚙️", "Owner", owner, isCopyable: true);
                            }
                        }

                        if (sec != null)
                        {
                            var owner = sec.GetOwner(typeof(NTAccount))?.Value;
                            if (!string.IsNullOrEmpty(owner))
                            {
                                context.AddItem("Attributes", "⚙️", "Owner", owner, isCopyable: true);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }, cancellationToken);
    }

    private static string FormatDate(DateTime dt)
    {
        if (dt == DateTime.MinValue || dt == default) return "—";
        return dt.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string GetDetailedTypeDescription(string ext)
    {
        var lower = ext.ToLowerInvariant();
        return lower switch
        {
            ".txt" => "Text Document (.txt)",
            ".log" => "Log File (.log)",
            ".json" => "JSON File (.json)",
            ".xml" => "XML Document (.xml)",
            ".xaml" or ".axaml" => "XAML UI Document",
            ".cs" => "C# Source File (.cs)",
            ".js" => "JavaScript File (.js)",
            ".ts" => "TypeScript File (.ts)",
            ".py" => "Python Script (.py)",
            ".cpp" or ".c" or ".h" or ".hpp" => "C/C++ Source File",
            ".html" or ".htm" => "HTML Document",
            ".css" => "Cascading Style Sheet (.css)",
            ".md" => "Markdown Document (.md)",
            ".pdf" => "PDF Document (.pdf)",
            ".zip" => "ZIP Archive (.zip)",
            ".7z" => "7-Zip Archive (.7z)",
            ".rar" => "RAR Archive (.rar)",
            ".tar" or ".gz" or ".tgz" => "Tarball Archive",
            ".exe" => "Windows Executable Application (.exe)",
            ".dll" => "Windows Dynamic Link Library (.dll)",
            ".png" => "PNG Image (.png)",
            ".jpg" or ".jpeg" => "JPEG Image (.jpg)",
            ".gif" => "GIF Animated Image (.gif)",
            ".webp" => "WebP Image (.webp)",
            ".bmp" => "Bitmap Image (.bmp)",
            ".svg" => "SVG Vector Image (.svg)",
            ".mp3" => "MP3 Audio (.mp3)",
            ".wav" => "Waveform Audio (.wav)",
            ".flac" => "Free Lossless Audio (.flac)",
            ".ogg" => "Ogg Vorbis Audio (.ogg)",
            ".m4a" => "MPEG-4 Audio (.m4a)",
            ".mp4" => "MPEG-4 Video (.mp4)",
            ".mkv" => "Matroska Video (.mkv)",
            ".avi" => "Audio Video Interleave (.avi)",
            ".mov" => "QuickTime Movie (.mov)",
            ".webm" => "WebM Video (.webm)",
            ".stl" => "3D STL Model (.stl)",
            _ => string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} File"
        };
    }
}
