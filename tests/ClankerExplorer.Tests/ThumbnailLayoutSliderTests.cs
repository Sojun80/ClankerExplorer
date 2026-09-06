using Avalonia.Headless.XUnit;
using ClankerExplorer.Models;
using ClankerExplorer.ViewModels;
using Xunit;

namespace ClankerExplorer.Tests;

public class ThumbnailLayoutSliderTests
{
    [AvaloniaFact]
    public void ThumbnailSize_Increase_RecalculatesColumnsWithoutViewportSizeChanged()
    {
        // 1. set viewport width
        // 2. set small ThumbnailSize
        // 3. record ThumbnailColumnCount
        // 4. change ThumbnailSize substantially larger (do NOT call UpdateThumbnailViewportWidth again)
        // 5. assert ThumbnailColumnCount decreases appropriately (reproduces the original bug)
        var pane = new ExplorerPaneViewModel("test", @"C:\FakePath");
        pane.SetThumbnailView();
        pane.UpdateThumbnailViewportWidth(1000);

        pane.ThumbnailSize = 64;
        int initialColumns = pane.ThumbnailColumnCount;
        // At 1000px viewport, 64px size: (1000 - 8) / (64 + 28 + 8) = 992 / 100 = 9 columns
        Assert.Equal(9, initialColumns);

        // Change size substantially larger without calling UpdateThumbnailViewportWidth
        pane.ThumbnailSize = 250;
        int newColumns = pane.ThumbnailColumnCount;
        // At 1000px viewport, 250px size: (1000 - 8) / (250 + 28 + 8) = 992 / 286 = 3 columns
        Assert.Equal(3, newColumns);
        Assert.True(newColumns < initialColumns);
    }

    [AvaloniaFact]
    public void ThumbnailSize_Decrease_RecalculatesColumns()
    {
        // large ThumbnailSize -> few columns
        // change to small ThumbnailSize -> more columns
        // Assert column count increases.
        var pane = new ExplorerPaneViewModel("test", @"C:\FakePath");
        pane.SetThumbnailView();
        pane.UpdateThumbnailViewportWidth(1000);

        pane.ThumbnailSize = 250;
        Assert.Equal(3, pane.ThumbnailColumnCount);

        pane.ThumbnailSize = 96;
        // At 1000px viewport, 96px size: 992 / (96 + 36) = 992 / 132 = 7 columns
        Assert.Equal(7, pane.ThumbnailColumnCount);
        Assert.True(pane.ThumbnailColumnCount > 3);
    }

    [AvaloniaFact]
    public void ThumbnailSize_SameColumnResize_AvoidsRegroup()
    {
        // Choose two thumbnail sizes that result in the same column count.
        // Change size.
        // Assert:
        // ThumbnailColumnCount unchanged
        // ThumbnailRows grouping unchanged (same reference, no regrouping)
        var pane = new ExplorerPaneViewModel("test", @"C:\FakePath");
        var tab = pane.SelectedTab!;
        for (int i = 0; i < 15; i++)
        {
            tab.FilteredItems.Add(new FileItem
            {
                Name = $"file_{i}.txt",
                FullPath = $@"C:\FakePath\file_{i}.txt",
                Extension = ".txt"
            });
        }

        pane.SetThumbnailView();
        pane.UpdateThumbnailViewportWidth(1000);

        // At 1000px viewport:
        // 140px size: 992 / (140 + 36) = 992 / 176 = 5 columns
        // 150px size: 992 / (150 + 36) = 992 / 186 = 5 columns
        pane.ThumbnailSize = 140;
        Assert.Equal(5, pane.ThumbnailColumnCount);
        Assert.Equal(3, pane.ThumbnailRows.Count);

        var initialRows = pane.ThumbnailRows;

        // Changing size within the same column count boundary
        pane.ThumbnailSize = 150;
        Assert.Equal(5, pane.ThumbnailColumnCount);

        // ThumbnailRows collection must NOT be reallocated or regrouped
        Assert.Same(initialRows, pane.ThumbnailRows);
    }

    [AvaloniaFact]
    public void ThumbnailSize_ColumnBoundaryRebuild_RegroupsCorrectlyAndPreservesAllItems()
    {
        // Choose sizes that cross a column boundary.
        // Assert rows are regrouped correctly and every source item appears exactly once, in the existing order.
        var pane = new ExplorerPaneViewModel("test", @"C:\FakePath");
        var tab = pane.SelectedTab!;
        for (int i = 0; i < 23; i++)
        {
            tab.FilteredItems.Add(new FileItem
            {
                Name = $"item_{i:D2}.txt",
                FullPath = $@"C:\FakePath\item_{i:D2}.txt",
                Extension = ".txt"
            });
        }

        pane.SetThumbnailView();
        pane.UpdateThumbnailViewportWidth(1000);

        // 140px size: 5 columns -> 23 items packed into 5 rows (5, 5, 5, 5, 3)
        pane.ThumbnailSize = 140;
        Assert.Equal(5, pane.ThumbnailColumnCount);
        Assert.Equal(5, pane.ThumbnailRows.Count);
        Assert.Equal(5, pane.ThumbnailRows[0].Items.Count);
        Assert.Equal(3, pane.ThumbnailRows[4].Items.Count);

        // Cross column boundary to 250px size (3 columns) -> 23 items packed into 8 rows (3*7 + 2)
        pane.ThumbnailSize = 250;
        Assert.Equal(3, pane.ThumbnailColumnCount);
        Assert.Equal(8, pane.ThumbnailRows.Count);
        for (int r = 0; r < 7; r++)
        {
            Assert.Equal(3, pane.ThumbnailRows[r].Items.Count);
        }
        Assert.Equal(2, pane.ThumbnailRows[7].Items.Count);

        // Verify every source item appears exactly once in the exact order
        var flattened = pane.ThumbnailRows.SelectMany(r => r.Items).ToList();
        Assert.Equal(23, flattened.Count);
        for (int i = 0; i < 23; i++)
        {
            Assert.Same(tab.FilteredItems[i], flattened[i]);
            Assert.Equal($"item_{i:D2}.txt", flattened[i].Name);
        }
    }
}
