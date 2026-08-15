using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class FileSystemServiceTests
{
    private readonly FileSystemService _service = new();

    [Fact]
    public async Task EnumerateDirectory_ReturnsFilesAndFolders()
    {
        using var fs = new TemporaryFileSystem();

        var (items, error) = await _service.ReadDirectoryAsync(fs.Root);

        Assert.Null(error);
        Assert.Contains(items, item => item.Name == "FolderA" && item.IsDirectory);
        Assert.Contains(items, item => item.Name == "FolderB" && item.IsDirectory);
        Assert.Contains(items, item => item.Name == "FolderC" && item.IsDirectory);
    }

    [Fact]
    public void CreateFileAndFolder_CreateOnlyInsideRequestedParent()
    {
        using var fs = new TemporaryFileSystem();

        _service.CreateFile(fs.FolderB, "created.txt");
        _service.CreateFolder(fs.FolderB, "CreatedFolder");

        Assert.True(File.Exists(Path.Combine(fs.FolderB, "created.txt")));
        Assert.True(Directory.Exists(Path.Combine(fs.FolderB, "CreatedFolder")));
    }

    [Fact]
    public void CreateFile_DoesNotTruncateExistingFile()
    {
        using var fs = new TemporaryFileSystem();
        var path = fs.CreateFile("FolderB/existing.txt", "keep me");

        Assert.Throws<IOException>(() => _service.CreateFile(fs.FolderB, "existing.txt"));

        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData(".")]
    [InlineData("..")]
    public void CreateAndRename_RejectPathTraversalNames(string invalidName)
    {
        using var fs = new TemporaryFileSystem();
        var source = Path.Combine(fs.FolderA, "alpha.txt");

        Assert.Throws<ArgumentException>(() => _service.CreateFile(fs.FolderB, invalidName));
        Assert.Throws<ArgumentException>(() => _service.Rename(source, invalidName));
    }

    [Fact]
    public void RenameFileAndFolder_PreserveContents()
    {
        using var fs = new TemporaryFileSystem();
        var file = Path.Combine(fs.FolderA, "alpha.txt");

        _service.Rename(file, "renamed.txt");
        _service.Rename(fs.FolderC, "RenamedFolder");

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(fs.FolderA, "renamed.txt")));
        Assert.True(File.Exists(Path.Combine(fs.Root, "RenamedFolder", "Nested", "nested.txt")));
    }

    [Fact]
    public void RenameConflict_PreservesBothItems()
    {
        using var fs = new TemporaryFileSystem();
        var alpha = Path.Combine(fs.FolderA, "alpha.txt");
        var beta = Path.Combine(fs.FolderA, "beta.txt");

        Assert.Throws<IOException>(() => _service.Rename(alpha, "beta.txt"));

        Assert.Equal("alpha", File.ReadAllText(alpha));
        Assert.Equal("beta", File.ReadAllText(beta));
    }

    [Fact]
    public async Task PermanentDelete_RemovesOnlyTemporaryFixtureTargets()
    {
        using var fs = new TemporaryFileSystem();
        var file = fs.CreateFile("FolderB/delete-me.txt");
        var folder = fs.CreateDirectory("delete-folder");
        fs.CreateFile("delete-folder/child.txt");

        await _service.DeleteAsync(new[] { file, folder }, permanent: true);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(fs.Root));
    }
}
