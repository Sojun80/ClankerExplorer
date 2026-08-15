using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public class FileSystemService
{
    public static FileSystemService Instance { get; } = new();

    public static string DefaultRootPath => OperatingSystem.IsWindows() ? @"C:\" : (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "/");

    private string? _notepadPlusPlusPath;

    public FileSystemService()
    {
        LocateNotepadPlusPlus();
    }

    private void LocateNotepadPlusPlus()
    {
        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                @"C:\Program Files\Notepad++\notepad++.exe",
                @"C:\Program Files (x86)\Notepad++\notepad++.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Notepad++", "notepad++.exe")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    _notepadPlusPlusPath = path;
                    break;
                }
            }
        }
    }

    public string GetEditMenuLabel()
    {
        if (OperatingSystem.IsWindows() && _notepadPlusPlusPath != null && File.Exists(_notepadPlusPlusPath))
        {
            return "Edit with Notepad++";
        }
        return "Edit";
    }

    public async Task<(string stdout, string stderr, int exitCode)> RunProcessWithTimeoutAsync(
        string fileName,
        string arguments,
        int timeoutMs = 3000,
        Encoding? encoding = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

            if (!process.Start())
            {
                return (string.Empty, "Failed to start process", -1);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return (stdoutBuilder.ToString(), stderrBuilder.ToString(), process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return (stdoutBuilder.ToString(), "Process execution timed out", -2);
            }
        }
        catch (Exception ex)
        {
            return (string.Empty, ex.Message, -1);
        }
    }

    public List<DriveModel> GetDrives()
    {
        var drives = new List<DriveModel>();

        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    var isNet = d.DriveType == System.IO.DriveType.Network;
                    if (d.IsReady)
                    {
                        var total = d.TotalSize;
                        var free = d.TotalFreeSpace;
                        var used = total - free;

                        drives.Add(new DriveModel
                        {
                            Letter = d.Name.TrimEnd('\\'),
                            VolumeLabel = d.VolumeLabel,
                            RootPath = d.RootDirectory.FullName,
                            DriveType = d.DriveType.ToString(),
                            IsNetworkDrive = isNet,
                            TotalBytes = total,
                            FreeBytes = free,
                            FormattedTotal = FormatBytes(total),
                            FormattedFree = FormatBytes(free),
                            FormattedUsed = FormatBytes(used)
                        });
                    }
                    else
                    {
                        drives.Add(new DriveModel
                        {
                            Letter = d.Name.TrimEnd('\\'),
                            RootPath = d.Name,
                            DriveType = d.DriveType.ToString(),
                            IsNetworkDrive = isNet
                        });
                    }
                }
                catch
                {
                    // Unready or network drives that throw
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to enumerate drives: {ex.Message}");
        }

        return drives;
    }

    public List<QuickAccessItem> GetStandardQuickAccess()
    {
        var items = new List<QuickAccessItem>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var list = new (string name, string path, string icon)[]
        {
            ("Home", userProfile, "🏠"),
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "🖥️"),
            ("Downloads", Path.Combine(userProfile, "Downloads"), "📥"),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "📄"),
            ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "🖼️"),
            ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "🎵"),
            ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "🎬")
        };

        foreach (var (name, path, icon) in list)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                items.Add(new QuickAccessItem(path, name, icon));
            }
        }

        return items;
    }

    public async Task<List<WslDistroItem>> GetWslDistributionsAsync(CancellationToken cancellationToken = default)
    {
        var distros = new List<WslDistroItem>();
        if (!OperatingSystem.IsWindows()) return distros;

        try
        {
            var (output, _, exitCode) = await RunProcessWithTimeoutAsync("wsl.exe", "-l -q", 2000, Encoding.Unicode, cancellationToken);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
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
        catch { }

        if (distros.Count == 0 && Directory.Exists(@"\\wsl$\Ubuntu"))
        {
            distros.Add(new WslDistroItem
            {
                Name = "Ubuntu (Linux)",
                DistroName = "Ubuntu",
                RootPath = @"\\wsl$\Ubuntu",
                HomePath = @"\\wsl$\Ubuntu\home"
            });
        }

        return distros;
    }

    public (List<FileItem> items, string? error) ReadDirectory(string dirPath)
    {
        return ReadDirectoryAsync(dirPath).GetAwaiter().GetResult();
    }

    public async Task<(List<FileItem> items, string? error)> ReadDirectoryAsync(string dirPath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var items = new List<FileItem>();
            if (string.IsNullOrWhiteSpace(dirPath)) return (items, "Invalid directory path.");

            try
            {
                if (!Directory.Exists(dirPath))
                {
                    return (items, $"Directory not found: {dirPath}");
                }

                var di = new DirectoryInfo(dirPath);
                var entries = di.EnumerateFileSystemInfos();

                foreach (var info in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                        bool isSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget != null;
                        bool isHidden = (info.Attributes & FileAttributes.Hidden) != 0 || info.Name.StartsWith(".");
                        bool isSystem = (info.Attributes & FileAttributes.System) != 0;
                        bool isReadOnly = (info.Attributes & FileAttributes.ReadOnly) != 0;
                        bool isArchive = (info.Attributes & FileAttributes.Archive) != 0;

                        long size = 0;
                        if (!isDir && info is FileInfo fi)
                        {
                            try { size = fi.Length; } catch { }
                        }

                        var attrChars = new List<string>();
                        if (isReadOnly) attrChars.Add("R");
                        if (isHidden) attrChars.Add("H");
                        if (isSystem) attrChars.Add("S");
                        if (isArchive) attrChars.Add("A");

                        items.Add(new FileItem
                        {
                            Name = info.Name,
                            Extension = isDir ? "" : Path.GetExtension(info.Name),
                            FullPath = info.FullName,
                            ParentPath = dirPath,
                            IsDirectory = isDir,
                            IsSymbolicLink = isSymlink,
                            SizeBytes = size,
                            FormattedSize = isDir ? "<DIR>" : FormatBytes(size),
                            ModifiedTime = SafeGetTime(() => info.LastWriteTime),
                            CreatedTime = SafeGetTime(() => info.CreationTime),
                            AccessedTime = SafeGetTime(() => info.LastAccessTime),
                            IsHidden = isHidden,
                            IsSystem = isSystem,
                            IsReadOnly = isReadOnly,
                            IsArchive = isArchive,
                            AttributesString = string.Join(" ", attrChars),
                            PermissionsString = GetPermissionsDisplay(info, isDir, isReadOnly),
                            OwnerGroupString = GetLinuxOwnerGroup(info)
                        });
                    }
                    catch (OperationCanceledException) { throw; }
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
            catch (OperationCanceledException)
            {
                return (items, "Operation canceled");
            }
            catch (Exception ex)
            {
                return (items, ex.Message);
            }
        }, cancellationToken);
    }

    private static DateTime SafeGetTime(Func<DateTime> getter)
    {
        try { return getter(); } catch { return DateTime.MinValue; }
    }

    public async Task<FilePreviewData> GetPreviewDataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

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
                        CreatedTime = di.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        AccessedTime = di.LastAccessTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        PreviewType = "directory"
                    };
                }

                if (!File.Exists(filePath))
                {
                    return new FilePreviewData { FilePath = filePath, PreviewType = "none" };
                }

                var fi = new FileInfo(filePath);
                var ext = fi.Extension.ToLowerInvariant();
                var size = fi.Length;

                var data = new FilePreviewData
                {
                    FilePath = filePath,
                    Name = fi.Name,
                    Extension = ext,
                    SizeBytes = size,
                    FormattedSize = FormatBytes(size),
                    ModifiedTime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    CreatedTime = fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    AccessedTime = fi.LastAccessTime.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // Determine type
                if (IsImageExtension(ext))
                {
                    data.PreviewType = "image";
                }
                else if (IsMediaExtension(ext))
                {
                    data.PreviewType = "media";
                }
                else if (IsTextExtension(ext))
                {
                    data.PreviewType = "text";
                    if (size < 1_000_000)
                    {
                        data.TextContent = File.ReadAllText(filePath);
                    }
                    else
                    {
                        using var reader = new StreamReader(filePath);
                        var buffer = new char[8192];
                        int read = reader.Read(buffer, 0, buffer.Length);
                        data.TextContent = new string(buffer, 0, read) + "\n\n... [Truncated due to large file size] ...";
                    }
                }
                else if (IsHexApplicable(ext) || size > 0)
                {
                    data.PreviewType = "hex";
                    data.HexRows = GenerateHexDump(filePath, 256);
                }
                else
                {
                    data.PreviewType = "binary";
                }

                return data;
            }
            catch (Exception ex)
            {
                return new FilePreviewData
                {
                    FilePath = filePath,
                    PreviewType = "error",
                    TextContent = $"Preview error: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    public async Task<HashResult> ComputeHashesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (!File.Exists(filePath)) return new HashResult();

            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: false);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

            var buffer = new byte[65536];
            int bytesRead;

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha256.AppendData(buffer, 0, bytesRead);
                md5.AppendData(buffer, 0, bytesRead);
            }

            var sha256Bytes = sha256.GetHashAndReset();
            var md5Bytes = md5.GetHashAndReset();

            return new HashResult
            {
                Sha256 = Convert.ToHexString(sha256Bytes).ToLowerInvariant(),
                Md5 = Convert.ToHexString(md5Bytes).ToLowerInvariant()
            };
        }, cancellationToken);
    }

    public List<BatchRenameItem> PreviewBatchRename(IEnumerable<string> paths, BatchRenameRule rule)
    {
        var results = new List<BatchRenameItem>();
        int counter = rule.StartNumber;
        var invalidChars = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).Distinct().ToArray();
        var seenTargetPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
                        newBaseName = Regex.Replace(nameWithoutExt, rule.FindText, rule.ReplaceText ?? "", opt, TimeSpan.FromMilliseconds(250));
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

            bool isInvalidName = string.IsNullOrWhiteSpace(newFullName) || newFullName.IndexOfAny(invalidChars) >= 0;
            bool isDuplicateTarget = !seenTargetPaths.Add(newPath);
            bool isExistingFileCollision = willChange && !paths.Contains(newPath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) && (File.Exists(newPath) || Directory.Exists(newPath));

            bool hasConflict = isInvalidName || isDuplicateTarget || isExistingFileCollision;

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

    public (bool success, string message, int renamedCount) ExecuteBatchRenameSafe(IEnumerable<BatchRenameItem> items)
    {
        var itemList = items.Where(i => i.WillChange && !i.HasConflict).ToList();
        if (itemList.Count == 0) return (true, "No items to rename.", 0);

        var invalidChars = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).Distinct().ToArray();
        var seenTargetPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var item in itemList)
        {
            if (string.IsNullOrWhiteSpace(item.NewName) || item.NewName.IndexOfAny(invalidChars) >= 0)
            {
                return (false, $"Invalid file name: '{item.NewName}' contains illegal characters or path separators.", 0);
            }

            if (!seenTargetPaths.Add(item.NewPath))
            {
                return (false, $"Target conflict: multiple items in the batch are named '{item.NewName}'.", 0);
            }
        }

        // Two-Phase Rename Execution with Rollback
        var tempRenames = new List<(string originalPath, string tempPath, string finalPath)>();
        var completedOriginals = new List<(string currentPath, string originalPath)>();

        try
        {
            // Phase 1: Move to unique temporary paths
            foreach (var item in itemList)
            {
                var dir = Path.GetDirectoryName(item.OriginalPath) ?? "";
                var tempPath = Path.Combine(dir, $".c_tmp_{Guid.NewGuid():N}_{item.OriginalName}");

                if (File.Exists(item.OriginalPath))
                {
                    File.Move(item.OriginalPath, tempPath);
                }
                else if (Directory.Exists(item.OriginalPath))
                {
                    Directory.Move(item.OriginalPath, tempPath);
                }
                else
                {
                    throw new FileNotFoundException($"Source file not found: {item.OriginalPath}");
                }

                tempRenames.Add((item.OriginalPath, tempPath, item.NewPath));
                completedOriginals.Add((tempPath, item.OriginalPath));
            }

            // Phase 2: Move from temporary paths to final target paths
            for (int i = 0; i < tempRenames.Count; i++)
            {
                var (orig, temp, final) = tempRenames[i];
                if (File.Exists(temp))
                {
                    File.Move(temp, final);
                }
                else if (Directory.Exists(temp))
                {
                    Directory.Move(temp, final);
                }
                completedOriginals[i] = (final, orig);
            }

            return (true, $"Successfully renamed {itemList.Count} items.", itemList.Count);
        }
        catch (Exception ex)
        {
            // Rollback on any failure
            Debug.WriteLine($"Batch rename failed, rolling back: {ex.Message}");
            int rollbackFailures = 0;
            foreach (var (current, original) in completedOriginals)
            {
                try
                {
                    if (File.Exists(current)) File.Move(current, original);
                    else if (Directory.Exists(current)) Directory.Move(current, original);
                }
                catch (Exception rollEx)
                {
                    rollbackFailures++;
                    Debug.WriteLine($"Rollback error for {current}: {rollEx.Message}");
                }
            }

            string rollbackMsg = rollbackFailures == 0 ? "All changes were safely rolled back." : $"{rollbackFailures} items could not be rolled back.";
            return (false, $"Batch rename error: {ex.Message}. {rollbackMsg}", 0);
        }
    }

    public void ExecuteBatchRename(IEnumerable<BatchRenameItem> items)
    {
        var result = ExecuteBatchRenameSafe(items);
        if (!result.success)
        {
            throw new InvalidOperationException(result.message);
        }
    }

    public void CreateFolder(string parentPath, string name)
    {
        var target = Path.Combine(parentPath, name);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"An item named '{name}' already exists.");
        }
        Directory.CreateDirectory(target);
    }

    public void CreateFile(string parentPath, string name)
    {
        var target = Path.Combine(parentPath, name);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"An item named '{name}' already exists.");
        }
        using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
    }

    public void Rename(string oldPath, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newName)) return;

        var dir = Path.GetDirectoryName(oldPath) ?? "";
        var oldName = Path.GetFileName(oldPath);
        var newPath = Path.Combine(dir, newName);

        if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return;

        bool isCaseOnlyWindowsRename = OperatingSystem.IsWindows() &&
            string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);

        if (!isCaseOnlyWindowsRename)
        {
            if (File.Exists(newPath) || Directory.Exists(newPath))
            {
                throw new IOException($"An item named '{newName}' already exists in this folder.");
            }

            if (File.Exists(oldPath))
            {
                File.Move(oldPath, newPath);
            }
            else if (Directory.Exists(oldPath))
            {
                Directory.Move(oldPath, newPath);
            }
        }
        else
        {
            // Direct two-step rename for Windows case-only change with rollback
            string tempPath = Path.Combine(dir, $".c_tmp_{Guid.NewGuid():N}_{oldName}");
            bool isDir = Directory.Exists(oldPath);

            try
            {
                if (isDir) Directory.Move(oldPath, tempPath);
                else File.Move(oldPath, tempPath);

                try
                {
                    if (isDir) Directory.Move(tempPath, newPath);
                    else File.Move(tempPath, newPath);
                }
                catch
                {
                    try
                    {
                        if (isDir) Directory.Move(tempPath, oldPath);
                        else File.Move(tempPath, oldPath);
                    }
                    catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Case-only rename failed: {ex.Message}");
                throw;
            }
        }
    }

    public async Task DeleteAsync(IEnumerable<string> paths, bool permanent = false, CancellationToken cancellationToken = default)
    {
        await Task.Run(async () =>
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(path)) continue;

                if (permanent)
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
                else
                {
                    // Move to Recycle Bin / Trash
                    if (OperatingSystem.IsWindows())
                    {
                        if (File.Exists(path))
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                path,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin
                            );
                        }
                        else if (Directory.Exists(path))
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                                path,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin
                            );
                        }
                    }
                    else
                    {
                        // Linux / POSIX Trash via gio
                        var (output, error, exitCode) = await RunProcessWithTimeoutAsync("gio", $"trash \"{path}\"", 3000, cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (exitCode != 0)
                        {
                            // Never fall back to permanent deletion if trash fails!
                            throw new IOException($"Failed to move '{path}' to Trash: {error}. Permanent deletion was prevented.");
                        }
                    }
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Delete(IEnumerable<string> paths, bool permanent = false)
    {
        DeleteAsync(paths, permanent).GetAwaiter().GetResult();
    }

    private static string GetLinuxOwnerGroup(FileSystemInfo info)
    {
        if (OperatingSystem.IsWindows()) return string.Empty;
        try
        {
            string user = Environment.UserName;
            return $"{user}:{user}";
        }
        catch
        {
            return "user:user";
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
            var workingDir = Directory.Exists(path) ? path : (File.Exists(path) ? Path.GetDirectoryName(path) : path) ?? DefaultRootPath;
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoExit",
                    WorkingDirectory = workingDir,
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
                string term = Environment.GetEnvironmentVariable("TERMINAL") ?? "";
                var candidates = new[] { term, "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "alacritty", "kitty", "xterm" };
                foreach (var candidate in candidates.Where(c => !string.IsNullOrEmpty(c)))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = candidate,
                            WorkingDirectory = workingDir,
                            UseShellExecute = true
                        });
                        return;
                    }
                    catch { }
                }
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
            var workingDir = Directory.Exists(path) ? path : (File.Exists(path) ? Path.GetDirectoryName(path) : path) ?? DefaultRootPath;
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k",
                    WorkingDirectory = workingDir,
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

    public Task<HashResult> CalculateHashesAsync(string filePath, CancellationToken cancellationToken = default) => ComputeHashesAsync(filePath, cancellationToken);

    public void EditFile(string filePath) => OpenEditor(filePath);

    public void OpenEditor(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (_notepadPlusPlusPath != null && File.Exists(_notepadPlusPlusPath))
                {
                    Process.Start(new ProcessStartInfo(_notepadPlusPlusPath, $"\"{path}\"") { UseShellExecute = true });
                    return;
                }

                // Try Notepad
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                // Linux editors: check $VISUAL, $EDITOR, gedit, kate, nano
                string editor = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR") ?? "";
                var candidates = new[] { editor, "gedit", "kate", "code", "nano" };
                foreach (var candidate in candidates.Where(c => !string.IsNullOrEmpty(c)))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(candidate, $"\"{path}\"") { UseShellExecute = true });
                        return;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open editor: {ex.Message}");
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

    public bool IsTextLikeFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || Directory.Exists(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return IsTextExtension(ext);
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

    private static List<HexRow> GenerateHexDump(string filePath, int maxBytes = 256)
    {
        var rows = new List<HexRow>();
        try
        {
            using var fs = File.OpenRead(filePath);
            var buffer = new byte[16];
            int offset = 0;
            int totalRead = 0;

            while (totalRead < maxBytes)
            {
                int bytesToRead = Math.Min(16, maxBytes - totalRead);
                int bytesRead = fs.Read(buffer, 0, bytesToRead);
                if (bytesRead == 0) break;

                var hexParts = new StringBuilder();
                var asciiParts = new StringBuilder();

                for (int i = 0; i < bytesRead; i++)
                {
                    hexParts.Append($"{buffer[i]:X2} ");
                    char c = (buffer[i] >= 32 && buffer[i] <= 126) ? (char)buffer[i] : '.';
                    asciiParts.Append(c);
                }

                rows.Add(new HexRow
                {
                    Offset = $"{offset:X8}",
                    HexBytes = hexParts.ToString().TrimEnd(),
                    Ascii = asciiParts.ToString()
                });

                offset += bytesRead;
                totalRead += bytesRead;
            }
        }
        catch { }

        return rows;
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    private static bool IsImageExtension(string ext) =>
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".svg" }.Contains(ext);

    private static bool IsMediaExtension(string ext) =>
        new[] { ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".mp4", ".mkv", ".avi", ".webm", ".mov" }.Contains(ext);

    private static bool IsTextExtension(string ext) =>
        new[]
        {
            ".txt", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf",
            ".md", ".markdown", ".cs", ".ts", ".js", ".jsx", ".tsx", ".py", ".rs", ".go",
            ".cpp", ".c", ".h", ".hpp", ".html", ".css", ".scss", ".axaml", ".xaml",
            ".sql", ".sh", ".bat", ".cmd", ".ps1", ".toml", ".env", ".gitignore"
        }.Contains(ext);

    private static bool IsHexApplicable(string ext) =>
        new[] { ".exe", ".dll", ".so", ".bin", ".dat", ".iso", ".sys", ".db", ".sqlite", ".obj", ".class" }.Contains(ext);
}
