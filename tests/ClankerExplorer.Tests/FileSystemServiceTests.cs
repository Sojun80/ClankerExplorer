using System.Text;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;

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
    public async Task TabRefresh_ReflectsFilesCreatedAndRemovedAfterInitialLoad()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.FolderB);
        await tab.RefreshAsync();
        Assert.Empty(tab.Items);

        var created = Path.GetFullPath(fs.CreateFile("FolderB/appeared.txt", "new"));
        await tab.RefreshAsync();
        Assert.Contains(tab.Items, item => string.Equals(Path.GetFullPath(item.FullPath), created, StringComparison.OrdinalIgnoreCase));

        File.Delete(created);
        await tab.RefreshAsync();
        Assert.DoesNotContain(tab.Items, item => string.Equals(Path.GetFullPath(item.FullPath), created, StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void OpenWith_NonExistentOrInvalidFile_DoesNotThrow()
    {
        var ex1 = Record.Exception(() => _service.OpenWith(""));
        var ex2 = Record.Exception(() => _service.OpenWith(@"C:\non_existent_file_xyz_123.fake"));
        var ex3 = Record.Exception(() => _service.OpenWith("   "));
        Assert.Null(ex1);
        Assert.Null(ex2);
        Assert.Null(ex3);
    }

    [Fact]
    public async Task RunProcessWithTimeoutAsync_ArgumentList_PreservesSpecialArguments()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var fs = new TemporaryFileSystem();
        var scriptPath = fs.CreateFile("echo_args.ps1", "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8; foreach ($a in $args) { Write-Output ('ARG:' + $a) }");

        var args = new[]
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "path with spaces",
            "-leadingDash",
            "UNICODE_ファイル名_😊",
            @"\\server\share\folder"
        };

        var (stdout, stderr, exitCode) = await _service.RunProcessWithTimeoutAsync("powershell.exe", args, 5000, Encoding.UTF8);

        Assert.Equal(0, exitCode);
        Assert.Contains("ARG:path with spaces", stdout);
        Assert.Contains("ARG:-leadingDash", stdout);
        Assert.Contains("ARG:UNICODE_ファイル名_😊", stdout);
        Assert.Contains(@"ARG:\\server\share\folder", stdout);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\\")]
    [InlineData(@"c:\")]
    [InlineData(@"C:")]
    [InlineData(@"c:")]
    [InlineData(@"C:/")]
    public void IsProtectedDeleteTarget_RefusesDriveRoots(string target)
    {
        Assert.True(FileSystemService.IsProtectedDeleteTarget(target));
    }

    [Fact]
    public void IsProtectedDeleteTarget_RefusesWindowsDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(winDir)) return;

        Assert.True(FileSystemService.IsProtectedDeleteTarget(winDir));
        Assert.True(FileSystemService.IsProtectedDeleteTarget(winDir + @"\"));
        Assert.True(FileSystemService.IsProtectedDeleteTarget(winDir.ToLowerInvariant()));
        Assert.True(FileSystemService.IsProtectedDeleteTarget(winDir.ToUpperInvariant()));

        // Ordinary child under Windows folder must NOT be blocked
        var child = Path.Combine(winDir, "Temp", "junk");
        Assert.False(FileSystemService.IsProtectedDeleteTarget(child));
    }

    [Fact]
    public void IsProtectedDeleteTarget_RefusesUserProfileRoot()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return;

        Assert.True(FileSystemService.IsProtectedDeleteTarget(userProfile));
        Assert.True(FileSystemService.IsProtectedDeleteTarget(userProfile + Path.DirectorySeparatorChar));

        // Ordinary child folder under user profile must NOT be blocked
        var child = Path.Combine(userProfile, "Downloads", "test_junk");
        Assert.False(FileSystemService.IsProtectedDeleteTarget(child));
    }

    [Fact]
    public void IsProtectedDeleteTarget_RefusesPosixRootsAndHome()
    {
        Assert.True(FileSystemService.IsProtectedDeleteTarget("/", isWindows: false));
        Assert.True(FileSystemService.IsProtectedDeleteTarget("///", isWindows: false));
        Assert.True(FileSystemService.IsProtectedDeleteTarget("/home/sojun", isWindows: false, customUserProfile: "/home/sojun"));
        Assert.True(FileSystemService.IsProtectedDeleteTarget("/home/sojun/", isWindows: false, customUserProfile: "/home/sojun"));

        // Child folder underneath POSIX home MUST be allowed
        Assert.False(FileSystemService.IsProtectedDeleteTarget("/home/sojun/Downloads/test", isWindows: false, customUserProfile: "/home/sojun"));
    }

    [Fact]
    public void ValidatePermanentDeleteTarget_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FileSystemService.ValidatePermanentDeleteTarget(@"C:\"));
        Assert.Contains("Permanent deletion of protected filesystem location", ex.Message);
        Assert.Contains("was refused", ex.Message);
    }

    [Fact]
    public async Task PermanentDelete_ProtectedRoot_ThrowsAndDoesNotDelete()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _service.DeleteAsync(new[] { @"C:\" }, permanent: true);
        });
    }

    [Fact]
    public async Task PermanentDelete_DirectoryReparsePoint_DeletesLinkWithoutTouchingTargetFiles()
    {
        using var fs = new TemporaryFileSystem();
        var realFolder = fs.CreateDirectory("RealTargetFolder");
        var secretFile = fs.CreateFile("RealTargetFolder/keep_me.txt", "precious data");

        var linkFolder = Path.Combine(fs.FolderA, "LinkToTarget");
        try
        {
            Directory.CreateSymbolicLink(linkFolder, realFolder);
        }
        catch
        {
            // If running in environment without symlink privileges, ignore
            return;
        }

        Assert.True(Directory.Exists(linkFolder));
        Assert.True(File.Exists(secretFile));

        await _service.DeleteAsync(new[] { linkFolder }, permanent: true);

        // Link should be deleted
        Assert.False(Directory.Exists(linkFolder));
        // Real target folder and file MUST still exist!
        Assert.True(Directory.Exists(realFolder));
        Assert.True(File.Exists(secretFile));
        Assert.Equal("precious data", File.ReadAllText(secretFile));
    }
}

