using System;
using System.Collections.Generic;

namespace ClankerExplorer.Services;

public static class MarqueeSelectionCalculator
{
    public static HashSet<int> CalculateFromVisibleRow(
        double firstY,
        double secondY,
        int firstVisibleIndex,
        double firstRowTop,
        double rowHeight,
        int itemCount,
        IEnumerable<int>? baseSelection = null,
        bool preserveBaseSelection = false)
    {
        var selected = preserveBaseSelection && baseSelection != null
            ? new HashSet<int>(baseSelection)
            : new HashSet<int>();

        selected.RemoveWhere(index => index < 0 || index >= itemCount);
        if (itemCount <= 0 ||
            rowHeight <= 0 ||
            !double.IsFinite(rowHeight) ||
            !double.IsFinite(firstY) ||
            !double.IsFinite(secondY) ||
            !double.IsFinite(firstRowTop) ||
            firstVisibleIndex < 0 ||
            firstVisibleIndex >= itemCount)
        {
            return selected;
        }

        double minY = Math.Min(firstY, secondY);
        double maxY = Math.Max(firstY, secondY);
        double lastRowBottom = firstRowTop + ((itemCount - firstVisibleIndex) * rowHeight);

        // A marquee entirely outside the item rows should select nothing new.
        // Clamping before this check incorrectly selected an offscreen edge row.
        if (maxY < firstRowTop || minY > lastRowBottom)
        {
            return selected;
        }

        int startIndex = firstVisibleIndex + (int)Math.Floor((minY - firstRowTop) / rowHeight);
        int endIndex = firstVisibleIndex + (int)Math.Floor((maxY - firstRowTop) / rowHeight);

        startIndex = Math.Clamp(startIndex, firstVisibleIndex, itemCount - 1);
        endIndex = Math.Clamp(endIndex, firstVisibleIndex, itemCount - 1);

        if (startIndex > endIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
        }

        for (int index = startIndex; index <= endIndex; index++)
        {
            selected.Add(index);
        }

        return selected;
    }

    public static HashSet<int> CalculateSelectedIndexes(
        double firstY,
        double secondY,
        double scrollOffset,
        double headerHeight,
        double rowHeight,
        int itemCount,
        IEnumerable<int>? baseSelection = null,
        bool preserveBaseSelection = false)
    {
        var selected = preserveBaseSelection && baseSelection != null
            ? new HashSet<int>(baseSelection)
            : new HashSet<int>();

        if (itemCount <= 0 || rowHeight <= 0 || double.IsNaN(rowHeight)) return selected;

        double minY = Math.Min(firstY, secondY);
        double maxY = Math.Max(firstY, secondY);
        int startIndex = Math.Max(0, (int)Math.Floor((minY - headerHeight + scrollOffset) / rowHeight));
        int endIndex = Math.Min(itemCount - 1, (int)Math.Floor((maxY - headerHeight + scrollOffset) / rowHeight));

        if (startIndex <= endIndex && endIndex >= 0)
        {
            for (int index = startIndex; index <= endIndex; index++)
            {
                selected.Add(index);
            }
        }

        selected.RemoveWhere(index => index < 0 || index >= itemCount);
        return selected;
    }
}
