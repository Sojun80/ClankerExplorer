using System;
using ClankerExplorer.Behaviors;
using ClankerExplorer.Platform;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class ScrollBehaviorAndSettingsTests
{
    [Fact]
    public void DefaultScrollSettingsProvider_ReturnsThreeLinesNotPageScroll()
    {
        var provider = DefaultScrollSettingsProvider.Instance;
        var settings = provider.GetMouseWheelSettings();

        Assert.Equal(3, settings.ScrollLines);
        Assert.False(settings.IsPageScroll);
    }

    [Fact]
    public void WindowsScrollSettingsProvider_ReturnsValidConfigurationWithoutThrowing()
    {
        var provider = WindowsScrollSettingsProvider.Instance;
        var settings = provider.GetMouseWheelSettings();

        if (OperatingSystem.IsWindows())
        {
            if (settings.IsPageScroll)
            {
                Assert.Equal(0, settings.ScrollLines);
            }
            else
            {
                Assert.InRange(settings.ScrollLines, 0, 100);
            }
        }
        else
        {
            Assert.Equal(3, settings.ScrollLines);
            Assert.False(settings.IsPageScroll);
        }
    }

    [Fact]
    public void DetailsScroll_ThreeLines_CalculatesApproximateThreeRowsMovement()
    {
        var settings = new MouseWheelSettings(ScrollLines: 3, IsPageScroll: false);
        double rowHeight = 26.0;
        double viewport = 600.0;

        // Scroll 1 notch down (delta -1)
        double downDistance = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Details, settings, deltaY: -1.0, rowHeight, viewport);
        Assert.Equal(-78.0, downDistance);

        // Scroll 1 notch up (delta +1)
        double upDistance = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Details, settings, deltaY: 1.0, rowHeight, viewport);
        Assert.Equal(78.0, upDistance);
    }

    [Fact]
    public void DetailsScroll_CustomLinesAndZeroLines_Respected()
    {
        double rowHeight = 28.0;
        double viewport = 500.0;

        // 6 lines
        var sixLines = new MouseWheelSettings(ScrollLines: 6, IsPageScroll: false);
        double distance6 = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Details, sixLines, deltaY: 1.0, rowHeight, viewport);
        Assert.Equal(168.0, distance6);

        // 0 lines (scrolling disabled)
        var zeroLines = new MouseWheelSettings(ScrollLines: 0, IsPageScroll: false);
        double distance0 = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Details, zeroLines, deltaY: 1.0, rowHeight, viewport);
        Assert.Equal(0.0, distance0);
    }

    [Fact]
    public void DetailsScroll_PageScroll_MovesByViewportHeight()
    {
        var pageSettings = new MouseWheelSettings(ScrollLines: 0, IsPageScroll: true);
        double rowHeight = 26.0;
        double viewport = 720.0;

        double distance = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Details, pageSettings, deltaY: -1.0, rowHeight, viewport);
        Assert.Equal(-720.0, distance);
    }

    [Fact]
    public void DetailsScroll_PrecisionTouchpad_SmoothFractionalMovement()
    {
        var settings = new MouseWheelSettings(ScrollLines: 3, IsPageScroll: false);
        double rowHeight = 26.0;
        double viewport = 600.0;

        // Precision touchpad micro-step (delta 0.15)
        double microDistance = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Details, settings, deltaY: 0.15, rowHeight, viewport);
        Assert.Equal(11.7, microDistance, precision: 3);
    }

    [Fact]
    public void ThumbnailScroll_DefaultThreeLines_MovesOneRowOfTiles()
    {
        var settings = new MouseWheelSettings(ScrollLines: 3, IsPageScroll: false);
        double rowHeight = 214.0; // 160 + 54
        double viewport = 800.0;

        double distance = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Thumbnails, settings, deltaY: -1.0, rowHeight, viewport);

        // 1 notch moves exactly 1 row (214px)
        Assert.Equal(-214.0, distance);
    }

    [Fact]
    public void ThumbnailScroll_SixLines_MovesTwoRowsOfTiles()
    {
        var settings = new MouseWheelSettings(ScrollLines: 6, IsPageScroll: false);
        double rowHeight = 200.0;
        double viewport = 900.0;

        double distance = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Thumbnails, settings, deltaY: 1.0, rowHeight, viewport);

        // 6 lines is (6/3) = 2x lines ratio -> 2 * 200 = 400px
        Assert.Equal(400.0, distance);
    }

    [Fact]
    public void ThumbnailScroll_LargeThumbnails_DoesNotJumpAbsurdDistances()
    {
        // 10 lines configured in Windows with huge thumbnails (374px) on a smaller viewport (400px)
        var settings = new MouseWheelSettings(ScrollLines: 10, IsPageScroll: false);
        double rowHeight = 374.0;
        double viewport = 400.0;

        double distance = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Thumbnails, settings, deltaY: 1.0, rowHeight, viewport);

        // Max jump is capped at Math.Max(rowHeight, viewport * 0.85) = 374.0
        // Raw uncapped would have been (10/3.0) * 374 = 1246px!
        Assert.Equal(374.0, distance);
    }

    [Fact]
    public void ThumbnailScroll_PageScroll_MovesByViewportHeight()
    {
        var pageSettings = new MouseWheelSettings(ScrollLines: 0, IsPageScroll: true);
        double rowHeight = 214.0;
        double viewport = 750.0;

        double distance = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Thumbnails, pageSettings, deltaY: -1.0, rowHeight, viewport);
        Assert.Equal(-750.0, distance);
    }

    [Fact]
    public void ThumbnailScroll_PrecisionTouchpad_SmoothFractionalMovement()
    {
        var settings = new MouseWheelSettings(ScrollLines: 3, IsPageScroll: false);
        double rowHeight = 214.0;
        double viewport = 800.0;

        double distance = ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Thumbnails, settings, deltaY: 0.25, rowHeight, viewport);
        Assert.Equal(53.5, distance, precision: 3);
    }

    [Fact]
    public void ZeroDelta_ReturnsZeroDistance()
    {
        var settings = new MouseWheelSettings(ScrollLines: 3, IsPageScroll: false);
        Assert.Equal(0, ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Details, settings, deltaY: 0, 26.0, 600.0));
        Assert.Equal(0, ExplorerScrollBehavior.CalculateScrollDistance(
            ExplorerScrollMode.Thumbnails, settings, deltaY: 0, 214.0, 600.0));
    }
}
