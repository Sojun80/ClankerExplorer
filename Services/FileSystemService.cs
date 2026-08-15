using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public class FileSystemService
{
    public static FileSystemService Instance { get; } = new();

    private readonly Dictionary<string, int> _folderVisitCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _folderLastVisited = new(StringComparer.OrdinalIgnoreCase);

    public static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] sizes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public List<DriveModel> GetDrives()
    {
        var list = new List<DriveModel>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                bool isNetwork = d.DriveType == System.IO.DriveType.Network;
                if (!d.IsReady)
                {
                    list.Add(new DriveModel
                    {
                        Letter = d.Name.TrimEnd('\\'),
                        RootPath = d.RootDirectory.FullName,
                        VolumeLabel = isNetwork ? "Network Share" : "Not Ready",
                        DriveType = d.DriveType.ToString(),
                        IsNetworkDrive = isNetwork
                    });
                    continue;
                }

                long total = d.TotalSize;
                long free = d.AvailableFreeSpace;

                list.Add(new DriveModel
                {
                    Letter = d.Name.TrimEnd('\\'),
                    RootPath = d.RootDirectory.FullName,
                    VolumeLabel = d.VolumeLabel,
                    DriveType = d.DriveType.ToString(),
                    IsNetworkDrive = isNetwork,
                    TotalBytes = total,
                    FreeBytes = free,
                    FormattedTotal = FormatBytes(total),
                    FormattedFree = FormatBytes(free),
                    FormattedUsed = FormatBytes(Math.Max(0, total - free))
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting drives: {ex.Message}");
        }
        return list;
    }

    public List<QuickAccessItem> GetQuickAccess()
    {
        var items = new List<QuickAccessItem>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userProfileName = Path.GetFileName(userProfile); // e.g. "5900x"

        var folders = new[]
        {
            (string.IsNullOrEmpty(userProfileName) ? "User Profile" : userProfileName, userProfile, "Home"),
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Desktop"),
            ("Downloads", Path.Combine(userProfile, "Downloads"), "Downloads"),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents")
        };

        foreach (var (name, path, icon) in folders)
        {
            if (Directory.Exists(path))
            {
                items.Add(new QuickAccessItem
                {
                    Name = name,
                    Path = path,
                    IconKind = icon
                });
            }
        }

        return items;
    }

    public List<WslDistroItem> GetWslDistributions()
    {
        var distros = new List<WslDistroItem>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "-l -q",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var name = line.Trim().Replace("\0", "");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        string wslPath = $@"\\wsl$\{name}";
                        distros.Add(new WslDistroItem
                        {
                            Name = $"{name} (Linux)",
                            DistroName = name,
                            RootPath = wslPath,
                            HomePath = Path.Combine(wslPath, "home")
                        });
                    }
                }
            }
        }
        catch
        {
            if (Directory.Exists(@"\\wsl$\Ubuntu"))
            {
                distros.Add(new WslDistroItem
                {
                    Name = "Ubuntu (Linux)",
                    DistroName = "Ubuntu",
                    RootPath = @"\\wsl$\Ubuntu",
                    HomePath = @"\\wsl$\Ubuntu\home"
                });
            }
        }
        return distros;
    }

    public void RecordFolderVisit(string path)
    {
        HistoryService.Instance.RecordFolderVisit(path);
    }

    public List<FrequentFolderItem> GetFrequentFolders(IEnumerable<string>? excludePaths = null, int max = 5)
    {
        return HistoryService.Instance.GetFrequentFolders(excludePaths, max);
    }

    public List<FrequentFolderItem> GetRecentFolders(IEnumerable<string>? excludePaths = null, int max = 5)
    {
        return HistoryService.Instance.GetRecentFolders(excludePaths, max);
    }

    public (List<FileItem> items, string? error) ReadDirectory(string dirPath)
    {
        var items = new List<FileItem>();
        try
        {
            var dirInfo = new DirectoryInfo(dirPath);
            if (!dirInfo.Exists)
            {
                return (items, $"Directory does not exist: {dirPath}");
            }

            RecordFolderVisit(dirPath);

            foreach (var info in dirInfo.EnumerateFileSystemInfos())
            {
                try
                {
                    bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    bool isHidden = (info.Attributes & FileAttributes.Hidden) != 0 || info.Name.StartsWith(".");
                    bool isSystem = (info.Attributes & FileAttributes.System) != 0;
                    bool isReadOnly = (info.Attributes & FileAttributes.ReadOnly) != 0;
                    bool isArchive = (info.Attributes & FileAttributes.Archive) != 0;
                    bool isSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0;

                    string ext = "";
                    long size = 0;

                    if (!isDir && info is FileInfo fileInfo)
                    {
                        size = fileInfo.Length;
                        var lowerName = info.Name.ToLowerInvariant();
                        if (lowerName.EndsWith(".tar.gz")) ext = ".tar.gz";
                        else if (lowerName.EndsWith(".tar.bz2")) ext = ".tar.bz2";
                        else ext = fileInfo.Extension;
                    }

                    var attrChars = new List<string>();
                    if (isDir) attrChars.Add("D");
                    if (isReadOnly) attrChars.Add("R");
                    if (isHidden) attrChars.Add("H");
                    if (isSystem) attrChars.Add("S");
                    if (isArchive) attrChars.Add("A");
                    if (isSymlink) attrChars.Add("L");

                    items.Add(new FileItem
                    {
                        Name = info.Name,
                        Extension = ext,
                        FullPath = info.FullName,
                        ParentPath = dirPath,
                        IsDirectory = isDir,
                        IsSymbolicLink = isSymlink,
                        SizeBytes = size,
                        FormattedSize = isDir ? "<DIR>" : FormatBytes(size),
                        ModifiedTime = info.LastWriteTime,
                        CreatedTime = info.CreationTime,
                        AccessedTime = info.LastAccessTime,
                        IsHidden = isHidden,
                        IsSystem = isSystem,
                        IsReadOnly = isReadOnly,
                        IsArchive = isArchive,
                        AttributesString = string.Join(" ", attrChars),
                        PermissionsString = GetPermissionsDisplay(info, isDir, isReadOnly),
                        OwnerGroupString = isDir ? "root:root" : "user:user"
                    });
                }
                catch
                {
                    items.Add(new FileItem
                    {
                        Name = info.Name,
                        Extension = Path.GetExtension(info.Name),
                        FullPath = info.FullName,
                        ParentPath = dirPath,
                        IsDirectory = (info.Attributes & FileAttributes.Directory) != 0,
                        FormattedSize = "<LOCKED>",
                        ModifiedTime = DateTime.Now,
                        IsHidden = info.Name.StartsWith("."),
                        IsSystem = true,
                        AttributesString = "S LOCKED"
                    });
                }
            }

            return (items, null);
        }
        catch (Exception ex)
        {
            return (items, ex.Message);
        }
    }

    public async Task<FilePreviewData> GetPreviewDataAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(filePath))
                {
                    var di = new DirectoryInfo(filePath);
                    return new FilePreviewData
                    {
                        FilePath = filePath,
                        Name = di.Name,
                        Extension = "",
                        FormattedSize = "<DIR>",
                        ModifiedTime = di.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        PreviewType = "directory"
                    };
                }

                if (!File.Exists(filePath))
                {
                    return new FilePreviewData
                    {
                        FilePath = filePath,
                        Name = Path.GetFileName(filePath),
                        PreviewType = "error",
                        ErrorMessage = "File does not exist"
                    };
                }

                var fi = new FileInfo(filePath);
                var ext = fi.Extension.ToLowerInvariant();

                var imageExts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff" };
                if (imageExts.Contains(ext))
                {
                    return new FilePreviewData
                    {
                        FilePath = filePath,
                        Name = fi.Name,
                        Extension = ext,
                        SizeBytes = fi.Length,
                        FormattedSize = FormatBytes(fi.Length),
                        ModifiedTime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        PreviewType = "image"
                    };
                }

                if (fi.Length <= 1024 * 1024 * 3)
                {
                    byte[] buffer = new byte[Math.Min(fi.Length, 512 * 1024)];
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.Read(buffer, 0, buffer.Length);
                    }

                    bool isBinary = false;
                    for (int i = 0; i < Math.Min(buffer.Length, 1024); i++)
                    {
                        if (buffer[i] == 0) { isBinary = true; break; }
                    }

                    if (!isBinary)
                    {
                        string text = Encoding.UTF8.GetString(buffer);
                        var lines = text.Split('\n');
                        return new FilePreviewData
                        {
                            FilePath = filePath,
                            Name = fi.Name,
                            Extension = ext,
                            SizeBytes = fi.Length,
                            FormattedSize = FormatBytes(fi.Length),
                            ModifiedTime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            PreviewType = "text",
                            TextContent = text,
                            LineCount = lines.Length
                        };
                    }
                }

                int hexBytesToRead = (int)Math.Min(fi.Length, 4096);
                byte[] hexBuffer = new byte[hexBytesToRead];
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Read(hexBuffer, 0, hexBytesToRead);
                }

                var hexRows = new List<HexRow>();
                for (int i = 0; i < hexBytesToRead; i += 16)
                {
                    int count = Math.Min(16, hexBytesToRead - i);
                    string offset = i.ToString("X8");
                    var hexSb = new StringBuilder();
                    var asciiSb = new StringBuilder();

                    for (int j = 0; j < 16; j++)
                    {
                        if (j < count)
                        {
                            byte b = hexBuffer[i + j];
                            hexSb.Append(b.ToString("X2")).Append(' ');
                            asciiSb.Append(b >= 32 && b <= 126 ? (char)b : '.');
                        }
                        else
                        {
                            hexSb.Append("   ");
                        }
                        if (j == 7) hexSb.Append(" ");
                    }

                    hexRows.Add(new HexRow
                    {
                        Offset = offset,
                        HexBytes = hexSb.ToString().TrimEnd(),
                        AsciiText = asciiSb.ToString()
                    });
                }

                return new FilePreviewData
                {
                    FilePath = filePath,
                    Name = fi.Name,
                    Extension = ext,
                    SizeBytes = fi.Length,
                    FormattedSize = FormatBytes(fi.Length),
                    ModifiedTime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    PreviewType = "binary",
                    HexRows = hexRows
                };
            }
            catch (Exception ex)
            {
                return new FilePreviewData
                {
                    FilePath = filePath,
                    Name = Path.GetFileName(filePath),
                    PreviewType = "error",
                    ErrorMessage = ex.Message
                };
            }
        });
    }

    public async Task<HashResult> CalculateHashesAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            using var sha256 = SHA256.Create();
            using var md5 = MD5.Create();

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buffer = new byte[81920];
            int read;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha256.TransformBlock(buffer, 0, read, null, 0);
                md5.TransformBlock(buffer, 0, read, null, 0);
            }

            sha256.TransformFinalBlock(buffer, 0, 0);
            md5.TransformFinalBlock(buffer, 0, 0);

            string sha256Hex = BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant();
            string md5Hex = BitConverter.ToString(md5.Hash!).Replace("-", "").ToLowerInvariant();

            return new HashResult
            {
                Sha256 = sha256Hex,
                Md5 = md5Hex
            };
        });
    }

    public List<BatchRenameItem> PreviewBatchRename(IEnumerable<string> paths, BatchRenameRule rule)
    {
        var results = new List<BatchRenameItem>();
        int counter = rule.StartNumber;

        foreach (var path in paths)
        {
            var originalName = Path.GetFileName(path);
            var ext = Path.GetExtension(originalName);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(originalName);
            var dir = Path.GetDirectoryName(path) ?? "";

            string newBaseName = nameWithoutExt;

            if (rule.Mode == "replace" && !string.IsNullOrEmpty(rule.FindText))
            {
                if (rule.IsRegex)
                {
                    try
                    {
                        var opt = rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                        newBaseName = Regex.Replace(nameWithoutExt, rule.FindText, rule.ReplaceText ?? "", opt);
                    }
                    catch { }
                }
                else
                {
                    var comp = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    newBaseName = nameWithoutExt.Replace(rule.FindText, rule.ReplaceText ?? "", comp);
                }
            }
            else if (rule.Mode == "prefix_suffix")
            {
                newBaseName = $"{rule.Prefix}{nameWithoutExt}{rule.Suffix}";
            }
            else if (rule.Mode == "numbering")
            {
                string numStr = counter.ToString().PadLeft(rule.Padding, '0');
                newBaseName = $"{rule.Prefix}{numStr}{rule.Suffix}";
                counter++;
            }
            else if (rule.Mode == "change_case")
            {
                if (rule.CaseMode == "lower") newBaseName = nameWithoutExt.ToLowerInvariant();
                else if (rule.CaseMode == "upper") newBaseName = nameWithoutExt.ToUpperInvariant();
                else if (rule.CaseMode == "title")
                {
                    newBaseName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(nameWithoutExt.ToLowerInvariant());
                }
            }

            string newFullName = $"{newBaseName}{ext}";
            string newPath = Path.Combine(dir, newFullName);
            bool willChange = !string.Equals(originalName, newFullName, StringComparison.Ordinal);
            bool hasConflict = willChange && (File.Exists(newPath) || Directory.Exists(newPath));

            results.Add(new BatchRenameItem
            {
                OriginalPath = path,
                OriginalName = originalName,
                NewName = newFullName,
                NewPath = newPath,
                HasConflict = hasConflict
            });
        }

        return results;
    }

    public void ExecuteBatchRename(IEnumerable<BatchRenameItem> items)
    {
        foreach (var item in items)
        {
            if (!item.WillChange || item.HasConflict) continue;
            if (File.Exists(item.OriginalPath))
            {
                File.Move(item.OriginalPath, item.NewPath);
            }
            else if (Directory.Exists(item.OriginalPath))
            {
                Directory.Move(item.OriginalPath, item.NewPath);
            }
        }
    }

    public void CreateFolder(string parentDir, string name)
    {
        var target = Path.Combine(parentDir, name);
        Directory.CreateDirectory(target);
    }

    public void CreateFile(string parentDir, string name)
    {
        var target = Path.Combine(parentDir, name);
        using (File.Create(target)) { }
    }

    public void Rename(string oldPath, string newName)
    {
        var dir = Path.GetDirectoryName(oldPath) ?? "";
        var newPath = Path.Combine(dir, newName);
        if (File.Exists(oldPath))
        {
            File.Move(oldPath, newPath);
        }
        else if (Directory.Exists(oldPath))
        {
            Directory.Move(oldPath, newPath);
        }
    }

    public void Delete(IEnumerable<string> paths, bool permanent)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    public void OpenItem(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open {path}: {ex.Message}");
        }
    }

    public void OpenTerminal(string path, bool asAdmin = false)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -Command \"Set-Location -LiteralPath '{path}'\"",
                    UseShellExecute = true
                };
                if (asAdmin)
                {
                    psi.Verb = "runas";
                }
                Process.Start(psi);
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "x-terminal-emulator",
                    WorkingDirectory = path,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open PowerShell: {ex.Message}");
        }
    }

    public void OpenCmd(string path, bool asAdmin = false)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"cd /d \"\"{path}\"\"\"",
                    WorkingDirectory = path,
                    UseShellExecute = true
                };
                if (asAdmin)
                {
                    psi.Verb = "runas";
                }
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open CMD: {ex.Message}");
        }
    }

    public void OpenEditor(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c code \"{path}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "code",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open editor: {ex.Message}");
        }
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".md",
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".html", ".css", ".scss", ".less", ".py",
        ".c", ".cpp", ".cc", ".h", ".hpp", ".rs", ".go", ".java", ".kt", ".sh", ".bash",
        ".bat", ".ps1", ".cmd", ".sql", ".axaml", ".xaml", ".svg", ".toml", ".env",
        ".csv", ".tsv", ".properties", ".config", ".editorconfig", ".gitignore", ".gitattributes"
    };

    private string? _detectedEditorPath;
    private string? _detectedEditorName;

    public string GetEditorName()
    {
        if (_detectedEditorName != null) return _detectedEditorName;
        DetectEditor();
        return _detectedEditorName ?? "Editor";
    }

    public string GetEditMenuLabel()
    {
        var name = GetEditorName();
        return name == "Editor" ? "Edit" : $"Edit with {name}";
    }

    private void DetectEditor()
    {
        var npp = @"C:\Program Files\Notepad++\notepad++.exe";
        if (File.Exists(npp))
        {
            _detectedEditorPath = npp;
            _detectedEditorName = "Notepad++";
            return;
        }

        var npp86 = @"C:\Program Files (x86)\Notepad++\notepad++.exe";
        if (File.Exists(npp86))
        {
            _detectedEditorPath = npp86;
            _detectedEditorName = "Notepad++";
            return;
        }

        _detectedEditorPath = "notepad.exe";
        _detectedEditorName = "Editor";
    }

    public bool IsTextLikeFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || Directory.Exists(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
        {
            var name = Path.GetFileName(filePath);
            return name.StartsWith(".") || name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.Equals("Makefile", StringComparison.OrdinalIgnoreCase);
        }
        return TextExtensions.Contains(ext);
    }

    public void EditFile(string filePath)
    {
        if (!File.Exists(filePath)) return;
        DetectEditor();
        try
        {
            if (!string.IsNullOrEmpty(_detectedEditorPath) && File.Exists(_detectedEditorPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _detectedEditorPath,
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{filePath}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to edit file: {ex.Message}");
        }
    }

    public void OpenWith(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = $"shell32.dll,OpenAs_RunDLL \"{filePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                OpenItem(filePath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch Open With: {ex.Message}");
        }
    }

    private static string GetPermissionsDisplay(FileSystemInfo info, bool isDir, bool isReadOnly)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(info.FullName);
                return $"{(isDir ? "d" : "-")}{((mode & UnixFileMode.UserRead) != 0 ? "r" : "-")}{((mode & UnixFileMode.UserWrite) != 0 ? "w" : "-")}{((mode & UnixFileMode.UserExecute) != 0 ? "x" : "-")}{((mode & UnixFileMode.GroupRead) != 0 ? "r" : "-")}{((mode & UnixFileMode.GroupWrite) != 0 ? "w" : "-")}{((mode & UnixFileMode.GroupExecute) != 0 ? "x" : "-")}{((mode & UnixFileMode.OtherRead) != 0 ? "r" : "-")}{((mode & UnixFileMode.OtherWrite) != 0 ? "w" : "-")}{((mode & UnixFileMode.OtherExecute) != 0 ? "x" : "-")}";
            }
        }
        catch { }

        return isDir ? "drwxr-xr-x" : (isReadOnly ? "-r--r--r--" : "-rw-rw-rw-");
    }
}
