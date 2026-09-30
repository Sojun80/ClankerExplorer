using System.IO.Compression;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class RecycleBinServiceTests
{
    [Fact]
    public void SameSecondVersions_KeepDistinctNamesAndRestoreMetadata()
    {
        using var fs = new TemporaryFileSystem();
        var time = new DateTimeOffset(2026, 9, 29, 12, 34, 56, TimeSpan.Zero);
        var service = new RecycleBinService(() => time, isNetworkPath: _ => true);
        string path = fs.CreateFile("FolderB/report.txt", "version zero");
        var zero = service.Recycle(path);
        File.WriteAllText(path, "version one");
        var one = service.Recycle(path);
        File.WriteAllText(path, "version two");
        var two = service.Recycle(path);

        Assert.Equal("report_2026-09-29_12-34-56.txt", Path.GetFileName(zero.StoredPath));
        Assert.Equal("report_2026-09-29_12-34-56-1.txt", Path.GetFileName(one.StoredPath));
        Assert.Equal("report_2026-09-29_12-34-56-2.txt", Path.GetFileName(two.StoredPath));
        Assert.Equal("version zero", File.ReadAllText(zero.StoredPath));
        Assert.Equal("version one", File.ReadAllText(one.StoredPath));
        Assert.Equal("version two", File.ReadAllText(two.StoredPath));
        Assert.Equal(Path.GetFullPath(path), new RecycleBinService(isNetworkPath: _ => true).GetEntry(one.StoredPath)!.OriginalPath);
        Assert.Equal(time, new RecycleBinService(isNetworkPath: _ => true).GetEntry(one.StoredPath)!.RecycledAt);
        Assert.Equal(Path.GetFullPath(path), new RecycleBinService(isNetworkPath: _ => true).Restore(one.StoredPath));
        Assert.Equal("version one", File.ReadAllText(path));
    }

    [Fact]
    public void Restore_RefusesToOverwriteANewFile()
    {
        using var fs = new TemporaryFileSystem();
        var service = new RecycleBinService(isNetworkPath: _ => true);
        string path = fs.CreateFile("FolderB/report.txt", "old");
        var entry = service.Recycle(path);
        File.WriteAllText(path, "new");

        Assert.Throws<IOException>(() => service.Restore(entry.StoredPath));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("old", File.ReadAllText(entry.StoredPath));
    }

    [Fact]
    public void FailedPromotion_RestoresOriginalDestination()
    {
        using var fs = new TemporaryFileSystem();
        var service = new RecycleBinService(isNetworkPath: _ => true);
        string original = fs.CreateFile("FolderB/report.txt", "old");
        string prepared = fs.CreateFile("FolderA/prepared.txt", "new");
        using var locked = new FileStream(prepared, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => service.CommitReplacement(prepared, original));
        Assert.Equal("old", File.ReadAllText(original));
    }

    [Fact]
    public void DirectoryRecycleAndRestore_PreservesContents()
    {
        using var fs = new TemporaryFileSystem();
        var service = new RecycleBinService(isNetworkPath: _ => true);
        string path = fs.CreateDirectory("FolderB/documents");
        File.WriteAllText(Path.Combine(path, "file.txt"), "contents");
        var entry = service.Recycle(path);

        Assert.False(Directory.Exists(path));
        Assert.True(entry.IsDirectory);
        Assert.Equal("contents", File.ReadAllText(Path.Combine(entry.StoredPath, "file.txt")));
        service.Restore(entry.StoredPath);
        Assert.Equal("contents", File.ReadAllText(Path.Combine(path, "file.txt")));
    }

    [Fact]
    public void ArchivePromotion_RecyclesAnOverwrittenFile()
    {
        using var fs = new TemporaryFileSystem();
        string staging = fs.CreateDirectory("staging");
        File.WriteAllText(Path.Combine(staging, "item.txt"), "new");
        string original = fs.CreateFile("FolderB/item.txt", "old");

        var result = ArchiveService.PromoteStagingDirectory(staging, fs.FolderB, overwrite: true);

        Assert.True(result.success);
        Assert.Equal("new", File.ReadAllText(original));
        string saved = Assert.Single(Directory.GetFiles(RecycleBinService.GetBinDirectory(fs.FolderB), "*.txt"));
        Assert.Equal("old", File.ReadAllText(saved));
    }
}
