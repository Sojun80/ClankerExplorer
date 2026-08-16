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
    public async Task CutFile_PastedIntoItsCurrentDirectoryIsASuccessfulNoOp()
    {
        using var fs = new TemporaryFileSystem();
        var source = Path.Combine(fs.FolderA, "alpha.txt");
        ClipboardFileService.Cut(new[] { source });

        var result = await ClipboardFileService.PasteAsync(fs.FolderA);

        Assert.Equal(1, result.successCount);
        Assert.Empty(result.failedPaths);
        Assert.Equal("alpha", File.ReadAllText(source));
        Assert.False(ClipboardFileService.CanPaste);
    }

    [Fact]
    public async Task CutFolder_MovesItsNestedContents()
    {
        using var fs = new TemporaryFileSystem();
        ClipboardFileService.Cut(new[] { fs.FolderC });

        var result = await ClipboardFileService.PasteAsync(fs.FolderB);

        Assert.Equal(1, result.successCount);
        Assert.False(Directory.Exists(fs.FolderC));
        Assert.Equal(
            "nested",
            File.ReadAllText(Path.Combine(fs.FolderB, "FolderC", "Nested", "nested.txt")));
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
        Assert.Equal(new[] { Path.Combine(fs.FolderB, "FolderC") }, result.createdDestinationPaths);
    }

    [Fact]
    public async Task PasteAsync_ReturnsCreatedDestinationPaths_ForMultipleItems()
    {
        using var fs = new TemporaryFileSystem();
        var file1 = Path.Combine(fs.FolderA, "alpha.txt");
        var file2 = Path.Combine(fs.FolderA, "beta.txt");
        ClipboardFileService.Copy(new[] { file1, file2, fs.FolderC });

        var result = await ClipboardFileService.PasteAsync(fs.FolderB);

        Assert.Equal(3, result.successCount);
        Assert.Equal(3, result.createdDestinationPaths.Count);
        Assert.Contains(Path.Combine(fs.FolderB, "alpha.txt"), result.createdDestinationPaths);
        Assert.Contains(Path.Combine(fs.FolderB, "beta.txt"), result.createdDestinationPaths);
        Assert.Contains(Path.Combine(fs.FolderB, "FolderC"), result.createdDestinationPaths);
    }

    public void Dispose() => ClipboardFileService.Copy(Array.Empty<string>());
}
