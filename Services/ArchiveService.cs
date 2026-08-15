using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace ClankerExplorer.Services;

public class ArchiveService
{
    public static ArchiveService Instance { get; } = new();

    private static readonly string[] ArchiveExtensions = new[]
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2",
        ".xz", ".txz", ".iso", ".cab", ".zst", ".tzst", ".wim", ".vhd"
    };

    private string? _sevenZipGuiExe;
    private string? _sevenZipFmExe;
    private string? _sevenZipCliExe;

    public ArchiveService()
    {
        Locate7Zip();
    }

    private void Locate7Zip()
    {
        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                @"C:\Program Files\7-Zip",
                @"C:\Program Files (x86)\7-Zip",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip")
            };

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

    public bool IsArchive(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || Directory.Exists(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
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
                            var destPath = Path.Combine(destinationDirectory, entry.FullName);

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
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath)) return;

        var dir = Directory.Exists(sourcePath) ? Path.GetDirectoryName(sourcePath.TrimEnd('\\', '/')) ?? "" : Path.GetDirectoryName(sourcePath) ?? "";
        var name = Path.GetFileName(sourcePath.TrimEnd('\\', '/'));
        targetZipPath ??= Path.Combine(dir, $"{name}.zip");

        if (_sevenZipGuiExe != null && File.Exists(_sevenZipGuiExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _sevenZipGuiExe,
                Arguments = $"a -tzip \"{targetZipPath}\" \"{sourcePath}\"",
                UseShellExecute = true
            });
        }
        else if (_sevenZipCliExe != null && File.Exists(_sevenZipCliExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _sevenZipCliExe,
                Arguments = $"a -tzip \"{targetZipPath}\" \"{sourcePath}\"",
                UseShellExecute = true
            });
        }
        else
        {
            // Background fallback using System.IO.Compression
            Task.Run(() =>
            {
                try
                {
                    if (File.Exists(sourcePath))
                    {
                        using var zip = ZipFile.Open(targetZipPath, ZipArchiveMode.Create);
                        zip.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath));
                    }
                    else if (Directory.Exists(sourcePath))
                    {
                        ZipFile.CreateFromDirectory(sourcePath, targetZipPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed ZIP creation: {ex.Message}");
                }
            });
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
