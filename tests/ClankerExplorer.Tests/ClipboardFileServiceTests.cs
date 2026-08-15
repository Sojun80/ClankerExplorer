using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class ClipboardFileServiceTests : IDisposable
{
    public ClipboardFileServiceTests() => ClipboardFileService.Copy(Array.Empty<string>());

    [Fact]
    public async Task CopyMultipleFiles_CopiesContentsAndReportsSuccess()
    {
        using var fs = new TemporaryFileSystem();
        var sources = new[]
        {
            Path.Combine(fs.FolderA, "alpha.txt"),
            Path.Combine(fs.FolderA, "beta.txt")
        };
        ClipboardFileService.Copy(sources);

        var result = await ClipboardFileService.PasteAsync(fs.FolderB);

        Assert.Equal(2, result.successCount);
        Assert.Empty(result.failedPaths);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(fs.FolderB, "alpha.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(fs.FolderB, "beta.txt")));
    }

    [Fact]
    public async Task DuplicateCopy_GeneratesNonConflictingNameWithoutOverwrite()
    {
        using var fs = new TemporaryFileSystem();
        var source = Path.Combine(fs.FolderA, "alpha.txt");
        fs.CreateFile("FolderB/alpha.txt", "original destination");
        ClipboardFileService.Copy(new[] { source });

        var result = await ClipboardFileService.PasteAsync(fs.FolderB);

        Assert.Equal(1, result.successCount);
        Assert.Equal("original destination", File.ReadAllText(Path.Combine(fs.FolderB, "alpha.txt")));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(fs.FolderB, "alpha (Copy).txt")));
    }

    [Fact]
    public async Task CutFile_MovesItAndClearsCutState()
    {
        using var fs = new TemporaryFileSystem();
        var source = Path.Combine(fs.FolderA, "alpha.txt");
        ClipboardFileService.Cut(new[] { source });

        var result = await ClipboardFileService.PasteAsync(fs.FolderB);

        Assert.Equal(1, result.successCount);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(fs.FolderB, "alpha.txt")));
        Assert.False(ClipboardFileService.IsCutMode);
        Assert.False(ClipboardFileService.CanPaste);
    }

    [Fact]
    public async Task MissingSource_IsReportedAndRetainedWhenCut()
    {
        using var fs = new TemporaryFileSystem();
        var missing = Path.Combine(fs.Root, "missing.txt");
        ClipboardFileService.Cut(new[] { missing });

        var result = await ClipboardFileService.PasteAsync(fs.FolderB);

        Assert.Equal(0, result.successCount);
        Assert.Equal(new[] { missing }, result.failedPaths);
        Assert.True(ClipboardFileService.IsCutMode);
        Assert.Contains(missing, ClipboardFileService.StoredPaths);
    }

    [Fact]
    public async Task MissingDestination_ReportsEveryPendingSourceAsFailed()
    {
        using var fs = new TemporaryFileSystem();
        var source = Path.Combine(fs.FolderA, "alpha.txt");
        ClipboardFileService.Copy(new[] { source });

        var result = await ClipboardFileService.PasteAsync(Path.Combine(fs.Root, "missing-destination"));

        Assert.Equal(0, result.successCount);
        Assert.Equal(new[] { source }, result.failedPaths);
    }

    [Fact]
    public async Task CopyDirectoryIntoOwnChild_IsRejected()
    {
        using var fs = new TemporaryFileSystem();
        ClipboardFileService.Copy(new[] { fs.FolderC });

        var result = await ClipboardFileService.PasteAsync(fs.Nested);

        Assert.Equal(0, result.successCount);
        Assert.Equal(new[] { fs.FolderC }, result.failedPaths);
        Assert.False(Directory.Exists(Path.Combine(fs.Nested, "FolderC")));
    }

    [Fact]
    public async Task CopyDirectory_PreservesNestedFiles()
    {
        using var fs = new TemporaryFileSystem();
        ClipboardFileService.Copy(new[] { fs.FolderC });

        var result = await ClipboardFileService.PasteAsync(fs.FolderB);

        Assert.Equal(1, result.successCount);
        Assert.Equal("nested", File.ReadAllText(Path.Combine(fs.FolderB, "FolderC", "Nested", "nested.txt")));
    }

    public void Dispose() => ClipboardFileService.Copy(Array.Empty<string>());
}
