using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

// Middle-button autoscroll.
public partial class ExplorerPaneView
{
    private readonly DispatcherTimer _middleScrollTimer;
    private bool _isMiddleAutoScrolling;
    private Point _autoScrollAnchorPos;
    private Point _currentPointerPos;
    private bool _hasMovedDuringMiddleScroll;
    private ScrollViewer? _activeMiddleScrollViewer;

    private void OnFileGridPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (IsFromActiveRenameTextBox(e)) return;

        if (_isMiddleAutoScrolling)
        {
            StopMiddleAutoScroll();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (FileGridContainer != null)
        {
            var props = e.GetCurrentPoint(FileGridContainer).Properties;
            if (props.IsMiddleButtonPressed)
            {
                StartMiddleAutoScroll(e);
                e.Handled = true;
            }
        }
    }

    private void StartMiddleAutoScroll(PointerPressedEventArgs e)
    {
        if (FileGridContainer == null) return;

        _isMiddleAutoScrolling = true;
        _hasMovedDuringMiddleScroll = false;
        _autoScrollAnchorPos = e.GetPosition(FileGridContainer);
        _currentPointerPos = _autoScrollAnchorPos;
        _activeMiddleScrollViewer = GetActiveMiddleScrollViewer();

        if (AutoScrollAnchor != null && AutoScrollCanvas != null)
        {
            double halfW = AutoScrollAnchor.Width > 0 ? AutoScrollAnchor.Width / 2 : 14;
            double halfH = AutoScrollAnchor.Height > 0 ? AutoScrollAnchor.Height / 2 : 14;
            Canvas.SetLeft(AutoScrollAnchor, _autoScrollAnchorPos.X - halfW);
            Canvas.SetTop(AutoScrollAnchor, _autoScrollAnchorPos.Y - halfH);
            AutoScrollCanvas.IsVisible = true;
        }

        e.Pointer.Capture(FileGridContainer);
        _middleScrollTimer.Start();
    }

    private void StopMiddleAutoScroll()
    {
        if (!_isMiddleAutoScrolling) return;

        _isMiddleAutoScrolling = false;
        _middleScrollTimer.Stop();
        _activeMiddleScrollViewer = null;

        if (AutoScrollCanvas != null)
        {
            AutoScrollCanvas.IsVisible = false;
        }
    }

    private ScrollViewer? GetActiveMiddleScrollViewer()
    {
        if (DataContext is ExplorerPaneViewModel vm)
        {
            if (vm.IsThumbnailView && ThumbnailListBox != null)
            {
                return ThumbnailListBox.FindDescendantOfType<ScrollViewer>();
            }
            else if (FileDataGrid != null)
            {
                return FileDataGrid.FindDescendantOfType<ScrollViewer>();
            }
        }
        return null;
    }

    private void OnMiddleScrollTick(object? sender, EventArgs e)
    {
        if (!_isMiddleAutoScrolling || _activeMiddleScrollViewer == null)
        {
            StopMiddleAutoScroll();
            return;
        }

        double dx = _currentPointerPos.X - _autoScrollAnchorPos.X;
        double dy = _currentPointerPos.Y - _autoScrollAnchorPos.Y;

        const double deadZone = 8.0;
        double vx = 0;
        double vy = 0;

        if (Math.Abs(dy) > deadZone)
        {
            double distY = Math.Abs(dy) - deadZone;
            double signY = Math.Sign(dy);
            vy = signY * Math.Pow(distY * 0.16, 1.4);
        }

        if (Math.Abs(dx) > deadZone && _activeMiddleScrollViewer.Extent.Width > _activeMiddleScrollViewer.Viewport.Width)
        {
            double distX = Math.Abs(dx) - deadZone;
            double signX = Math.Sign(dx);
            vx = signX * Math.Pow(distX * 0.16, 1.4);
        }

        if (vx != 0 || vy != 0)
        {
            double maxOffsetX = Math.Max(0, _activeMiddleScrollViewer.Extent.Width - _activeMiddleScrollViewer.Viewport.Width);
            double maxOffsetY = Math.Max(0, _activeMiddleScrollViewer.Extent.Height - _activeMiddleScrollViewer.Viewport.Height);

            double newX = Math.Clamp(_activeMiddleScrollViewer.Offset.X + vx, 0, maxOffsetX);
            double newY = Math.Clamp(_activeMiddleScrollViewer.Offset.Y + vy, 0, maxOffsetY);

            _activeMiddleScrollViewer.Offset = new Vector(newX, newY);
        }
    }
}
