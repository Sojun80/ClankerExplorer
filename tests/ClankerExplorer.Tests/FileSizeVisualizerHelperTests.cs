using System;
using Avalonia.Media;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using Xunit;

namespace ClankerExplorer.Tests;

public class FileSizeVisualizerHelperTests
{
    [Fact]
    public void CalculateFill_ZeroAndNegativeBytes_ReturnsZero()
    {
        Assert.Equal(0.0, FileSizeVisualizerHelper.CalculateFill(0, isDirectory: false));
        Assert.Equal(0.0, FileSizeVisualizerHelper.CalculateFill(-100, isDirectory: false));
    }

    [Fact]
    public void CalculateFill_Directory_ReturnsZero()
    {
        Assert.Equal(0.0, FileSizeVisualizerHelper.CalculateFill(1024 * 1024, isDirectory: true));
        Assert.Equal(0.0, FileSizeVisualizerHelper.CalculateFill(50L * 1024 * 1024 * 1024, isDirectory: true));
    }

    [Fact]
    public void CalculateFill_UnderOneKB_ReturnsMinimalVisibleSliver()
    {
        double fill500B = FileSizeVisualizerHelper.CalculateFill(500, isDirectory: false);
        Assert.True(fill500B > 0.0 && fill500B <= 0.02);
    }

    [Fact]
    public void CalculateFill_OneKB_ReturnsMinimumVisibleEmpty()
    {
        double fill1KB = FileSizeVisualizerHelper.CalculateFill(1024, isDirectory: false);
        Assert.True(fill1KB >= 0.01 && fill1KB <= 0.02);
    }

    [Fact]
    public void CalculateFill_OneMB_ReturnsAround39Percent()
    {
        double fill1MB = FileSizeVisualizerHelper.CalculateFill(1024 * 1024, isDirectory: false);
        Assert.InRange(fill1MB, 0.38, 0.40);
    }

    [Fact]
    public void CalculateFill_100MB_ReturnsAround65Percent()
    {
        double fill100MB = FileSizeVisualizerHelper.CalculateFill(100L * 1024 * 1024, isDirectory: false);
        Assert.InRange(fill100MB, 0.63, 0.67);
    }

    [Fact]
    public void CalculateFill_OneGB_ReturnsBetween75And85Percent()
    {
        double fill1GB = FileSizeVisualizerHelper.CalculateFill(1024L * 1024L * 1024L, isDirectory: false);
        // Requirement: "1 GB = roughly 75–85% full"
        Assert.InRange(fill1GB, 0.75, 0.85);
    }

    [Fact]
    public void CalculateFill_10GB_ReturnsAround91Percent()
    {
        double fill10GB = FileSizeVisualizerHelper.CalculateFill(10L * 1024L * 1024L * 1024L, isDirectory: false);
        Assert.InRange(fill10GB, 0.89, 0.93);
    }

    [Fact]
    public void CalculateFill_50GB_Returns100Percent()
    {
        double fill50GB = FileSizeVisualizerHelper.CalculateFill(50L * 1024L * 1024L * 1024L, isDirectory: false);
        Assert.Equal(1.0, fill50GB);
    }

    [Fact]
    public void CalculateFill_Above50GB_ClampsTo100Percent()
    {
        double fill100GB = FileSizeVisualizerHelper.CalculateFill(100L * 1024L * 1024L * 1024L, isDirectory: false);
        Assert.Equal(1.0, fill100GB);
    }

    [Fact]
    public void CalculateFill_PreservesUsefulDifferentiationBetweenLargeSizes()
    {
        double fill1GB = FileSizeVisualizerHelper.CalculateFill(1024L * 1024L * 1024L, isDirectory: false);
        double fill10GB = FileSizeVisualizerHelper.CalculateFill(10L * 1024L * 1024L * 1024L, isDirectory: false);
        double fill50GB = FileSizeVisualizerHelper.CalculateFill(50L * 1024L * 1024L * 1024L, isDirectory: false);

        Assert.True(fill1GB < fill10GB);
        Assert.True(fill10GB < fill50GB);
        Assert.True(fill10GB - fill1GB > 0.10, "1GB vs 10GB should have significant visual difference (> 10%)");
    }

    [Fact]
    public void FileItem_HasSizeBar_Behavior()
    {
        var zeroItem = new FileItem { Name = "empty.txt", SizeBytes = 0, IsDirectory = false };
        Assert.False(zeroItem.HasSizeBar);

        var dirItem = new FileItem { Name = "Documents", SizeBytes = 1024 * 1024, IsDirectory = true };
        Assert.False(dirItem.HasSizeBar);

        var smallItem = new FileItem { Name = "small.txt", SizeBytes = 1024, IsDirectory = false };
        Assert.True(smallItem.HasSizeBar);
        Assert.True(smallItem.SizeBarFillPercent > 0);

        var bigItem = new FileItem { Name = "movie.mkv", SizeBytes = 2L * 1024 * 1024 * 1024, IsDirectory = false };
        Assert.True(bigItem.HasSizeBar);
        Assert.InRange(bigItem.SizeBarFillPercent, 78.0, 85.0);
    }

    [Fact]
    public void GetBrush_ReturnsValidNonNullBrushes()
    {
        for (int i = 0; i <= 100; i++)
        {
            var brush = FileSizeVisualizerHelper.GetBrush(i / 100.0);
            Assert.NotNull(brush);
            Assert.IsType<SolidColorBrush>(brush);
        }
    }

    [Fact]
    public void ColorInterpolation_TransitionsFromTealToRed()
    {
        var smallColor = FileSizeVisualizerHelper.InterpolateColor(0.1);
        var medColor = FileSizeVisualizerHelper.InterpolateColor(0.5);
        var largeColor = FileSizeVisualizerHelper.InterpolateColor(0.75);
        var hugeColor = FileSizeVisualizerHelper.InterpolateColor(0.95);

        // Small files should have dominant Green/Teal
        Assert.True(smallColor.G > smallColor.R);

        // Medium files should have warm Green/Yellow
        Assert.True(medColor.G > 100);

        // Large files should have higher Red than small files
        Assert.True(largeColor.R > smallColor.R);

        // Huge files should have high Red (red/crimson)
        Assert.True(hugeColor.R > hugeColor.G);
    }
}
