using System;

namespace ClankerExplorer.Services;

public enum PointerSurface
{
    FileBackground,
    FileRow,
    Tab,
    InspectorSplitter,
    QuickAccessItem
}

public enum PointerInteraction
{
    MarqueeSelection,
    FileSelectionOrDrag,
    TabDrag,
    InspectorResize,
    QuickAccessReorder
}

public static class PointerGestureClassifier
{
    public static PointerInteraction ClassifyPress(PointerSurface surface) => surface switch
    {
        PointerSurface.FileBackground => PointerInteraction.MarqueeSelection,
        PointerSurface.FileRow => PointerInteraction.FileSelectionOrDrag,
        PointerSurface.Tab => PointerInteraction.TabDrag,
        PointerSurface.InspectorSplitter => PointerInteraction.InspectorResize,
        PointerSurface.QuickAccessItem => PointerInteraction.QuickAccessReorder,
        _ => throw new ArgumentOutOfRangeException(nameof(surface))
    };

    public static bool ExceedsDragThreshold(double deltaX, double deltaY, double threshold) =>
        Math.Abs(deltaX) > threshold || Math.Abs(deltaY) > threshold;
}
