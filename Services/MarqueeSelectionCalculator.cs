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

        if (itemCount <= 0 || rowHeight <= 0 || double.IsNaN(rowHeight) || firstVisibleIndex < 0) return selected;

        double minY = Math.Min(firstY, secondY);
        double maxY = Math.Max(firstY, secondY);

        int startIndex = firstVisibleIndex + (int)Math.Floor((minY - firstRowTop) / rowHeight);
        int endIndex = firstVisibleIndex + (int)Math.Floor((maxY - firstRowTop) / rowHeight);

        startIndex = Math.Clamp(startIndex, 0, itemCount - 1);
        endIndex = Math.Clamp(endIndex, 0, itemCount - 1);

        if (startIndex > endIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
        }

        for (int index = startIndex; index <= endIndex; index++)
        {
            selected.Add(index);
        }

        selected.RemoveWhere(index => index < 0 || index >= itemCount);
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
