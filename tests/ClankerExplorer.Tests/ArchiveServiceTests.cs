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
}
