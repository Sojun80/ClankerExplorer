using System.Collections.ObjectModel;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Tests;

public sealed class FilteringAndSortingTests
{
    [Fact]
    public async Task NameSort_IsNaturalAndKeepsFoldersFirst()
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
            new[] { "FolderA", "FolderZ", "file1.txt", "file2.txt", "file10.txt" },
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

    private static FileItem Item(string name, string extension = "", bool isDirectory = false) => new()
    {
        Name = name,
        Extension = extension,
        FullPath = name,
        IsDirectory = isDirectory
    };
}
