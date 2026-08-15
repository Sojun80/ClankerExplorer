using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class QuickAccessServiceTests
{
    [Fact]
    public void PinDuplicateUnpinAndReload_PreserveExpectedState()
    {
        using var fs = new TemporaryFileSystem();
        var service = new QuickAccessService(fs.Config, populateDefaultsWhenEmpty: false);

        service.PinFolder(fs.FolderA, "Alpha");
        service.PinFolder(fs.FolderA, "Duplicate");

        var pinned = Assert.Single(service.Items);
        Assert.Equal("Alpha", pinned.DisplayName);
        Assert.True(service.IsPinned(fs.FolderA));

        var reloaded = new QuickAccessService(fs.Config, populateDefaultsWhenEmpty: false);
        Assert.Single(reloaded.Items);
        Assert.Equal(fs.FolderA, reloaded.Items[0].Path);

        reloaded.UnpinFolder(fs.FolderA);
        Assert.Empty(reloaded.Items);
        Assert.True(Directory.Exists(fs.FolderA));
    }

    [Fact]
    public void Reorder_PersistsAcrossReload()
    {
        using var fs = new TemporaryFileSystem();
        var service = new QuickAccessService(fs.Config, populateDefaultsWhenEmpty: false);
        service.PinFolder(fs.FolderA, "A");
        service.PinFolder(fs.FolderB, "B");
        service.PinFolder(fs.FolderC, "C");

        service.MoveItem(0, 2);
        var reloaded = new QuickAccessService(fs.Config, populateDefaultsWhenEmpty: false);

        Assert.Equal(new[] { "B", "C", "A" }, reloaded.Items.Select(item => item.DisplayName));
    }

    [Fact]
    public void Unpin_RemovesOnlyShortcutNotRealFolder()
    {
        using var fs = new TemporaryFileSystem();
        var sentinel = fs.CreateFile("FolderB/keep.txt", "safe");
        var service = new QuickAccessService(fs.Config, populateDefaultsWhenEmpty: false);
        service.PinFolder(fs.FolderB);

        service.UnpinFolder(fs.FolderB);

        Assert.False(service.IsPinned(fs.FolderB));
        Assert.Equal("safe", File.ReadAllText(sentinel));
    }
}
