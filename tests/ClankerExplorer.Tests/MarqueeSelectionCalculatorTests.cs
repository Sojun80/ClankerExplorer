using ClankerExplorer.Services;

namespace ClankerExplorer.Tests;

public sealed class MarqueeSelectionCalculatorTests
{
    [Fact]
    public void VisibleRowCalculation_AccountsForScrolledFirstItem()
    {
        var selected = MarqueeSelectionCalculator.CalculateFromVisibleRow(
            firstY: 33,
            secondY: 63,
            firstVisibleIndex: 5,
            firstRowTop: 32,
            rowHeight: 32,
            itemCount: 20);

        Assert.Equal(new[] { 5 }, selected.OrderBy(index => index));
    }

    [Fact]
    public void VisibleRowCalculation_DoesNotSelectAnOffscreenRowAboveTheList()
    {
        var selected = MarqueeSelectionCalculator.CalculateFromVisibleRow(
            firstY: 0,
            secondY: 20,
            firstVisibleIndex: 10,
            firstRowTop: 32,
            rowHeight: 32,
            itemCount: 20);

        Assert.Empty(selected);
    }

    [Fact]
    public void VisibleRowCalculation_DoesNotClampBlankSpaceBelowListToLastItem()
    {
        var selected = MarqueeSelectionCalculator.CalculateFromVisibleRow(
            firstY: 200,
            secondY: 240,
            firstVisibleIndex: 0,
            firstRowTop: 32,
            rowHeight: 32,
            itemCount: 3);

        Assert.Empty(selected);
    }

    [Fact]
    public void VisibleRowCalculation_PreservesCtrlSelectionWhenRangeMissesRows()
    {
        var selected = MarqueeSelectionCalculator.CalculateFromVisibleRow(
            firstY: 200,
            secondY: 240,
            firstVisibleIndex: 0,
            firstRowTop: 32,
            rowHeight: 32,
            itemCount: 3,
            baseSelection: new[] { 1 },
            preserveBaseSelection: true);

        Assert.Equal(new[] { 1 }, selected);
    }

    [Fact]
    public void BackgroundDrag_SelectsRowsIntersectingVerticalRange()
    {
        var selected = MarqueeSelectionCalculator.CalculateSelectedIndexes(
            firstY: 33,
            secondY: 95,
            scrollOffset: 0,
            headerHeight: 32,
            rowHeight: 32,
            itemCount: 10);

        Assert.Equal(new[] { 0, 1 }, selected.OrderBy(index => index));
    }

    [Fact]
    public void Scrolling_ShiftsMarqueeSelectionByVisibleRows()
    {
        var selected = MarqueeSelectionCalculator.CalculateSelectedIndexes(
            firstY: 33,
            secondY: 63,
            scrollOffset: 64,
            headerHeight: 32,
            rowHeight: 32,
            itemCount: 10);

        Assert.Equal(new[] { 2 }, selected.OrderBy(index => index));
    }

    [Fact]
    public void CtrlMarquee_PreservesBaseSelectionAndAddsRange()
    {
        var selected = MarqueeSelectionCalculator.CalculateSelectedIndexes(
            firstY: 65,
            secondY: 95,
            scrollOffset: 0,
            headerHeight: 32,
            rowHeight: 32,
            itemCount: 10,
            baseSelection: new[] { 0, 7 },
            preserveBaseSelection: true);

        Assert.Equal(new[] { 0, 1, 7 }, selected.OrderBy(index => index));
    }

    [Fact]
    public void Marquee_UsesConfiguredRowHeightInsteadOfAssumingLegacy28Pixels()
    {
        var selected = MarqueeSelectionCalculator.CalculateSelectedIndexes(
            firstY: 32,
            secondY: 103,
            scrollOffset: 0,
            headerHeight: 32,
            rowHeight: 36,
            itemCount: 10);

        Assert.Equal(new[] { 0, 1 }, selected.OrderBy(index => index));
    }
}
