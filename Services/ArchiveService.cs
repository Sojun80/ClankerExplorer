using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

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

        return await Task.Run(() =>
        {
            var exe = _sevenZipGuiExe ?? _sevenZipCliExe;
            if (exe != null && File.Exists(exe))
            {
                try
                {
                    // If GUI extractor (7zG.exe), run with interactive prompt on conflict
                    bool isGui = exe.EndsWith("7zG.exe", StringComparison.OrdinalIgnoreCase);
                    string args = isGui
                        ? $"x \"{archivePath}\" -o\"{destinationDirectory}\""
                        : $"x \"{archivePath}\" -o\"{destinationDirectory}\" {(overwrite ? "-aoa" : "-aos")}";

                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = !isGui
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit();
                        return (proc.ExitCode == 0, proc.ExitCode == 0 ? "Extracted successfully." : $"Extraction exited with code {proc.ExitCode}");
                    }
                    return (false, "Failed to start extraction process.");
                }
                catch (Exception ex)
                {
                    return (false, $"7-Zip extraction error: {ex.Message}");
                }
            }
            else
            {
                // Fallback to System.IO.Compression for zip files
                try
                {
                    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var archive = ZipFile.OpenRead(archivePath);
                        foreach (var entry in archive.Entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var destPath = GetSafeExtractionPath(destinationDirectory, entry.FullName);

                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                Directory.CreateDirectory(destPath);
                                continue;
                            }

                            var parent = Path.GetDirectoryName(destPath);
                            if (parent != null) Directory.CreateDirectory(parent);

                            if (File.Exists(destPath) && !overwrite)
                            {
                                // Safe non-overwriting extraction
                                continue;
                            }

                            entry.ExtractToFile(destPath, overwrite);
                        }
                        return (true, "Extracted successfully.");
                    }
                    return (false, "Unsupported archive format without 7-Zip installed.");
                }
                catch (Exception ex)
                {
                    return (false, $"ZIP extraction error: {ex.Message}");
                }
            }
        }, cancellationToken);
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
