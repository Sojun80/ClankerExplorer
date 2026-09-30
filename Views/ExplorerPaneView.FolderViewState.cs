using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClankerExplorer.Models;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

// Folder scroll positions, viewport anchors, and layout restoration.
public partial class ExplorerPaneView
{
    private readonly DispatcherTimer _folderScrollSaveTimer;
    private ScrollViewer? _detailsScrollViewer;
    private ScrollViewer? _thumbnailScrollViewer;
    private bool _restoringFolderViewState;
    private bool _preservingThumbnailLayout;
    private long _thumbnailResizeGeneration;
    private bool _detailsScrollSubscribed;
    private bool _thumbnailScrollSubscribed;

    private void InitializeFolderViewRestoration()
    {
        if (DataContext is not ExplorerPaneViewModel vm) return;
        EnsureFolderScrollViewers();
        vm.FolderViewStateRestored += RestoreFolderViewState;
        vm.RequestScrollItemIntoView += OnRequestScrollItemIntoView;
        vm.RequestSyncSelection += OnRequestSyncSelection;
        vm.RequestThumbnailViewportUpdate += ScheduleThumbnailViewportUpdate;
        vm.BeforeThumbnailLayoutChanging += OnBeforeThumbnailLayoutChanging;
        vm.AfterThumbnailLayoutChanged += OnAfterThumbnailLayoutChanged;
        RestoreFolderViewState();
    }

    private void OnBeforeThumbnailLayoutChanging()
    {
        if (!_preservingThumbnailLayout)
        {
            CaptureFolderViewportAnchors();
            _preservingThumbnailLayout = true;
        }
        _thumbnailResizeGeneration++;
    }

