using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using ClankerExplorer.Models;
using ClankerExplorer.ViewModels;
using ClankerExplorer.Tests.TestInfrastructure;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class SelectionAnchorTests
{
    [Fact]
    public async Task SetSelectionAnchor_ValidItem_SetsAnchor_InvalidItem_ClearsAnchor()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.FolderA);
        await tab.RefreshAsync();

        var item1 = new FileItem { Name = "a.txt", FullPath = Path.Combine(fs.FolderA, "a.txt") };
        var item2 = new FileItem { Name = "b.txt", FullPath = Path.Combine(fs.FolderA, "b.txt") };
        var foreignItem = new FileItem { Name = "foreign.txt", FullPath = Path.Combine(fs.FolderB, "foreign.txt") };

        tab.Items = new ObservableCollection<FileItem> { item1, item2 };
        tab.ApplyFilter();

        // Valid item sets anchor
        tab.SetSelectionAnchor(item1);
        var range = tab.GetSelectionRange(item2);
        Assert.Equal(2, range.Count);
        Assert.Equal(item1, range[0]);
        Assert.Equal(item2, range[1]);

        // Foreign item clears anchor
        tab.SetSelectionAnchor(foreignItem);
        // Range with no anchor and no SelectedItem starts anchor at item2
        var rangeSelf = tab.GetSelectionRange(item2);
        Assert.Single(rangeSelf);
        Assert.Equal(item2, rangeSelf[0]);

        // Null clears anchor
        tab.SetSelectionAnchor(null);
        var rangeNull = tab.GetSelectionRange(item1);
        Assert.Single(rangeNull);
        Assert.Equal(item1, rangeNull[0]);
    }

    [Fact]
    public async Task GetSelectionRange_PreservesAnchorAcrossReordering()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.FolderA);
        await tab.RefreshAsync();

        var item1 = new FileItem { Name = "a.txt", FullPath = Path.Combine(fs.FolderA, "a.txt") };
        var item2 = new FileItem { Name = "b.txt", FullPath = Path.Combine(fs.FolderA, "b.txt") };
        var item3 = new FileItem { Name = "c.txt", FullPath = Path.Combine(fs.FolderA, "c.txt") };

        tab.Items = new ObservableCollection<FileItem> { item1, item2, item3 };
        tab.SortColumn = "Name";
        tab.SortAscending = true;
        tab.ApplyFilter();

        // Set anchor to item1 (at index 0 in ascending order)
        tab.SetSelectionAnchor(item1);

        // Reverse sort order so item1 moves to index 2
        tab.SortAscending = false;
        tab.ApplyFilter();

        // Item1 is now at index 2, item2 is at index 1, item3 is at index 0
        // Getting selection range to item2 (index 1) should span index 1..2 (item2, item1)
        var range = tab.GetSelectionRange(item2);
        Assert.Equal(2, range.Count);
        Assert.Equal(item2, range[0]);
        Assert.Equal(item1, range[1]);
    }

    [Fact]
    public async Task GetSelectionRange_AnchorVanished_FallsBackToSelectedItem()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.FolderA);
        await tab.RefreshAsync();

        var item1 = new FileItem { Name = "a.txt", FullPath = Path.Combine(fs.FolderA, "a.txt") };
        var item2 = new FileItem { Name = "b.txt", FullPath = Path.Combine(fs.FolderA, "b.txt") };
        var item3 = new FileItem { Name = "c.txt", FullPath = Path.Combine(fs.FolderA, "c.txt") };

        tab.Items = new ObservableCollection<FileItem> { item1, item2, item3 };
        tab.ApplyFilter();

        tab.SetSelectionAnchor(item1);
        tab.SelectedItem = item2;

        // Simulate item1 being removed
        tab.Items = new ObservableCollection<FileItem> { item2, item3 };
        tab.ApplyFilter();

        // Old anchor (item1) is not in FilteredItems anymore.
        // GetSelectionRange to item3 should fallback to SelectedItem (item2) as anchor.
        var range = tab.GetSelectionRange(item3);
        Assert.Equal(2, range.Count);
        Assert.Equal(item2, range[0]);
        Assert.Equal(item3, range[1]);
    }

    [Fact]
    public async Task SelectThumbnailItem_ShiftSelection_MaintainsAnchorForConsecutiveShifts()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.FolderA);
        await tab.RefreshAsync();

        tab.Items = new ObservableCollection<FileItem>(
            Enumerable.Range(0, 5).Select(i => new FileItem { Name = $"file{i}.txt", FullPath = Path.Combine(fs.FolderA, $"file{i}.txt") }));
        tab.SortColumn = "Name";
        tab.SortAscending = true;
        tab.ApplyFilter();

        var items = tab.FilteredItems;

        // Plain click on index 1 establishes anchor
        tab.SelectThumbnailItem(items[1], control: false, shift: false);
        Assert.Single(tab.SelectedItems);
        Assert.Equal(items[1], tab.SelectedItem);

        // Shift click on index 3 -> range 1..3
        tab.SelectThumbnailItem(items[3], control: false, shift: true);
        Assert.Equal(3, tab.SelectedItems.Count);
        Assert.Equal(new[] { items[1], items[2], items[3] }, tab.SelectedItems);

        // Another Shift click on index 4 -> range 1..4 (anchor remains at index 1)
        tab.SelectThumbnailItem(items[4], control: false, shift: true);
        Assert.Equal(4, tab.SelectedItems.Count);
        Assert.Equal(new[] { items[1], items[2], items[3], items[4] }, tab.SelectedItems);

        // Shift click backwards on index 0 -> range 0..1 (anchor remains at index 1)
        tab.SelectThumbnailItem(items[0], control: false, shift: true);
        Assert.Equal(2, tab.SelectedItems.Count);
        Assert.Equal(new[] { items[0], items[1] }, tab.SelectedItems);
    }

    [Fact]
    public async Task SelectAll_SetsAnchorToSelectedItem()
    {
        using var fs = new TemporaryFileSystem();
        using var tab = new ExplorerTabViewModel(fs.FolderA);
        await tab.RefreshAsync();

        tab.Items = new ObservableCollection<FileItem>(
            Enumerable.Range(0, 3).Select(i => new FileItem { Name = $"file{i}.txt", FullPath = Path.Combine(fs.FolderA, $"file{i}.txt") }));
        tab.SortColumn = "Name";
        tab.SortAscending = true;
        tab.ApplyFilter();

        var items = tab.FilteredItems;

        // Details SelectAll
        tab.SelectAll(isThumbnailView: false);
        Assert.Equal(items.Last(), tab.SelectedItem);

        // Range from items[0] should span 0..2 because anchor was set to items[2]
        var range = tab.GetSelectionRange(items[0]);
        Assert.Equal(3, range.Count);

        // Thumbnail SelectAll
        tab.SelectAll(isThumbnailView: true);
        Assert.Equal(items.Last(), tab.SelectedItem);

        range = tab.GetSelectionRange(items[1]);
        Assert.Equal(2, range.Count);
        Assert.Equal(items[1], range[0]);
        Assert.Equal(items[2], range[1]);
    }
}
