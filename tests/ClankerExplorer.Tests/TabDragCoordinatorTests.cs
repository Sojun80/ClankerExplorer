using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;
using Avalonia;

namespace ClankerExplorer.Tests;

public sealed class TabDragCoordinatorTests : IDisposable
{
    private readonly TabDragCoordinator _coordinator = TabDragCoordinator.Instance;

    public TabDragCoordinatorTests() => _coordinator.CancelDrag();

    [Fact]
    public void SamePaneDrop_ReordersTabsAndKeepsDraggedTabActive()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        var first = pane.SelectedTab!;
        pane.AddNewTab(fs.FolderB);
        var second = pane.SelectedTab!;
        pane.AddNewTab(fs.FolderC);
        var third = pane.SelectedTab!;

        _coordinator.StartDrag(third, pane, isCtrl: false, new Point(0, 0));
        _coordinator.CompleteDrop(pane, 0, isCtrl: false);

        Assert.Equal(new[] { third, first, second }, pane.Tabs);
        Assert.Same(third, pane.SelectedTab);
        Assert.False(_coordinator.IsDragging);
    }

    [Fact]
    public void SamePaneDrop_MovingForwardUsesInsertionIndexWithoutOffByOne()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        var first = pane.SelectedTab!;
        pane.AddNewTab(fs.FolderB);
        var second = pane.SelectedTab!;
        pane.AddNewTab(fs.FolderC);
        var third = pane.SelectedTab!;

        _coordinator.StartDrag(first, pane, isCtrl: false, new Point(0, 0));
        _coordinator.CompleteDrop(pane, targetIndex: 3, isCtrl: false);

        Assert.Equal(new[] { second, third, first }, pane.Tabs);
        Assert.Same(first, pane.SelectedTab);
    }

    [Fact]
    public void CrossPaneDrop_BeyondLastIndexAppendsSafely()
    {
        using var fs = new TemporaryFileSystem();
        using var left = new ExplorerPaneViewModel("left", fs.FolderA);
        using var right = new ExplorerPaneViewModel("right", fs.FolderB);
        right.AddNewTab(fs.FolderC);
        var moved = left.SelectedTab!;

        _coordinator.StartDrag(moved, left, isCtrl: false, new Point(0, 0));
        _coordinator.CompleteDrop(right, targetIndex: int.MaxValue, isCtrl: false);

        Assert.Same(moved, right.Tabs[^1]);
        Assert.Same(moved, right.SelectedTab);
        Assert.Single(left.Tabs);
        Assert.NotSame(moved, left.SelectedTab);
    }

    [Fact]
    public void CrossPaneMove_PreservesHistoryAndLeavesValidSourceSelection()
    {
        using var fs = new TemporaryFileSystem();
        using var left = new ExplorerPaneViewModel("left", fs.FolderA);
        using var right = new ExplorerPaneViewModel("right", fs.FolderC);
        left.AddNewTab(fs.FolderB);
        var moved = left.SelectedTab!;
        moved.NavigateTo(fs.Nested);
        var expectedHistory = moved.History.ToArray();

        _coordinator.StartDrag(moved, left, isCtrl: false, new Point(0, 0));
        _coordinator.CompleteDrop(right, 0, isCtrl: false);

        Assert.DoesNotContain(moved, left.Tabs);
        Assert.Contains(moved, right.Tabs);
        Assert.Contains(left.SelectedTab, left.Tabs);
        Assert.Same(moved, right.SelectedTab);
        Assert.Equal(expectedHistory, moved.History);

        var sourceAddressAfterMove = left.RawAddressInput;
        moved.NavigateTo(fs.FolderC);
        Assert.Equal(sourceAddressAfterMove, left.RawAddressInput);
        Assert.Equal(Path.GetFullPath(fs.FolderC), right.RawAddressInput);
    }

    [Fact]
    public void CtrlDrop_DuplicatesTabWithoutRemovingOriginal()
    {
        using var fs = new TemporaryFileSystem();
        using var left = new ExplorerPaneViewModel("left", fs.FolderA);
        using var right = new ExplorerPaneViewModel("right", fs.FolderB);
        var original = left.SelectedTab!;
        original.NavigateTo(fs.FolderC);

        _coordinator.StartDrag(original, left, isCtrl: true, new Point(0, 0));
        _coordinator.CompleteDrop(right, 0, isCtrl: true);

        Assert.Contains(original, left.Tabs);
        var clone = right.SelectedTab!;
        Assert.NotSame(original, clone);
        Assert.Equal(original.CurrentPath, clone.CurrentPath);
        Assert.Equal(original.History, clone.History);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossPaneDrop_RespectsTargetPaneTabLimit(bool copy)
    {
        using var fs = new TemporaryFileSystem();
        SettingsService.Instance.SaveSettings(new ClankerExplorer.Models.AppSettings
        {
            DefaultPath = fs.FolderA,
            StartupBehavior = "OpenDefaultPath",
            MaxTabsAllowed = 1
        });
        using var left = new ExplorerPaneViewModel("left", fs.FolderA);
        using var right = new ExplorerPaneViewModel("right", fs.FolderB);
        var source = left.SelectedTab!;
        var existingTarget = right.SelectedTab!;

        _coordinator.StartDrag(source, left, isCtrl: copy, new Point(0, 0));
        _coordinator.CompleteDrop(right, 0, isCtrl: copy);

        Assert.Equal(new[] { source }, left.Tabs);
        Assert.Equal(new[] { existingTarget }, right.Tabs);
        Assert.Same(source, left.SelectedTab);
        Assert.Same(existingTarget, right.SelectedTab);
        Assert.False(_coordinator.IsDragging);
    }

    [Fact]
    public void CancelDrag_ClearsEveryInteractionIndicator()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA);
        pane.AddNewTab(fs.FolderB);
        var dragged = pane.Tabs[0];
        var hovered = pane.Tabs[1];

        _coordinator.StartDrag(dragged, pane, isCtrl: false, new Point(0, 0));
        _coordinator.UpdateDrag(pane, hovered, isLeftHalf: true, isCtrl: false, new Point(10, 10));
        _coordinator.CancelDrag();

        Assert.False(dragged.IsBeingDragged);
        Assert.False(hovered.IsDropTargetLeft);
        Assert.False(hovered.IsDropTargetRight);
        Assert.False(_coordinator.IsDragging);
    }

    public void Dispose()
    {
        _coordinator.CancelDrag();
        TestEnvironment.ResetGlobalSettings(TestEnvironment.DefaultFolder);
    }
}
