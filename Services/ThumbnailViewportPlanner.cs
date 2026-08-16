using System;

namespace ClankerExplorer.Services;

public readonly record struct ThumbnailViewportWindow(
    int VisibleStart,
    int VisibleEnd,
    int RetainedStart,
    int RetainedEnd);

public static class ThumbnailViewportPlanner
{
    public static ThumbnailViewportWindow Plan(
        int itemCount,
        int columnCount,
        int firstVisibleRow,
        int lastVisibleRow,
        double prefetchViewports)
    {
        int columns = Math.Max(1, columnCount);
        int visibleStart = Math.Clamp(firstVisibleRow * columns, 0, itemCount);
        int visibleEnd = Math.Clamp((lastVisibleRow + 1) * columns, visibleStart, itemCount);
        int visibleCount = Math.Max(columns, visibleEnd - visibleStart);
        int prefetchCount = Math.Max(columns,
            (int)Math.Ceiling(visibleCount * Math.Clamp(prefetchViewports, 1.0, 2.0)));
        return new ThumbnailViewportWindow(
            visibleStart,
            visibleEnd,
            Math.Max(0, visibleStart - prefetchCount),
            Math.Min(itemCount, visibleEnd + prefetchCount));
    }
}
