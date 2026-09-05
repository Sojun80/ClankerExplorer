using System.IO.Compression;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class ArchiveServiceTests
{
    [Fact]
    public async Task ExtractZip_RejectsEntriesThatEscapeDestination()
    {
        using var fs = new TemporaryFileSystem();
        var archivePath = Path.Combine(fs.FolderB, "malicious.zip");
        var destination = fs.CreateDirectory("FolderB/extracted");
        var escapedPath = Path.Combine(fs.Root, "escaped.txt");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../../escaped.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("must not be written");
        }

        var result = await ArchiveService.Instance.ExtractToAsync(archivePath, destination);

        Assert.False(result.success);
        Assert.Contains("rejected", result.message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(escapedPath));
        Assert.False(File.Exists(Path.Combine(destination, "escaped.txt")));
    }

    [Fact]
    public async Task ExtractZip_ExtractsToDestinationWithSpaces()
    {
        using var fs = new TemporaryFileSystem();
        var archivePath = Path.Combine(fs.FolderB, "archive with spaces.zip");
        var destination = fs.CreateDirectory("FolderB/extracted destination with spaces");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("inner file with spaces.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("archive content");
        }

        var result = await ArchiveService.Instance.ExtractToAsync(archivePath, destination);

        Assert.True(result.success, result.message);
        var expected = Path.Combine(destination, "inner file with spaces.txt");
        Assert.True(File.Exists(expected));
        Assert.Equal("archive content", File.ReadAllText(expected));
    }

    [Fact]
    public async Task CreateZip_CreatesZipInDestinationWithSpaces()
    {
        using var fs = new TemporaryFileSystem();
        var sourceFile = fs.CreateFile("FolderA/source file with spaces.txt", "content to zip");
        var targetZip = Path.Combine(fs.FolderB, "output zip with spaces.zip");

        var result = await ArchiveService.Instance.CreateZipAsync(sourceFile, targetZip);

        Assert.True(result.success, result.message);
        Assert.True(File.Exists(targetZip));
    }
}

