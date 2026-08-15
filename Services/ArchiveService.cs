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

    public void ExtractHere(string archivePath)
    {
        if (!File.Exists(archivePath)) return;
        var dir = Path.GetDirectoryName(archivePath) ?? "";
        ExtractTo(archivePath, dir);
    }

    public void ExtractToSubfolder(string archivePath)
    {
        if (!File.Exists(archivePath)) return;
        var dir = Path.GetDirectoryName(archivePath) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(archivePath);
        if (nameWithoutExt.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            nameWithoutExt = Path.GetFileNameWithoutExtension(nameWithoutExt);
        }
        var targetDir = Path.Combine(dir, nameWithoutExt);
        Directory.CreateDirectory(targetDir);
        ExtractTo(archivePath, targetDir);
    }

    public void ExtractTo(string archivePath, string destinationDirectory)
    {
        if (!File.Exists(archivePath)) return;

        var exe = _sevenZipGuiExe ?? _sevenZipCliExe;
        if (exe != null && File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"x \"{archivePath}\" -o\"{destinationDirectory}\" -y",
                UseShellExecute = true
            });
        }
        else
        {
            // Background fallback to System.IO.Compression for zip files
            Task.Run(() =>
            {
                try
                {
                    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        ZipFile.ExtractToDirectory(archivePath, destinationDirectory, true);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed extraction fallback: {ex.Message}");
                }
            });
        }
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
            ExtractHere(archivePath);
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
