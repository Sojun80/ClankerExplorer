using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using ClankerExplorer.Services.Metadata;
using ClankerExplorer.Services.Metadata.Providers;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class MetadataSubsystemTests
{
    [Fact]
    public async Task FileSystemMetadataProvider_ExtractsExpectedFields()
    {
        using var fs = new TemporaryFileSystem();
        string testFile = fs.CreateFile("FolderA/sample.txt", "Hello World! This is a test file for metadata extraction.");

        var provider = new FileSystemMetadataProvider();
        var ctx = new MetadataExtractionContext(testFile);

        Assert.True(provider.CanHandle(ctx));
        await provider.ProvideMetadataAsync(ctx, default);

        var sections = ctx.BuildSections();
        Assert.NotEmpty(sections);

        var general = sections.FirstOrDefault(s => s.Title == "General");
        Assert.NotNull(general);
        Assert.Contains(general.Items, i => i.Key == "Name" && i.Value == "sample.txt");
        Assert.Contains(general.Items, i => i.Key == "Type" && i.Value.Contains("Text Document"));
        Assert.Contains(general.Items, i => i.Key == "Size");

        var dates = sections.FirstOrDefault(s => s.Title == "Dates");
        Assert.NotNull(dates);
        Assert.Contains(dates.Items, i => i.Key == "Modified");
        Assert.Contains(dates.Items, i => i.Key == "Created");

        var attributes = sections.FirstOrDefault(s => s.Title == "Attributes");
        Assert.NotNull(attributes);
        Assert.Contains(attributes.Items, i => i.Key == "Flags");
    }

    [Fact]
    public async Task TextMetadataProvider_DetectsUtf8AndLineEndings()
    {
        using var fs = new TemporaryFileSystem();
        string crlfContent = "Line 1\r\nLine 2\r\nLine 3\r\n";
        string filePath = fs.CreateFile("FolderA/crlf.txt", crlfContent);

        var provider = new TextMetadataProvider();
        var ctx = new MetadataExtractionContext(filePath);

        Assert.True(provider.CanHandle(ctx));
        await provider.ProvideMetadataAsync(ctx, default);

        var sections = ctx.BuildSections();
        var textSection = sections.FirstOrDefault(s => s.Title == "Text Details");
        Assert.NotNull(textSection);

        Assert.Contains(textSection.Items, i => i.Key == "Encoding" && i.Value.Contains("UTF-8"));
        Assert.Contains(textSection.Items, i => i.Key == "Line Endings" && i.Value.Contains("CRLF"));
        Assert.Contains(textSection.Items, i => i.Key == "Line Count" && i.Value.StartsWith("3"));
    }

    [Fact]
    public async Task ArchiveMetadataProvider_ExtractsZipSummary()
    {
        using var fs = new TemporaryFileSystem();
        string zipPath = Path.Combine(fs.Root, "test_archive.zip");

        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var entry1 = archive.CreateEntry("doc1.txt");
            using (var writer = new StreamWriter(entry1.Open()))
            {
                writer.Write("Document 1 content with lots of text to achieve compression.");
            }

            var entry2 = archive.CreateEntry("doc2.txt");
            using (var writer = new StreamWriter(entry2.Open()))
            {
                writer.Write("Document 2 another piece of text for compression ratio.");
            }
        }

        var provider = new ArchiveMetadataProvider();
        var ctx = new MetadataExtractionContext(zipPath);

        Assert.True(provider.CanHandle(ctx));
        await provider.ProvideMetadataAsync(ctx, default);

        var sections = ctx.BuildSections();
        var archiveSection = sections.FirstOrDefault(s => s.Title == "Archive");
        Assert.NotNull(archiveSection);

        Assert.Contains(archiveSection.Items, i => i.Key == "Archive Type" && i.Value.Contains("ZIP"));
        Assert.Contains(archiveSection.Items, i => i.Key == "Entries" && i.Value.Contains("2"));
        Assert.Contains(archiveSection.Items, i => i.Key == "Unpacked Size");
        Assert.Contains(archiveSection.Items, i => i.Key == "Packed Size");
        Assert.Contains(archiveSection.Items, i => i.Key == "Compression Ratio");
    }

    [Fact]
    public async Task FileMetadataCache_CachesAndReturnsMetadata()
    {
        using var fs = new TemporaryFileSystem();
        string testFile = fs.CreateFile("FolderA/cached_item.txt", "Cache test content");

        var service = FileMetadataService.Instance;

        var meta1 = await service.GetMetadataAsync(testFile);
        Assert.NotNull(meta1);
        Assert.Equal("cached_item.txt", meta1.ItemName);

        // Second call should return cached instance
        var meta2 = await service.GetMetadataAsync(testFile);
        Assert.Same(meta1, meta2);

        // Calculate hashes
        var hashes = await service.CalculateHashesAsync(testFile);
        Assert.False(string.IsNullOrEmpty(hashes.Sha256));
        Assert.False(string.IsNullOrEmpty(hashes.Md5));
    }
}
