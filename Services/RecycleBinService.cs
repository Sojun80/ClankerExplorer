using System.Globalization;
using System.Text.Json;

namespace ClankerExplorer.Services;

public sealed record RecycleEntry(string OriginalPath, string StoredPath, DateTimeOffset RecycledAt, bool IsDirectory, string Backend = "Managed", string? NativeItem = null);

/// <summary>Recoverable replacements on the destination filesystem, including network shares.</summary>
public sealed class RecycleBinService
{
    public const string DirectoryName = ".clanker-recycle";
    public static RecycleBinService Instance { get; internal set; } = new();
    private static readonly object Gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string, bool> _isNetworkPath;
    private readonly IWindowsRecycleBin _windows;
    public string HistoryPath => Path.Combine(AppStoragePaths.GetDataDirectory(), "recycle-history.jsonl");

    public RecycleBinService(Func<DateTimeOffset>? clock = null, Func<string, bool>? isNetworkPath = null,
        IWindowsRecycleBin? windows = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        _isNetworkPath = isNetworkPath ?? IsNetworkPath;
        _windows = windows ?? new WindowsRecycleBin();
    }

    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(@"\\") && !path.StartsWith(@"\\?\")) return true;
        string fullPath = Path.GetFullPath(path);
        return new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType == DriveType.Network;
    }

