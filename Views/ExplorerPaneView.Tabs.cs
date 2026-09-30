using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

// Tab dragging and overflow scrolling.
public partial class ExplorerPaneView
{
    private ExplorerTabViewModel? _pressedTab;
    private Point _tabPressStartPoint;
    private bool _isTabDragging;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Visual visual && visual.DataContext is ExplorerTabViewModel tab && DataContext is ExplorerPaneViewModel vm)
        {
            if (e.GetCurrentPoint(visual).Properties.IsLeftButtonPressed)
            {
                _pressedTab = tab;
                _tabPressStartPoint = e.GetPosition(this);
                _isTabDragging = false;
                vm.SelectedTab = tab;
            }
        }
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTab != null && DataContext is ExplorerPaneViewModel vm)
        {
            var cur = e.GetPosition(this);
            var delta = cur - _tabPressStartPoint;

            var topLevel = TopLevel.GetTopLevel(this);
            var windowPos = topLevel != null ? e.GetPosition(topLevel) : cur;

            if (!_isTabDragging && PointerGestureClassifier.ExceedsDragThreshold(delta.X, delta.Y, 6))
            {
                _isTabDragging = true;
                TabDragCoordinator.Instance.StartDrag(_pressedTab, vm, e.KeyModifiers.HasFlag(KeyModifiers.Control), windowPos);
            }
        }
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedTab = null;
        _isTabDragging = false;
    }

    private void OnTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _pressedTab = null;
        _isTabDragging = false;
    }

    private void UpdateTabScrollButtonsVisibility()
    {
        if (TabsScrollViewer != null && TabScrollButtonsPanel != null)
        {
            var content = TabsScrollViewer.Presenter?.Content as Control;
            double contentWidth = content != null && content.Bounds.Width > 0 ? content.Bounds.Width : TabsScrollViewer.Extent.Width;
            double viewportWidth = TabsScrollViewer.Viewport.Width;
            if (contentWidth <= viewportWidth + 4 && TabsScrollViewer.Offset.X > 0)
            {
                TabsScrollViewer.Offset = new Vector(0, TabsScrollViewer.Offset.Y);
            }
            bool canScroll = contentWidth > viewportWidth + 4;
            TabScrollButtonsPanel.IsVisible = canScroll;
        }
    }

    private void OnTabsPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (TabsScrollViewer != null)
        {
            double scrollDelta = e.Delta.Y != 0 ? -e.Delta.Y * 70 : -e.Delta.X * 70;
            double maxOffset = Math.Max(0, TabsScrollViewer.Extent.Width - TabsScrollViewer.Viewport.Width);
            double targetOffset = Math.Clamp(TabsScrollViewer.Offset.X + scrollDelta, 0, maxOffset);
            TabsScrollViewer.Offset = new Vector(targetOffset, TabsScrollViewer.Offset.Y);
            UpdateTabScrollButtonsVisibility();
            e.Handled = true;
        }
    }

    private void OnTabScrollLeftClicked(object? sender, RoutedEventArgs e)
    {
        if (TabsScrollViewer != null)
        {
            double targetOffset = Math.Max(0, TabsScrollViewer.Offset.X - 120);
            TabsScrollViewer.Offset = new Vector(targetOffset, TabsScrollViewer.Offset.Y);
            UpdateTabScrollButtonsVisibility();
        }
    }

    private void OnTabScrollRightClicked(object? sender, RoutedEventArgs e)
    {
        if (TabsScrollViewer != null)
        {
            double maxOffset = Math.Max(0, TabsScrollViewer.Extent.Width - TabsScrollViewer.Viewport.Width);
            double targetOffset = Math.Min(maxOffset, TabsScrollViewer.Offset.X + 120);
            TabsScrollViewer.Offset = new Vector(targetOffset, TabsScrollViewer.Offset.Y);
            UpdateTabScrollButtonsVisibility();
        }
    }

    private void InitializeTabStrip()
    {
        if (DataContext is ExplorerPaneViewModel vm)
        {
            vm.Tabs.CollectionChanged += (s2, e2) =>
            {
                Dispatcher.UIThread.Post(UpdateTabScrollButtonsVisibility, DispatcherPriority.Loaded);
            };

            vm.PropertyChanged += (s2, e2) =>
            {
                if (e2.PropertyName == nameof(ExplorerPaneViewModel.TabWidth))
                {
                    Dispatcher.UIThread.Post(UpdateTabScrollButtonsVisibility, DispatcherPriority.Loaded);
                }
            };
        }

        if (TabsScrollViewer != null)
        {
            TabsScrollViewer.SizeChanged += (s2, e2) => UpdateTabScrollButtonsVisibility();
            TabsScrollViewer.LayoutUpdated += (s2, e2) => UpdateTabScrollButtonsVisibility();
        }
    }
}
