using System.Text.Json;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class RecycleRoutingTests
{
    [Theory]
    [InlineData(@"\\server\share\file.txt", true)]
    [InlineData(@"\\?\UNC\server\share\file.txt", true)]
    [InlineData(@"C:\file.txt", false)]
    public void DetectsNetworkPaths(string path, bool expected) => Assert.Equal(expected, RecycleBinService.IsNetworkPath(path));

    [Fact]
    public void LocalRecycle_UsesWindowsAndRecordsIncomingSource()
    {
        using var fs = new TemporaryFileSystem();
        var native = new FakeWindowsBin(fs.Root);
        var service = new RecycleBinService(isNetworkPath: _ => false, windows: native);
        string old = fs.CreateFile("FolderB/report.txt", "old");
        string incoming = fs.CreateFile("FolderA/source.txt", "new");
        var entry = service.CommitReplacement(incoming, old, incoming)!;
        Assert.Equal("Windows", entry.Backend);
        Assert.Equal("old", File.ReadAllText(entry.NativeItem!));
        Assert.Equal("new", File.ReadAllText(old));
        Assert.False(Directory.Exists(RecycleBinService.GetBinDirectory(fs.FolderB)));
        var lines = File.ReadAllLines(service.HistoryPath).Select(line => JsonDocument.Parse(line)).ToList();
        Assert.Contains(lines, line => line.RootElement.GetProperty("Action").GetString() == "Replaced" &&
            line.RootElement.GetProperty("IncomingSource").GetString() == Path.GetFullPath(incoming));
        foreach (var line in lines) line.Dispose();
    }

    [Fact]
    public void WindowsRecycleFailure_FallsBackToManagedBin()
    {
        // e.g. a USB drive (no Windows bin) or an item too large for the bin.
        using var fs = new TemporaryFileSystem();
        var service = new RecycleBinService(isNetworkPath: _ => false, windows: new FakeWindowsBin(fs.Root) { Fail = true });
        string old = fs.CreateFile("FolderB/report.txt", "old");
        string incoming = fs.CreateFile("FolderA/source.txt", "new");
        var entry = service.CommitReplacement(incoming, old)!;
        Assert.Equal("Managed", entry.Backend);
        Assert.Equal("new", File.ReadAllText(old));
        Assert.Equal("old", File.ReadAllText(entry.StoredPath));
        Assert.Single(Directory.GetFiles(fs.FolderB));
        string reservations = Path.Combine(AppStoragePaths.GetDataDirectory(), "recycle-names");
        Assert.Empty(Directory.Exists(reservations) ? Directory.GetFiles(reservations) : Array.Empty<string>());
    }

    [Fact]
    public void WindowsRecycle_RemovesNameReservation()
    {
        using var fs = new TemporaryFileSystem();
        var service = new RecycleBinService(isNetworkPath: _ => false, windows: new FakeWindowsBin(fs.Root));
        string old = fs.CreateFile("FolderB/report.txt", "old");
        service.Recycle(old);
        string reservations = Path.Combine(AppStoragePaths.GetDataDirectory(), "recycle-names");
        Assert.Empty(Directory.Exists(reservations) ? Directory.GetFiles(reservations) : Array.Empty<string>());
    }

    [Fact]
    public void Commit_WithNothingToReplace_SkipsJournal()
    {
        using var fs = new TemporaryFileSystem();
        var service = new RecycleBinService(isNetworkPath: _ => true);
        string incoming = fs.CreateFile("FolderA/source.txt", "new");
        string destination = Path.Combine(fs.FolderB, "fresh.txt");
        long historyBefore = File.Exists(service.HistoryPath) ? new FileInfo(service.HistoryPath).Length : 0;
        // An unwritable history must not block a copy that destroys nothing.
        using (new FileStream(service.HistoryPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(service.CommitReplacement(incoming, destination));
        }
        Assert.Equal("new", File.ReadAllText(destination));
        Assert.Equal(historyBefore, new FileInfo(service.HistoryPath).Length);
    }

    [Fact]
    public void Commit_FileOverFolder_IsRefusedAndLeavesFolderInPlace()
    {
        using var fs = new TemporaryFileSystem();
        var service = new RecycleBinService(isNetworkPath: _ => true);
        string folder = Path.Combine(fs.FolderB, "data");
        fs.CreateFile("FolderB/data/keep.txt", "keep");
        string incoming = fs.CreateFile("FolderA/data", "file");
        Assert.Throws<IOException>(() => service.CommitReplacement(incoming, folder));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "keep.txt")));
        Assert.Equal("file", File.ReadAllText(incoming));
        Assert.False(Directory.Exists(RecycleBinService.GetBinDirectory(fs.FolderB)));
    }

    [Fact]
    public void LocalPromotionFailure_RestoresFromWindowsBin()
    {
        using var fs = new TemporaryFileSystem();
        var service = new RecycleBinService(isNetworkPath: _ => false, windows: new FakeWindowsBin(fs.Root));
        string old = fs.CreateFile("FolderB/report.txt", "old");
        string incoming = fs.CreateFile("FolderA/source.txt", "new");
        using var held = new FileStream(incoming, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() => service.CommitReplacement(incoming, old));
        Assert.Equal("old", File.ReadAllText(old));
    }

    [Fact]
    public void LockedHistory_PreventsDestructiveWork()
    {
        using var fs = new TemporaryFileSystem();
        var service = new RecycleBinService(isNetworkPath: _ => true);
        string old = fs.CreateFile("FolderB/report.txt", "old");
        string incoming = fs.CreateFile("FolderA/source.txt", "new");
        using var history = new FileStream(service.HistoryPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() => service.CommitReplacement(incoming, old));
        Assert.Equal("old", File.ReadAllText(old));
        Assert.Equal("new", File.ReadAllText(incoming));
    }

    [Fact]
    public async Task NormalDelete_UsesManagedBinAndKeepsRecord()
    {
        using var fs = new TemporaryFileSystem();
        string path = fs.CreateFile("FolderB/deleted.txt", "recover me");
        await new FileSystemService().DeleteAsync(new[] { path });
        Assert.False(File.Exists(path));
        string saved = Assert.Single(Directory.GetFiles(RecycleBinService.GetBinDirectory(fs.FolderB), "*.txt"));
        Assert.Equal("recover me", File.ReadAllText(saved));
        Assert.Equal(Path.GetFullPath(path), RecycleBinService.Instance.GetEntry(saved)!.OriginalPath);
        Assert.Contains(Path.GetFullPath(path).Replace("\\", "\\\\"), File.ReadAllText(RecycleBinService.Instance.HistoryPath));
    }

    [WindowsRecycleFact]
    public void RealWindowsBin_OptInSmokeTest()
    {
        string file = Path.Combine(AppContext.BaseDirectory, $"native-smoke-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "synthetic recycle test");
        var service = new RecycleBinService();
        try
        {
            var entry = service.Recycle(file);
            Assert.Equal("Windows", entry.Backend);
            Assert.NotNull(entry.NativeItem);
            Assert.False(File.Exists(file));
            service.Restore(entry);
            Assert.Equal("synthetic recycle test", File.ReadAllText(file));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    private sealed class FakeWindowsBin(string root) : IWindowsRecycleBin
    {
        public bool Fail { get; init; }
        public string Recycle(string path)
        {
            if (Fail) throw new IOException("Simulated Windows recycle failure");
            string destination = Path.Combine(root, "native-" + Guid.NewGuid());
            File.Move(path, destination);
            return destination;
        }
        public void Restore(string item, string destination) => File.Move(item, destination);
    }
}

public sealed class WindowsRecycleFactAttribute : FactAttribute
{
    public WindowsRecycleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLANKER_TEST_WINDOWS_RECYCLE") != "1")
            Skip = "Set CLANKER_TEST_WINDOWS_RECYCLE=1 for a real Windows Recycle Bin round trip.";
    }
}
