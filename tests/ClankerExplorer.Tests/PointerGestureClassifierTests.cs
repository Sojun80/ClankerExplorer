using ClankerExplorer.Services;

namespace ClankerExplorer.Tests;

public sealed class PointerGestureClassifierTests
{
    [Theory]
    [InlineData(PointerSurface.FileBackground, PointerInteraction.MarqueeSelection)]
    [InlineData(PointerSurface.FileRow, PointerInteraction.FileSelectionOrDrag)]
    [InlineData(PointerSurface.Tab, PointerInteraction.TabDrag)]
    [InlineData(PointerSurface.InspectorSplitter, PointerInteraction.InspectorResize)]
    [InlineData(PointerSurface.QuickAccessItem, PointerInteraction.QuickAccessReorder)]
    public void PressSurface_HasExactlyOneInteractionOwner(
        PointerSurface surface,
        PointerInteraction expected)
    {
        Assert.Equal(expected, PointerGestureClassifier.ClassifyPress(surface));
    }

    [Fact]
    public void SmallPointerJitter_DoesNotStartAnyDragInteraction()
    {
        Assert.False(PointerGestureClassifier.ExceedsDragThreshold(3, 4, 6));
        Assert.False(PointerGestureClassifier.ExceedsDragThreshold(-6, 0, 6));
    }

    [Fact]
    public void MovementPastThreshold_StartsTheOwnedInteraction()
    {
        Assert.True(PointerGestureClassifier.ExceedsDragThreshold(7, 0, 6));
        Assert.True(PointerGestureClassifier.ExceedsDragThreshold(0, -7, 6));
    }
}
