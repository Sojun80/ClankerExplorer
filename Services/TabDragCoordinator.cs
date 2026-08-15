using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Services;

public class TabDragCoordinator
{
    public static TabDragCoordinator Instance { get; } = new();

    public ExplorerTabViewModel? DraggedTab { get; private set; }
    public ExplorerPaneViewModel? SourcePane { get; private set; }
    public ExplorerPaneViewModel? CurrentTargetPane { get; private set; }
    public ExplorerTabViewModel? CurrentHoveredTab { get; private set; }
    public bool IsLeftDropHalf { get; private set; }
    public bool IsCtrlCopy { get; private set; }
    public bool IsDragging { get; private set; }

    public event Action? DragStateChanged;
    public event Action<ExplorerTabViewModel, Point, bool>? TabDragMoved;
    public event Action? TabDragEnded;

    public void StartDrag(ExplorerTabViewModel tab, ExplorerPaneViewModel sourcePane, bool isCtrl, Point initialPosition)
    {
        DraggedTab = tab;
        SourcePane = sourcePane;
        IsCtrlCopy = isCtrl;
        IsDragging = true;

        tab.IsBeingDragged = true;
        DragStateChanged?.Invoke();
        TabDragMoved?.Invoke(tab, initialPosition, isCtrl);
    }

    public void UpdateDrag(ExplorerPaneViewModel? targetPane, ExplorerTabViewModel? hoveredTab, bool isLeftHalf, bool isCtrl, Point position)
    {
        if (!IsDragging || DraggedTab == null) return;

        IsCtrlCopy = isCtrl;
        CurrentTargetPane = targetPane ?? SourcePane;

        // Clear previous tab indicators
        if (CurrentHoveredTab != null && CurrentHoveredTab != hoveredTab)
        {
            CurrentHoveredTab.IsDropTargetLeft = false;
            CurrentHoveredTab.IsDropTargetRight = false;
        }

        CurrentHoveredTab = hoveredTab;
        IsLeftDropHalf = isLeftHalf;

        if (hoveredTab != null && hoveredTab != DraggedTab)
        {
            hoveredTab.IsDropTargetLeft = isLeftHalf;
            hoveredTab.IsDropTargetRight = !isLeftHalf;
        }

        DragStateChanged?.Invoke();
        TabDragMoved?.Invoke(DraggedTab, position, isCtrl);
    }

    public void CompleteDrop(ExplorerPaneViewModel? targetPane, int targetIndex, bool isCtrl)
    {
        if (!IsDragging || DraggedTab == null || SourcePane == null)
        {
            CancelDrag();
            return;
        }

        targetPane ??= CurrentTargetPane ?? SourcePane;

        try
        {
            if (isCtrl)
            {
                // Ctrl+Drag: Duplicate/Copy Tab to target position
                var cloned = DraggedTab.CloneTab();
                if (targetIndex < 0 || targetIndex >= targetPane.Tabs.Count)
                {
                    targetPane.Tabs.Add(cloned);
                }
                else
                {
                    targetPane.Tabs.Insert(targetIndex, cloned);
                }
                targetPane.WireTabEvents(cloned);
                targetPane.SelectedTab = cloned;
            }
            else
            {
                // Move Tab
                if (SourcePane == targetPane)
                {
                    // Same pane reorder
                    int oldIndex = targetPane.Tabs.IndexOf(DraggedTab);
                    if (oldIndex >= 0 && targetIndex >= 0 && targetIndex < targetPane.Tabs.Count && oldIndex != targetIndex)
                    {
                        targetPane.Tabs.Move(oldIndex, targetIndex);
                    }
                    targetPane.SelectedTab = DraggedTab;
                }
                else
                {
                    // Cross-pane transfer
                    // If source pane would become empty, create a fallback tab first
                    if (SourcePane.Tabs.Count <= 1)
                    {
                        var fallback = new ExplorerTabViewModel(SourcePane.SelectedTab?.CurrentPath ?? FileSystemService.DefaultRootPath);
                        SourcePane.Tabs.Add(fallback);
                        SourcePane.WireTabEvents(fallback);
                        SourcePane.SelectedTab = fallback;
                    }

                    int sourceIndex = SourcePane.Tabs.IndexOf(DraggedTab);
                    bool movedTabWasSelected = SourcePane.SelectedTab == DraggedTab;
                    SourcePane.Tabs.Remove(DraggedTab);
                    SourcePane.UnwireTabEvents(DraggedTab);

                    if (movedTabWasSelected && SourcePane.Tabs.Count > 0)
                    {
                        SourcePane.SelectedTab = SourcePane.Tabs[Math.Min(sourceIndex, SourcePane.Tabs.Count - 1)];
                    }

                    if (targetIndex < 0 || targetIndex >= targetPane.Tabs.Count)
                    {
                        targetPane.Tabs.Add(DraggedTab);
                    }
                    else
                    {
                        targetPane.Tabs.Insert(targetIndex, DraggedTab);
                    }

                    targetPane.WireTabEvents(DraggedTab);
                    targetPane.SelectedTab = DraggedTab;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Drop failed: {ex.Message}");
        }
        finally
        {
            CancelDrag();
        }
    }

    public void CancelDrag()
    {
        if (DraggedTab != null)
        {
            DraggedTab.IsBeingDragged = false;
        }

        if (CurrentHoveredTab != null)
        {
            CurrentHoveredTab.IsDropTargetLeft = false;
            CurrentHoveredTab.IsDropTargetRight = false;
        }

        // Clear all tabs indicators in both panes if needed
        if (SourcePane != null)
        {
            foreach (var t in SourcePane.Tabs)
            {
                t.IsBeingDragged = false;
                t.IsDropTargetLeft = false;
                t.IsDropTargetRight = false;
            }
        }

        if (CurrentTargetPane != null)
        {
            foreach (var t in CurrentTargetPane.Tabs)
            {
                t.IsBeingDragged = false;
                t.IsDropTargetLeft = false;
                t.IsDropTargetRight = false;
            }
        }

        DraggedTab = null;
        SourcePane = null;
        CurrentTargetPane = null;
        CurrentHoveredTab = null;
        IsDragging = false;
        IsCtrlCopy = false;

        DragStateChanged?.Invoke();
        TabDragEnded?.Invoke();
    }
}
