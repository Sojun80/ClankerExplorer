using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

// File drag/drop payloads, targets, and feedback.
public partial class ExplorerPaneView
{
    private Point _dragStartPoint;
    private FileItem? _dragCandidateItem;
    private bool _isDragActive;
    private bool _dragOccurredForCurrentPress;
    private FileItem? _hoveredDragTarget;

    private async void StartDragAsync(PointerEventArgs triggerEvent, FileItem triggerItem)
    {
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
        var tab = vm.SelectedTab;

        try
        {
            List<string> dragPaths;
            bool isAlreadySelected = (vm.IsThumbnailView && triggerItem.IsThumbnailSelected) ||
                                     (!vm.IsThumbnailView && tab.SelectedItems.Contains(triggerItem));

            if (isAlreadySelected)
            {
                dragPaths = tab.SelectedItems
                    .Where(i => !string.IsNullOrEmpty(i.FullPath))
                    .Select(i => i.FullPath)
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                if (vm.IsThumbnailView)
                {
                    tab.SelectThumbnailItem(triggerItem, control: false, shift: false);
                }
                else
                {
                    tab.SelectedItems.Clear();
                    tab.SelectedItems.Add(triggerItem);
                    tab.SelectedItem = triggerItem;
                }
                dragPaths = new List<string> { triggerItem.FullPath };
            }

            if (dragPaths.Count == 0) return;

            var dataObject = new DataObject();
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            var storageItems = FileDragDropService.ResolveStorageItems(storageProvider, dragPaths);

            if (storageItems.Count > 0)
            {
                dataObject.Set(DataFormats.Files, storageItems);
            }
            dataObject.Set(DataFormats.FileNames, dragPaths);
            dataObject.Set(DataFormats.Text, string.Join(Environment.NewLine, dragPaths));

            await DragDrop.DoDragDrop(triggerEvent, dataObject, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Drag failed: {ex}");
            if (DataContext is ExplorerPaneViewModel paneVm && paneVm.SelectedTab != null)
            {
                paneVm.SelectedTab.StatusMessage = "Drag operation failed.";
            }
        }
        finally
        {
            _dragCandidateItem = null;
            _isDragActive = false;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDragOverState(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        UpdateDragOverState(e);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        ClearDragOverHighlight();
    }

    private void ClearDragOverHighlight()
    {
        if (_hoveredDragTarget != null)
        {
            _hoveredDragTarget.IsDragOver = false;
            _hoveredDragTarget = null;
        }
    }

    private void UpdateDragOverState(DragEventArgs e)
    {
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDragOverHighlight();
            return;
        }

        var sourcePaths = FileDragDropService.ExtractPaths(e.Data);
        if (sourcePaths.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDragOverHighlight();
            return;
        }

        // Find folder under cursor if any
        FileItem? targetFolder = null;
        if (e.Source is Visual visual)
        {
            var element = visual;
            while (element != null && element != this)
            {
                if (element is Control { DataContext: FileItem item } && item.IsDirectory)
                {
                    targetFolder = item;
                    break;
                }
                element = element.GetVisualParent();
            }
        }

        if (targetFolder != _hoveredDragTarget)
        {
            ClearDragOverHighlight();
            if (targetFolder != null)
            {
                _hoveredDragTarget = targetFolder;
                _hoveredDragTarget.IsDragOver = true;
            }
        }

        string destDir = targetFolder?.FullPath ?? vm.SelectedTab.CurrentPath;
        e.DragEffects = FileDragDropService.ResolveEffect(sourcePaths, destDir, e.KeyModifiers);
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var targetFolder = _hoveredDragTarget;
        ClearDragOverHighlight();

        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;

        try
        {
            var sourcePaths = FileDragDropService.ExtractPaths(e.Data);
            if (sourcePaths.Count == 0) return;

            string destDir = targetFolder?.FullPath ?? vm.SelectedTab.CurrentPath;
            var effect = FileDragDropService.ResolveEffect(sourcePaths, destDir, e.KeyModifiers);
            if (effect == DragDropEffects.None) return;

            bool isMove = effect.HasFlag(DragDropEffects.Move);
            e.Handled = true;

            await vm.ExecuteDropAsync(sourcePaths, destDir, isMove);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Drop failed: {ex}");
            if (vm.SelectedTab != null)
            {
                vm.SelectedTab.StatusMessage = "Drop operation failed.";
            }
        }
    }
}
