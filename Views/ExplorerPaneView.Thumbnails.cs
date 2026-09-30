using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

// Visible thumbnail loading, prefetching, and cancellation.
public partial class ExplorerPaneView
{
    private readonly DispatcherTimer _thumbnailDebounceTimer;
    private CancellationTokenSource? _thumbnailViewportCts;
    private HashSet<FileItem> _retainedThumbnailItems = new();
    private bool _thumbnailViewportInitialized;

    private void InitializeThumbnailViewport()
    {
        if (_thumbnailViewportInitialized || ThumbnailListBox == null) return;
        _thumbnailViewportInitialized = true;

        ThumbnailListBox.SizeChanged += (_, _) =>
        {
            if (DataContext is ExplorerPaneViewModel vm)
            {
                vm.UpdateThumbnailViewportWidth(ThumbnailListBox.Bounds.Width);
                ScheduleThumbnailViewportUpdate();
            }
        };

        _thumbnailScrollViewer = ThumbnailListBox.FindDescendantOfType<ScrollViewer>();
        if (_thumbnailScrollViewer != null)
        {
            _thumbnailScrollSubscribed = true;
            _thumbnailScrollViewer.ScrollChanged += (_, _) =>
            {
                ScheduleThumbnailViewportUpdate();
                OnFolderScrollChanged();
            };
        }

        if (DataContext is ExplorerPaneViewModel pane)
        {
            pane.UpdateThumbnailViewportWidth(ThumbnailListBox.Bounds.Width);
            pane.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ExplorerPaneViewModel.ThumbnailRows)
                    or nameof(ExplorerPaneViewModel.IsThumbnailView)
                    or nameof(ExplorerPaneViewModel.ThumbnailSize)
                    or nameof(ExplorerPaneViewModel.SelectedTab))
                {
                    _thumbnailViewportCts?.Cancel();
                    ThumbnailService.Instance.CancelPendingRequests();
                    _retainedThumbnailItems.Clear();
                    Dispatcher.UIThread.Post(ScheduleThumbnailViewportUpdate, DispatcherPriority.Loaded);
                }
            };
        }

        Dispatcher.UIThread.Post(ScheduleThumbnailViewportUpdate, DispatcherPriority.Loaded);
    }

    private void ScheduleThumbnailViewportUpdate()
    {
        _thumbnailViewportCts?.Cancel();
        if (DataContext is not ExplorerPaneViewModel vm || !vm.IsThumbnailView) return;

        ThumbnailService.Instance.NotifyScrollActivity();

        // Fast-path while scrolling: immediately assign any realized visible items already in memory cache
        if (ThumbnailListBox != null && vm.SelectedTab != null)
        {
            var panel = ThumbnailListBox.FindDescendantOfType<VirtualizingStackPanel>();
            int firstRow = panel?.FirstRealizedIndex ?? 0;
            int lastRow = panel?.LastRealizedIndex ?? Math.Min(vm.ThumbnailRows.Count - 1, 3);
            if (firstRow >= 0 && lastRow >= firstRow)
            {
                var items = vm.SelectedTab.FilteredItems;
                int columns = Math.Max(1, vm.ThumbnailColumnCount);
                int start = Math.Clamp(firstRow * columns, 0, items.Count);
                int end = Math.Clamp((lastRow + 1) * columns, start, items.Count);
                if (end > start)
                {
                    ThumbnailService.Instance.TryPopulateFromMemoryCache(items.Skip(start).Take(end - start), (int)vm.ThumbnailSize);
                }
            }
        }

        int configuredDelay = SettingsService.Instance.CurrentSettings.ThumbnailScrollDebounceMilliseconds;
        int delay = Math.Clamp(configuredDelay, 60, 250);
        _thumbnailDebounceTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _thumbnailDebounceTimer.Stop();
        _thumbnailDebounceTimer.Start();
    }

    private void OnThumbnailDebounceTick(object? sender, EventArgs e)
    {
        _thumbnailDebounceTimer.Stop();
        LoadRealizedThumbnailWindow();
    }

    private void LoadRealizedThumbnailWindow()
    {
        if (ThumbnailListBox == null || DataContext is not ExplorerPaneViewModel vm ||
            !vm.IsThumbnailView || vm.SelectedTab == null || vm.ThumbnailRows.Count == 0)
        {
            return;
        }

        var panel = ThumbnailListBox.FindDescendantOfType<VirtualizingStackPanel>();
        int firstRow;
        int lastRow;
        bool panelRealized = panel != null && panel.FirstRealizedIndex >= 0 && panel.LastRealizedIndex >= panel.FirstRealizedIndex;

        if (panelRealized)
        {
            firstRow = panel!.FirstRealizedIndex;
            lastRow = panel.LastRealizedIndex;
        }
        else
        {
            _thumbnailScrollViewer ??= ThumbnailListBox.FindDescendantOfType<ScrollViewer>();
            double cellHeight = Math.Max(50.0, vm.ThumbnailCellHeight);
            double offset = _thumbnailScrollViewer?.Offset.Y ?? 0;
            double viewportHeight = (_thumbnailScrollViewer != null && _thumbnailScrollViewer.Viewport.Height > 0)
                ? _thumbnailScrollViewer.Viewport.Height
                : (ThumbnailListBox.Bounds.Height > 0 ? ThumbnailListBox.Bounds.Height : 600.0);

            firstRow = Math.Max(0, (int)Math.Floor(offset / cellHeight));
            int visibleRowCount = Math.Max(1, (int)Math.Ceiling(viewportHeight / cellHeight) + 1);
            lastRow = Math.Min(vm.ThumbnailRows.Count - 1, firstRow + visibleRowCount);
        }

        if (firstRow < 0 || lastRow < firstRow) return;

        var items = vm.SelectedTab.FilteredItems;
        var window = ThumbnailViewportPlanner.Plan(
            items.Count,
            vm.ThumbnailColumnCount,
            firstRow,
            lastRow,
            SettingsService.Instance.CurrentSettings.ThumbnailPrefetchViewports);

        var visible = new List<FileItem>(window.VisibleEnd - window.VisibleStart);
        var prefetch = new List<FileItem>(Math.Max(0, window.RetainedEnd - window.RetainedStart - visible.Count));
        var retained = new HashSet<FileItem>();
        for (int index = window.RetainedStart; index < window.RetainedEnd; index++)
        {
            var item = items[index];
            retained.Add(item);
            if (index >= window.VisibleStart && index < window.VisibleEnd) visible.Add(item);
            else prefetch.Add(item);
        }

        foreach (var oldItem in _retainedThumbnailItems)
        {
            if (!retained.Contains(oldItem) &&
                !ThumbnailService.Instance.IsCachedInMemory(oldItem.FullPath, oldItem.SizeBytes, oldItem.ModifiedTime, (int)vm.ThumbnailSize))
            {
                oldItem.ThumbnailImage = null;
            }
        }
        _retainedThumbnailItems = retained;

        var previous = _thumbnailViewportCts;
        var current = new CancellationTokenSource();
        _thumbnailViewportCts = current;
        previous?.Cancel();

        _ = LoadViewportSafelyAsync(
            visible,
            prefetch,
            (int)vm.ThumbnailSize,
            current);
    }

    private async Task LoadViewportSafelyAsync(
        IReadOnlyList<FileItem> visible,
        IReadOnlyList<FileItem> prefetch,
        int thumbnailSize,
        CancellationTokenSource cts)
    {
        try
        {
            await ThumbnailService.Instance.LoadViewportAsync(
                visible,
                prefetch,
                thumbnailSize,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Thumbnail viewport load failed: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_thumbnailViewportCts, cts))
            {
                _thumbnailViewportCts = null;
            }
            cts.Dispose();
        }
    }
}
