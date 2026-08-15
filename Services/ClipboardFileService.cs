using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClankerExplorer.Services;

public static class ClipboardFileService
{
    private static readonly List<string> _storedPaths = new();
    private static bool _isCut = false;

    public static event Action? ClipboardChanged;

    public static bool IsCutMode => _isCut;
    public static IReadOnlyList<string> StoredPaths => _storedPaths;

    public static bool IsPathCut(string path)
    {
        if (!_isCut || string.IsNullOrEmpty(path)) return false;
        var normalized = path.TrimEnd('\\', '/');
        return _storedPaths.Any(p => string.Equals(p.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static void Copy(IEnumerable<string> paths)
    {
        _storedPaths.Clear();
        _storedPaths.AddRange(paths);
        _isCut = false;
        ClipboardChanged?.Invoke();
    }

    public static void Cut(IEnumerable<string> paths)
    {
        _storedPaths.Clear();
        _storedPaths.AddRange(paths);
        _isCut = true;
        ClipboardChanged?.Invoke();
    }

    public static bool CanPaste => _storedPaths.Count > 0;

    public static void Paste(string destinationDirectory)
    {
        if (!Directory.Exists(destinationDirectory)) return;

        foreach (var source in _storedPaths.ToList())
        {
            try
            {
                if (File.Exists(source))
                {
                    var dest = Path.Combine(destinationDirectory, Path.GetFileName(source));
                    if (_isCut)
                    {
                        File.Move(source, dest, true);
                    }
                    else
                    {
                        File.Copy(source, dest, true);
                    }
                }
                else if (Directory.Exists(source))
                {
                    var dest = Path.Combine(destinationDirectory, Path.GetFileName(source));
                    if (_isCut)
                    {
                        Directory.Move(source, dest);
                    }
                    else
                    {
                        CopyDirectory(source, dest);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to paste {source}: {ex.Message}");
            }
        }

        if (_isCut)
        {
            _storedPaths.Clear();
            _isCut = false;
        }

        ClipboardChanged?.Invoke();
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}