    private void OnAfterThumbnailLayoutChanged()
    {
        long currentGen = _thumbnailResizeGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (currentGen != _thumbnailResizeGeneration) return;
            try
            {
                if (DataContext is ExplorerPaneViewModel vm && vm.IsThumbnailView)
                {
                    RestoreViewportAnchors(vm);
                }
            }
            finally
            {
                if (currentGen == _thumbnailResizeGeneration)
                {
                    _preservingThumbnailLayout = false;
                    if (DataContext is ExplorerPaneViewModel settledVm && settledVm.IsThumbnailView)
                    {
                        SaveFolderScrollState(persist: false);
                    }
                }
            }
        }, DispatcherPriority.Loaded);
    }

    private void EnsureFolderScrollViewers()
    {
        _detailsScrollViewer ??= FileDataGrid?.FindDescendantOfType<ScrollViewer>();
        if (_detailsScrollViewer != null && !_detailsScrollSubscribed)
        {
            _detailsScrollSubscribed = true;
            _detailsScrollViewer.ScrollChanged += (_, _) => OnFolderScrollChanged();
        }

        _thumbnailScrollViewer ??= ThumbnailListBox?.FindDescendantOfType<ScrollViewer>();
        if (_thumbnailScrollViewer != null && !_thumbnailScrollSubscribed)
        {
            _thumbnailScrollSubscribed = true;
            _thumbnailScrollViewer.ScrollChanged += (_, _) =>
            {
                ScheduleThumbnailViewportUpdate();
                OnFolderScrollChanged();
            };
        }
    }

    private void OnFolderScrollChanged()
    {
        if (_restoringFolderViewState || _preservingThumbnailLayout) return;
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;

        // If the ScrollViewer is collapsing during a collection reload or transient layout update,
        // do not overwrite the valid non-zero scroll position or anchor with 0.
        if (!vm.IsThumbnailView && _detailsScrollViewer != null)
        {
            bool isCollapsing = _detailsScrollViewer.Extent.Height <= _detailsScrollViewer.Viewport.Height;
            if (isCollapsing && _detailsScrollViewer.Offset.Y == 0 && vm.DetailsVerticalOffset > 0 && vm.SelectedTab.FilteredItems.Count > 0)
            {
                return;
            }
        }
        else if (vm.IsThumbnailView && _thumbnailScrollViewer != null)
        {
            bool isCollapsing = _thumbnailScrollViewer.Extent.Height <= _thumbnailScrollViewer.Viewport.Height;
            if (isCollapsing && _thumbnailScrollViewer.Offset.Y == 0 && vm.ThumbnailVerticalOffset > 0 && vm.SelectedTab.FilteredItems.Count > 0)
            {
                return;
            }
        }

        SaveFolderScrollState(persist: false);
        _folderScrollSaveTimer.Stop();
        _folderScrollSaveTimer.Start();
    }

    private void SaveFolderScrollState(bool persist)
    {
        if (DataContext is not ExplorerPaneViewModel vm) return;
        CaptureFolderViewportAnchors();
        vm.UpdateFolderScrollState(
            _detailsScrollViewer?.Offset.X ?? vm.DetailsHorizontalOffset,
            _detailsScrollViewer?.Offset.Y ?? vm.DetailsVerticalOffset,
            _thumbnailScrollViewer?.Offset.Y ?? vm.ThumbnailVerticalOffset,
            persist);
    }

    private void CaptureFolderViewportAnchors()
    {
        if (_restoringFolderViewState || DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
        EnsureFolderScrollViewers();
        string? detailsPath = FileDataGrid?.GetVisualDescendants()
            .OfType<DataGridRow>()
            .Select(row => new { Row = row, Point = row.TranslatePoint(new Point(0, 0), FileDataGrid) })
            .Where(entry => entry.Point.HasValue && entry.Point.Value.Y + entry.Row.Bounds.Height >= 0)
            .OrderBy(entry => entry.Point!.Value.Y)
            .Select(entry => (entry.Row.DataContext as FileItem)?.FullPath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        string? thumbnailPath = null;
        var panel = ThumbnailListBox?.FindDescendantOfType<VirtualizingStackPanel>();
        int targetRowIndex = -1;
        if (panel != null && panel.FirstRealizedIndex >= 0)
        {
            targetRowIndex = panel.FirstRealizedIndex;
        }
        else if (_thumbnailScrollViewer != null && _thumbnailScrollViewer.Offset.Y > 0)
        {
            double rowHeight = vm.ThumbnailCellHeight + 8;
            if (rowHeight > 0)
            {
                targetRowIndex = (int)Math.Floor(_thumbnailScrollViewer.Offset.Y / rowHeight);
            }
        }

        if (targetRowIndex >= 0)
        {
            int itemIndex = targetRowIndex * Math.Max(1, vm.ThumbnailColumnCount);
            if (itemIndex < vm.SelectedTab.FilteredItems.Count)
                thumbnailPath = vm.SelectedTab.FilteredItems[itemIndex].FullPath;
        }

        vm.UpdateFolderViewportAnchors(
            detailsPath ?? vm.DetailsTopItemPath,
            thumbnailPath ?? vm.ThumbnailTopItemPath);
    }

    private void RestoreFolderViewState()
    {
        if (DataContext is not ExplorerPaneViewModel vm) return;
        _restoringFolderViewState = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                EnsureFolderScrollViewers();
                RestoreColumnOrder(vm.CurrentColumnOrder);
                bool anchored = RestoreViewportAnchors(vm);
                if (!anchored)
                {
                    if (_detailsScrollViewer != null && vm.DetailsVerticalOffset > 0)
                        _detailsScrollViewer.Offset = new Vector(vm.DetailsHorizontalOffset, vm.DetailsVerticalOffset);
                    if (_thumbnailScrollViewer != null && vm.ThumbnailVerticalOffset > 0)
                        _thumbnailScrollViewer.Offset = new Vector(0, vm.ThumbnailVerticalOffset);
                }
                else
                {
                    if (_detailsScrollViewer != null && vm.DetailsHorizontalOffset > 0)
                        _detailsScrollViewer.Offset = new Vector(vm.DetailsHorizontalOffset, _detailsScrollViewer.Offset.Y);
                }
            }
            finally
            {
                _restoringFolderViewState = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private bool RestoreViewportAnchors(ExplorerPaneViewModel vm)
    {
        var tab = vm.SelectedTab;
        if (tab == null) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!vm.IsThumbnailView && !string.IsNullOrWhiteSpace(vm.DetailsTopItemPath))
        {
            var item = tab.FilteredItems.FirstOrDefault(candidate =>
                string.Equals(candidate.FullPath, vm.DetailsTopItemPath, comparison));
            if (item != null && FileDataGrid != null)
            {
                FileDataGrid.ScrollIntoView(item, null);
                return true;
            }
        }

        if (vm.IsThumbnailView && !string.IsNullOrWhiteSpace(vm.ThumbnailTopItemPath) && ThumbnailListBox != null)
        {
            int index = -1;
            for (int candidateIndex = 0; candidateIndex < tab.FilteredItems.Count; candidateIndex++)
            {
                if (string.Equals(tab.FilteredItems[candidateIndex].FullPath, vm.ThumbnailTopItemPath, comparison))
                {
                    index = candidateIndex;
                    break;
                }
            }
            if (index >= 0)
            {
                int rowIndex = index / Math.Max(1, vm.ThumbnailColumnCount);
                if (rowIndex >= 0 && rowIndex < vm.ThumbnailRows.Count)
                {
                    ThumbnailListBox.ScrollIntoView(vm.ThumbnailRows[rowIndex]);
                    return true;
                }
            }
        }

        return false;
    }

    private void OnRequestScrollItemIntoView(FileItem item)
    {
        // Dispatch on Loaded priority so the DataGrid/ListBox has finished laying out the new items
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
            var tab = vm.SelectedTab;

            // Details view: scroll the DataGrid and sync selection
            if (FileDataGrid != null && !vm.IsThumbnailView)
            {
                if (tab.SelectedItems.Count > 0)
                {
                    FileDataGrid.SelectedItems.Clear();
                    foreach (var selected in tab.SelectedItems)
                    {
                        FileDataGrid.SelectedItems.Add(selected);
                    }
                }
                else if (tab.SelectedItem != null)
                {
                    FileDataGrid.SelectedItems.Clear();
                    FileDataGrid.SelectedItems.Add(tab.SelectedItem);
                }

                FileDataGrid.ScrollIntoView(item, null);
            }

            // Thumbnail view: scroll the ListBox row containing this item
            if (ThumbnailListBox != null && vm.IsThumbnailView)
            {
                int index = tab.FilteredItems.IndexOf(item);
                int colCount = Math.Max(1, vm.ThumbnailColumnCount);
                int rowIndex = index < 0 ? -1 : index / colCount;
                if (rowIndex >= 0 && rowIndex < vm.ThumbnailRows.Count)
                {
                    ThumbnailListBox.ScrollIntoView(vm.ThumbnailRows[rowIndex]);
                }
            }
        }, DispatcherPriority.Loaded);
    }
}
