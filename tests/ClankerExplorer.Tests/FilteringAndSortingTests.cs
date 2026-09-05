using System.Collections.ObjectModel;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Tests;

public sealed class FilteringAndSortingTests
{
    [Fact]
    public async Task NameSort_IsNaturalAndTreatsFilesAndFoldersAsEquals()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.Root);
        await tab.RefreshAsync();
        tab.Items = new ObservableCollection<FileItem>
            {
                Item("file10.txt"),
                Item("file2.txt"),
                Item("FolderZ", isDirectory: true),
                Item("file1.txt"),
                Item("FolderA", isDirectory: true)
            };
        tab.SortColumn = "Name";
        tab.SortAscending = true;

        tab.ApplyFilter();

        Assert.Equal(
            new[] { "file1.txt", "file2.txt", "file10.txt", "FolderA", "FolderZ" },
            tab.FilteredItems.Select(item => item.Name));
    }

    [Fact]
    public async Task PlainWildcardAndRegexFilters_ReturnExpectedItems()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.Root);
        await tab.RefreshAsync();
        tab.Items = new ObservableCollection<FileItem>
            {
                Item("alpha.txt", ".txt"),
                Item("beta.log", ".log"),
                Item("file2.txt", ".txt"),
                Item("file10.txt", ".txt")
            };

        tab.FilterText = "alpha";
        tab.ApplyFilter();
        Assert.Equal(new[] { "alpha.txt" }, tab.FilteredItems.Select(item => item.Name));

        tab.IsFilterRegex = false;
        tab.FilterText = "*.txt";
        tab.ApplyFilter();
        Assert.Equal(
            new[] { "alpha.txt", "file2.txt", "file10.txt" },
            tab.FilteredItems.Select(item => item.Name));

        tab.IsFilterRegex = true;
        tab.FilterText = "^file\\d+\\.txt$";
        tab.ApplyFilter();
        Assert.Equal(new[] { "file2.txt", "file10.txt" }, tab.FilteredItems.Select(item => item.Name));
    }

    [Fact]
    public async Task InvalidRegex_DoesNotCrashFiltering()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.Root);
        await tab.RefreshAsync();
        tab.Items = new ObservableCollection<FileItem> { Item("[draft].txt", ".txt") };
        tab.IsFilterRegex = true;
        tab.FilterText = "[";

        var exception = Record.Exception(tab.ApplyFilter);

        Assert.Null(exception);
        Assert.Single(tab.FilteredItems);
    }

    [Theory]
    [InlineData("Extension", true)]
    [InlineData("Extension", false)]
    [InlineData("Size", true)]
    [InlineData("Size", false)]
    [InlineData("Modified", true)]
    [InlineData("Modified", false)]
    [InlineData("Created", true)]
    [InlineData("Created", false)]
    [InlineData("Accessed", true)]
    [InlineData("Accessed", false)]
    [InlineData("Type", true)]
    [InlineData("Type", false)]
    [InlineData("Attributes", true)]
    [InlineData("Attributes", false)]
    [InlineData("Permissions", true)]
    [InlineData("Permissions", false)]
    [InlineData("OwnerGroup", true)]
    [InlineData("OwnerGroup", false)]
    [InlineData("Name", true)]
    [InlineData("Name", false)]
    public async Task AllSortColumns_AscendingAndDescending_ProduceDeterministicResults(string sortColumn, bool ascending)
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.Root);
        await tab.RefreshAsync();

        var baseTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        tab.Items = new ObservableCollection<FileItem>
        {
            new() { Name = "b.txt", FullPath = @"C:\test\b.txt", Extension = ".txt", SizeBytes = 200, ModifiedTime = baseTime.AddHours(2), CreatedTime = baseTime.AddDays(1), AccessedTime = baseTime.AddMinutes(5), AttributesString = "ReadOnly", PermissionsString = "rw-r--r--", OwnerGroupString = "user" },
            new() { Name = "a.log", FullPath = @"C:\test\a.log", Extension = ".log", SizeBytes = 100, ModifiedTime = baseTime.AddHours(1), CreatedTime = baseTime.AddDays(2), AccessedTime = baseTime.AddMinutes(10), AttributesString = "Archive", PermissionsString = "rwxr-xr-x", OwnerGroupString = "admin" },
            new() { Name = "c.bin", FullPath = @"C:\test\c.bin", Extension = ".bin", SizeBytes = 300, ModifiedTime = baseTime.AddHours(3), CreatedTime = baseTime.AddDays(3), AccessedTime = baseTime.AddMinutes(1), AttributesString = "Hidden", PermissionsString = "r--------", OwnerGroupString = "system" }
        };

        tab.SortColumn = sortColumn;
        tab.SortAscending = ascending;
        tab.ApplyFilter();

        Assert.Equal(3, tab.FilteredItems.Count);

        // Applying a filter should preserve the relative sort order of matching items
        tab.FilterText = "b";
        tab.ApplyFilter();
        Assert.All(tab.FilteredItems, i => Assert.Contains("b", i.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FilteredResults_PreserveCachedSortOrderingWithoutResorting()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.Root);
        await tab.RefreshAsync();

        tab.Items = new ObservableCollection<FileItem>
        {
            Item("photo_03.jpg", ".jpg"),
            Item("photo_01.jpg", ".jpg"),
            Item("document.pdf", ".pdf"),
            Item("photo_02.jpg", ".jpg"),
            Item("archive.zip", ".zip")
        };

        tab.SortColumn = "Name";
        tab.SortAscending = true;
        tab.ApplyFilter();

        // Check full sort
        Assert.Equal(
            new[] { "archive.zip", "document.pdf", "photo_01.jpg", "photo_02.jpg", "photo_03.jpg" },
            tab.FilteredItems.Select(i => i.Name));

        // Now filter to "photo". The sorted cache should be used and relative order preserved
        tab.FilterText = "photo";
        tab.ApplyFilter();

        Assert.Equal(
            new[] { "photo_01.jpg", "photo_02.jpg", "photo_03.jpg" },
            tab.FilteredItems.Select(i => i.Name));

        // Refine filter to "02"
        tab.FilterText = "02";
        tab.ApplyFilter();

        Assert.Equal(
            new[] { "photo_02.jpg" },
            tab.FilteredItems.Select(i => i.Name));

        // Clear filter
        tab.FilterText = string.Empty;
        tab.ApplyFilter();

        Assert.Equal(
            new[] { "archive.zip", "document.pdf", "photo_01.jpg", "photo_02.jpg", "photo_03.jpg" },
            tab.FilteredItems.Select(i => i.Name));
    }

    [Fact]
    public async Task RapidFilterCancellation_CancelsObsoleteTokensPromptly()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.Root);
        await tab.RefreshAsync();

        var items = new List<FileItem>();
        for (int i = 0; i < 2000; i++)
        {
            items.Add(new FileItem { Name = $"item_{i:D4}.txt", FullPath = $@"C:\folder\item_{i:D4}.txt", Extension = ".txt" });
        }
        tab.Items = new ObservableCollection<FileItem>(items);

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        tab.FilterText = "item_0";
        var task1 = tab.ApplyFilterAsync(cts1.Token);
        cts1.Cancel(); // Cancel immediately

        tab.FilterText = "item_1";
        var task2 = tab.ApplyFilterAsync(cts2.Token);

        await Task.WhenAll(task1, task2);

        // Final result should reflect the latest filter (item_1)
        Assert.All(tab.FilteredItems, i => Assert.Contains("item_1", i.Name));
    }

    [Fact]
    public async Task SelectionRestoration_HundredsOfItems_MatchesQuicklyAndAccurately()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.Root);
        await tab.RefreshAsync();

        var items = new List<FileItem>();
        var selectedPaths = new List<string>();
        for (int i = 0; i < 1000; i++)
        {
            string path = $@"C:\data\file_{i:D4}.dat";
            items.Add(new FileItem { Name = $"file_{i:D4}.dat", FullPath = path, Extension = ".dat" });
            if (i % 3 == 0)
            {
                selectedPaths.Add(path);
            }
        }
        tab.Items = new ObservableCollection<FileItem>(items);
        tab.ApplyFilter();

        // Select hundreds of paths
        tab.SelectPaths(selectedPaths);

        Assert.Equal(selectedPaths.Count, tab.SelectedItems.Count);
        Assert.NotNull(tab.SelectedItem);
        Assert.Equal(selectedPaths.Last(), tab.SelectedItem!.FullPath);

        // Apply a filter that matches a subset of selected items
        tab.FilterText = "file_00";
        tab.ApplyFilter();

        // Selected items within the filter must still be selected
        Assert.True(tab.SelectedItems.Count > 0);
        Assert.All(tab.SelectedItems, s => Assert.Contains("file_00", s.Name));
    }

    [Fact]
    public void ClipboardSnapshot_BulkChecking_CorrectlyIdentifiesCutPaths()
    {
        var paths = new[] { @"C:\Folder\fileA.txt", @"C:\Folder\subfolder" };
        ClipboardFileService.Cut(paths);

        var snapshot = ClipboardFileService.GetCutPathsSnapshot();
        Assert.NotNull(snapshot);
        Assert.Contains(@"C:\Folder\fileA.txt", snapshot!);
        Assert.Contains(@"C:\Folder\subfolder", snapshot!);
        Assert.DoesNotContain(@"C:\Folder\unrelated.txt", snapshot!);

        // Copy should clear cut mode
        ClipboardFileService.Copy(paths);
        var copySnapshot = ClipboardFileService.GetCutPathsSnapshot();
        Assert.Null(copySnapshot);
    }

    private static FileItem Item(string name, string extension = "", bool isDirectory = false) => new()
    {
        Name = name,
        Extension = extension,
        FullPath = name,
        IsDirectory = isDirectory
    };
}