    public void Record(string action, string path, string? source = null, RecycleEntry? entry = null, string? error = null)
    {
        lock (Gate)
        {
            using var stream = new FileStream(HistoryPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] line = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                Action = action, Timestamp = _clock(), OriginalPath = Path.GetFullPath(path),
                IncomingSource = source == null ? null : Path.GetFullPath(source), Entry = entry, Error = error
            }) + "\n");
            stream.Write(line);
            stream.Flush(flushToDisk: true);
        }
    }

    public static string GetBinDirectory(string directory) => Path.Combine(directory, DirectoryName);

    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void EnsureStorageDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The recycle folder cannot be a symbolic link or junction.");
    }

    private static void Move(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.Directory) != 0)
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
    }

    private static string MetadataPath(string storedPath) =>
        Path.Combine(Path.GetDirectoryName(storedPath)!, ".metadata", Path.GetFileName(storedPath) + ".json");

    public RecycleEntry Recycle(string path, string? incomingSource = null)
    {
        Record("RecycleRequested", path, incomingSource);
        try { return RecycleCore(path, incomingSource); }
        catch (Exception ex)
        {
            try { Record("RecycleFailed", path, incomingSource, error: ex.Message); } catch { }
            throw;
        }
    }

    private RecycleEntry RecycleCore(string path, string? incomingSource)
    {
        lock (Gate)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(path) ?? throw new IOException("A drive root cannot be recycled.");
            bool isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            if (OperatingSystem.IsWindows() && !_isNetworkPath(path))
                return RecycleLocal(path, isDirectory, incomingSource);
            string bin = GetBinDirectory(parent);
            EnsureStorageDirectory(bin);
            EnsureStorageDirectory(Path.Combine(bin, ".metadata"));
            DateTimeOffset time = _clock();
            string name = Path.GetFileName(path);
            string extension = isDirectory ? "" : Path.GetExtension(name);
            string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
            string timestamp = time.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

            for (int suffix = 0; ; suffix++)
            {
                string collision = suffix == 0 ? "" : "-" + suffix.ToString(CultureInfo.InvariantCulture);
                string stored = Path.Combine(bin, $"{stem}_{timestamp}{collision}{extension}");
                string metadata = MetadataPath(stored);
                if (Exists(stored) || File.Exists(metadata)) continue;
                FileStream reservation;
                try { reservation = new FileStream(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException) when (File.Exists(metadata)) { continue; }
                var entry = new RecycleEntry(path, stored, time, isDirectory);
                try
                {
                    // Write the recovery record before moving the data. Flush it before promotion.
                    using (reservation)
                    {
                        JsonSerializer.Serialize(reservation, entry);
                        reservation.Flush(flushToDisk: true);
                    }
                    Record("RecyclePrepared", path, incomingSource, entry);
                    Move(path, stored);
                    try { Record("Recycled", path, incomingSource, entry); }
                    catch { Move(stored, path); throw; }
                    return entry;
                }
                catch
                {
                    // A completed move always keeps its recovery record, even if later work fails.
                    if (!Exists(stored)) { try { File.Delete(metadata); } catch { } }
                    throw;
                }
            }
        }
    }


    private RecycleEntry RecycleLocal(string path, bool isDirectory, string? source)
    {
        var time = _clock();
        string extension = isDirectory ? "" : Path.GetExtension(path);
        string stem = isDirectory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        string reservations = Path.Combine(AppStoragePaths.GetDataDirectory(), "recycle-names");
        EnsureStorageDirectory(reservations);
        for (int suffix = 0; ; suffix++)
        {
            string name = $"{stem}_{time:yyyy-MM-dd_HH-mm-ss}{(suffix == 0 ? "" : "-" + suffix)}{extension}";
            string renamed = Path.Combine(Path.GetDirectoryName(path)!, name);
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(renamed.ToUpperInvariant())));
            string reservation = Path.Combine(reservations, key);
            if (Exists(renamed) || File.Exists(reservation)) continue;
            try { using var file = new FileStream(reservation, FileMode.CreateNew); }
            catch (IOException) when (File.Exists(reservation)) { continue; }
            var entry = new RecycleEntry(path, renamed, time, isDirectory, "Windows");
            Record("RecycleRequested", path, source, entry);
            Move(path, renamed);
            try
            {
                string nativeItem = _windows.Recycle(renamed);
                entry = entry with { NativeItem = nativeItem };
                try { Record("Recycled", path, source, entry); }
                catch { _windows.Restore(nativeItem, path); throw; }
                return entry;
            }
            catch (Exception error)
            {
                if (Exists(renamed) && !Exists(path)) Move(renamed, path);
                try { Record("RecycleFailed", path, source, entry, error.Message); } catch { }
                throw new IOException($"Windows could not recycle '{path}': {error.Message}. The operation was stopped.", error);
            }
        }
    }

    public RecycleEntry? GetEntry(string storedPath)
    {
        string fullPath = Path.GetFullPath(storedPath);
        string? bin = Path.GetDirectoryName(fullPath);
        if (bin == null || Path.GetFileName(bin) != DirectoryName) return null;
        string metadata = MetadataPath(fullPath);
        if (!File.Exists(metadata)) return null;
        var entry = JsonSerializer.Deserialize<RecycleEntry>(File.ReadAllText(metadata));
        if (entry == null || entry.Backend != "Managed" || entry.NativeItem != null)
            throw new IOException("The recycle record is invalid.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Never let a modified record restore outside the folder that owns this bin.
        if (!string.Equals(Path.GetFullPath(entry.StoredPath), fullPath, comparison) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(entry.OriginalPath)), Path.GetDirectoryName(bin), comparison))
            throw new IOException("The recycle record does not match its original folder.");
        return entry;
    }

    public string Restore(string storedPath)
    {
        lock (Gate)
        {
            var entry = GetEntry(storedPath) ?? throw new IOException("No recycle record exists for this item.");
            return RestoreEntry(entry);
        }
    }

    public string Restore(RecycleEntry entry) => RestoreEntry(entry);

    private string RestoreEntry(RecycleEntry entry)
    {
        lock (Gate)
        {
            if (Exists(entry.OriginalPath))
                throw new IOException("The original location already contains an item. Rename or move that item before restoring this version.");
            Record("RestoreRequested", entry.OriginalPath, entry: entry);
            if (entry.Backend == "Windows") _windows.Restore(entry.NativeItem!, entry.OriginalPath);
            else Move(entry.StoredPath, entry.OriginalPath);
            Record("Restored", entry.OriginalPath, entry: entry);
            if (entry.Backend == "Managed") { try { File.Delete(MetadataPath(entry.StoredPath)); } catch { } }
            return entry.OriginalPath;
        }
    }

    public RecycleEntry? CommitReplacement(string preparedPath, string destination, string? incomingSource = null)
    {
        lock (Gate)
        {
            File.GetAttributes(preparedPath); // Validate the new item before touching the destination.
            RecycleEntry? previous = Exists(destination) ? Recycle(destination, incomingSource) : null;
            try
            {
                // No overwrite flag: a newly appeared destination must never be silently destroyed.
                Record("ReplacementRequested", destination, incomingSource ?? preparedPath, previous);
                Move(preparedPath, destination);
                // A journal failure after commit must not trigger restoration over the incoming item.
                try { Record("Replaced", destination, incomingSource ?? preparedPath, previous); } catch { }
                return previous;
            }
            catch (Exception replacementError)
            {
                try { Record("ReplacementFailed", destination, incomingSource ?? preparedPath, previous, replacementError.Message); } catch { }
                if (previous != null)
                {
                    try { RestoreEntry(previous); }
                    catch (Exception rollbackError)
                    {
                        throw new IOException($"Replacement failed. The previous item is recoverable at '{previous.StoredPath}'. Restore failed: {rollbackError.Message}", replacementError);
                    }
                }
                throw;
            }
        }
    }
}
