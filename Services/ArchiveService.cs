using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.AppLayer.Operations;

namespace ClankerExplorer.Services;

public class ArchiveService
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2",
        ".xz", ".txz", ".iso", ".cab", ".zst", ".tzst", ".wim", ".vhd"
    };

    private static readonly Lazy<ArchiveService> _instance = new(() => new ArchiveService());
    public static ArchiveService Instance => _instance.Value;

    private string? _sevenZipGuiExe;
    private string? _sevenZipFmExe;
    private string? _sevenZipCliExe;

    public ArchiveService()
    {
        Locate7Zip();
    }

    private void Locate7Zip()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var candidates = new List<string>
                {
                    @"C:\Program Files\7-Zip",
                    @"C:\Program Files (x86)\7-Zip"
                };

                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "7-Zip"));

                var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pfx86)) candidates.Add(Path.Combine(pfx86, "7-Zip"));

                foreach (var dir in candidates.Distinct())
                {
                    if (!Directory.Exists(dir)) continue;

                    var gui = Path.Combine(dir, "7zG.exe");
                    var fm = Path.Combine(dir, "7zFM.exe");
                    var cli = Path.Combine(dir, "7z.exe");

                    if (File.Exists(gui)) _sevenZipGuiExe = gui;
                    if (File.Exists(fm)) _sevenZipFmExe = fm;
                    if (File.Exists(cli)) _sevenZipCliExe = cli;

                    if (_sevenZipGuiExe != null) break;
                }
            }
            else
            {
                // Linux / Unix standard locations
                var linuxCandidates = new[] { "/usr/bin/7z", "/usr/local/bin/7z", "/usr/bin/7za", "/usr/bin/p7zip" };
                foreach (var path in linuxCandidates)
                {
                    if (File.Exists(path))
                    {
                        _sevenZipCliExe = path;
                        break;
                    }
                }
            }
        }
        catch { }
    }

    public bool IsArchive(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || Directory.Exists(filePath)) return false;
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? string.Empty;
        var lower = filePath.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tar.bz2") || lower.EndsWith(".tar.xz")) return true;
        return ArchiveExtensions.Contains(ext);
    }

    public void OpenArchive(string archivePath)
    {
        if (!File.Exists(archivePath)) return;

        if (_sevenZipFmExe != null && File.Exists(_sevenZipFmExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _sevenZipFmExe,
                Arguments = $"\"{archivePath}\"",
                UseShellExecute = true
            });
        }
        else
        {
            // Fallback to default system open
            Process.Start(new ProcessStartInfo(archivePath) { UseShellExecute = true });
        }
    }

    public async Task<(bool success, string message)> ExtractHereAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath)) return (false, "Archive file does not exist.");
        var dir = Path.GetDirectoryName(archivePath) ?? "";
        return await ExtractToAsync(archivePath, dir, cancellationToken: cancellationToken);
    }

    public async Task<(bool success, string message)> ExtractToSubfolderAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath)) return (false, "Archive file does not exist.");
        var dir = Path.GetDirectoryName(archivePath) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(archivePath);
        if (nameWithoutExt.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            nameWithoutExt = Path.GetFileNameWithoutExtension(nameWithoutExt);
        }
        var targetDir = Path.Combine(dir, nameWithoutExt);
        Directory.CreateDirectory(targetDir);
        return await ExtractToAsync(archivePath, targetDir, cancellationToken: cancellationToken);
    }

    public async Task<(bool success, string message)> ExtractToAsync(string archivePath, string destinationDirectory, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath)) return (false, "Archive file does not exist.");

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateZipArchiveEntries(archivePath, destinationDirectory);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, $"ZIP extraction rejected: {ex.Message}");
            }
        }

        Directory.CreateDirectory(destinationDirectory);

        // Contextual sweep: clean abandoned disposable scratch in destination directory
        TransferEngine.DeleteLeftoverTransferScratchFiles(destinationDirectory);

        var exe = _sevenZipCliExe ?? _sevenZipGuiExe;
        if (exe != null && File.Exists(exe))
        {
            return await ExtractWith7ZipAsync(exe, archivePath, destinationDirectory, overwrite, cancellationToken).ConfigureAwait(false);
        }

        return await ExtractManagedZipAsync(archivePath, destinationDirectory, overwrite, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<(bool success, string message)> ExtractWith7ZipAsync(
        string exe,
        string archivePath,
        string destinationDirectory,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var stagingDir = Path.Combine(destinationDirectory, $".clanker-transfer-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDir);
        TransferEngine.RegisterActiveTempFile(stagingDir);

        try
        {
            bool isGui = exe.EndsWith("7zG.exe", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = !isGui
            };
            psi.ArgumentList.Add("x");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add($"-o{stagingDir}");
            if (!isGui)
            {
                psi.ArgumentList.Add(overwrite ? "-aoa" : "-aos");
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return (false, "Failed to start extraction process.");
            }

            try
            {
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch { }
                throw;
            }

            if (proc.ExitCode != 0)
            {
                return (false, $"Extraction exited with code {proc.ExitCode}");
            }

            return PromoteStagingDirectory(stagingDir, destinationDirectory, overwrite);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"7-Zip extraction error: {ex.Message}");
        }
        finally
        {
            TransferEngine.UnregisterActiveTempFile(stagingDir);
            if (Directory.Exists(stagingDir))
            {
                try
                {
                    Directory.Delete(stagingDir, recursive: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to delete staging directory '{stagingDir}': {ex.Message}");
                }
            }
        }
    }

    internal static (bool success, string message) PromoteStagingDirectory(
        string stagingDir,
        string destinationDirectory,
        bool overwrite)
    {
        if (!Directory.Exists(stagingDir))
        {
            return (true, "Extracted successfully.");
        }

        var errors = new List<string>();

        // 1. Create missing destination directories
        try
        {
            foreach (var stagedSubDir in Directory.EnumerateDirectories(stagingDir, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(stagingDir, stagedSubDir);
                var destSubDir = Path.Combine(destinationDirectory, relPath);
                if (!Directory.Exists(destSubDir))
                {
                    Directory.CreateDirectory(destSubDir);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to create destination directories: {ex.Message}");
        }

        // 2. Promote files
        try
        {
            foreach (var stagedFile in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(stagingDir, stagedFile);
                var destFile = Path.Combine(destinationDirectory, relPath);

                var parentDir = Path.GetDirectoryName(destFile);
                if (parentDir != null && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                if (File.Exists(destFile) && !overwrite)
                {
                    continue;
                }

                try
                {
                    File.Move(stagedFile, destFile, overwrite: overwrite);
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to promote '{relPath}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to enumerate staged files: {ex.Message}");
        }

        if (errors.Count > 0)
        {
            return (false, $"Extracted with promotion errors: {string.Join("; ", errors)}");
        }

        return (true, "Extracted successfully.");
    }

    internal async Task<(bool success, string message)> ExtractManagedZipAsync(
        string archivePath,
        string destinationDirectory,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Unsupported archive format without 7-Zip installed.");
        }

        return await Task.Run(() =>
        {
            var errors = new List<string>();
            using var archive = ZipFile.OpenRead(archivePath);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destPath = GetSafeExtractionPath(destinationDirectory, entry.FullName);

                // Directory entry
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                var parent = Path.GetDirectoryName(destPath);
                if (parent != null)
                {
                    Directory.CreateDirectory(parent);
                }

                if (File.Exists(destPath) && !overwrite)
                {
                    continue;
                }

                var siblingDir = parent ?? destinationDirectory;
                var tempPath = Path.Combine(siblingDir, $".clanker-transfer-{Guid.NewGuid():N}.tmp");
                TransferEngine.RegisterActiveTempFile(tempPath);

                try
                {
                    try
                    {
                        using (var entryStream = entry.Open())
                        using (var tempStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            entryStream.CopyTo(tempStream);
                            tempStream.Flush(flushToDisk: true);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to extract '{entry.FullName}': {ex.Message}");
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    if (File.Exists(destPath) && !overwrite)
                    {
                        // Destination appeared between precheck and promotion
                        continue;
                    }

                    File.Move(tempPath, destPath, overwrite: overwrite);
                }
                finally
                {
                    TransferEngine.UnregisterActiveTempFile(tempPath);
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            }

            if (errors.Count > 0)
            {
                return (false, $"Extracted with {errors.Count} error(s): {string.Join("; ", errors)}");
            }

            return (true, "Extracted successfully.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateZipArchiveEntries(string archivePath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            _ = GetSafeExtractionPath(destinationDirectory, entry.FullName);
        }
    }

    private static string GetSafeExtractionPath(string destinationDirectory, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            throw new InvalidDataException("The archive contains an entry with an empty path.");
        }

        var normalizedEntry = entryName.Replace('\\', '/');
        if (normalizedEntry.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(entryName) ||
            (normalizedEntry.Length >= 2 && char.IsLetter(normalizedEntry[0]) && normalizedEntry[1] == ':'))
        {
            throw new InvalidDataException($"The archive entry '{entryName}' uses an absolute path.");
        }

        var root = Path.GetFullPath(destinationDirectory);
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            normalizedEntry.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.Equals(root, comparison) && !candidate.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidDataException($"The archive entry '{entryName}' escapes the extraction directory.");
        }

        return candidate;
    }

    public void OpenExtractDialog(string archivePath)
    {
        if (!File.Exists(archivePath)) return;

        if (_sevenZipGuiExe != null && File.Exists(_sevenZipGuiExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _sevenZipGuiExe,
                Arguments = $"x \"{archivePath}\"",
                UseShellExecute = true
            });
        }
        else
        {
            _ = ExtractHereAsync(archivePath);
        }
    }

    public void CreateZip(string sourcePath, string? targetZipPath = null)
    {
        _ = CreateZipAsync(sourcePath, targetZipPath);
    }

    public async Task<(bool success, string message, string? targetPath)> CreateZipAsync(
        string sourcePath,
        string? targetZipPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return (false, "The source item does not exist.", null);
        }

        var dir = Directory.Exists(sourcePath) ? Path.GetDirectoryName(sourcePath.TrimEnd('\\', '/')) ?? "" : Path.GetDirectoryName(sourcePath) ?? "";
        var name = Path.GetFileName(sourcePath.TrimEnd('\\', '/'));
        targetZipPath ??= Path.Combine(dir, $"{name}.zip");

        if (string.IsNullOrWhiteSpace(targetZipPath))
        {
            return (false, "A destination ZIP path is required.", null);
        }

        targetZipPath = Path.GetFullPath(targetZipPath);
        var targetParent = Path.GetDirectoryName(targetZipPath);
        if (!string.IsNullOrEmpty(targetParent)) Directory.CreateDirectory(targetParent);

        if (_sevenZipCliExe != null && File.Exists(_sevenZipCliExe))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _sevenZipCliExe,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("a");
                psi.ArgumentList.Add("-tzip");
                psi.ArgumentList.Add(targetZipPath);
                psi.ArgumentList.Add(sourcePath);

                using var process = Process.Start(psi);
                if (process == null) return (false, "Failed to start ZIP creation.", targetZipPath);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return process.ExitCode == 0
                    ? (true, "ZIP created successfully.", targetZipPath)
                    : (false, $"ZIP creation exited with code {process.ExitCode}.", targetZipPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, $"ZIP creation error: {ex.Message}", targetZipPath);
            }
        }

        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(sourcePath))
                {
                    using var zip = ZipFile.Open(targetZipPath, ZipArchiveMode.Create);
                    zip.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath));
                }
                else
                {
                    ZipFile.CreateFromDirectory(sourcePath, targetZipPath);
                }
            }, cancellationToken).ConfigureAwait(false);
            return (true, "ZIP created successfully.", targetZipPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed ZIP creation: {ex.Message}");
            return (false, $"ZIP creation error: {ex.Message}", targetZipPath);
        }
    }

    public void OpenAddToArchiveDialog(string sourcePath)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath)) return;

        if (_sevenZipGuiExe != null && File.Exists(_sevenZipGuiExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _sevenZipGuiExe,
                Arguments = $"a \"{sourcePath}\"",
                UseShellExecute = true
            });
        }
        else
        {
            CreateZip(sourcePath);
        }
    }
}
