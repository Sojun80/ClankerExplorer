using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClankerExplorer.Platform;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Behaviors;

public enum ExplorerScrollMode
{
    None,
    Details,
    Thumbnails
}

public static class ExplorerScrollBehavior
{
    public static readonly AttachedProperty<ExplorerScrollMode> ScrollModeProperty =
        AvaloniaProperty.RegisterAttached<Control, ExplorerScrollMode>(
            "ScrollMode",
            typeof(ExplorerScrollBehavior),
            ExplorerScrollMode.None);

    public static ExplorerScrollMode GetScrollMode(Control element) => element.GetValue(ScrollModeProperty);
    public static void SetScrollMode(Control element, ExplorerScrollMode value) => element.SetValue(ScrollModeProperty, value);

    static ExplorerScrollBehavior()
    {
        ScrollModeProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            var oldMode = args.GetOldValue<ExplorerScrollMode>();
            var newMode = args.GetNewValue<ExplorerScrollMode>();

            if (oldMode != ExplorerScrollMode.None)
            {
                control.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChangedTunnel);
            }

            if (newMode != ExplorerScrollMode.None)
            {
                control.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChangedTunnel, RoutingStrategies.Tunnel);
            }
        });
    }

    private static void OnPointerWheelChangedTunnel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not Control control) return;

        var mode = GetScrollMode(control);
        if (mode == ExplorerScrollMode.None) return;

        // Leave horizontal scrolling (e.g. horizontal wheel delta or Shift+Wheel) to default Avalonia handlers
        if (e.Delta.Y == 0 || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        // On non-Windows platforms, retain Avalonia default behavior
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var settings = ScrollSettings.Current.GetMouseWheelSettings();

        // Respect 0 lines (scrolling disabled by Windows setting)
        if (settings.ScrollLines == 0 && !settings.IsPageScroll)
        {
            e.Handled = true;
            return;
        }

        var scrollViewer = control as ScrollViewer ?? control.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer == null) return;

        // If content fits completely inside the viewport, nothing to scroll vertically
        if (scrollViewer.Extent.Height <= scrollViewer.Viewport.Height)
        {
            return;
        }

        double rowHeight = ResolveRowHeight(control, mode);
        double scrollDistance = CalculateScrollDistance(
            mode,
            settings,
            e.Delta.Y,
            rowHeight,
            scrollViewer.Viewport.Height);

        if (Math.Abs(scrollDistance) < 0.0001)
        {
            e.Handled = true;
            return;
        }

        double maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        double targetOffsetY = Math.Clamp(scrollViewer.Offset.Y - scrollDistance, 0, maxOffsetY);

        if (Math.Abs(targetOffsetY - scrollViewer.Offset.Y) > 0.0001)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetOffsetY);
        }

        e.Handled = true;
    }

    public static double ResolveRowHeight(Control control, ExplorerScrollMode mode)
    {
        if (mode == ExplorerScrollMode.Details)
        {
            if (control is DataGrid dataGrid)
            {
                var sampleRow = dataGrid.FindDescendantOfType<DataGridRow>();
                if (sampleRow != null && sampleRow.Bounds.Height > 0)
                {
                    return sampleRow.Bounds.Height;
                }
            }
            return 26.0;
        }

        if (mode == ExplorerScrollMode.Thumbnails)
        {
            if (control is ListBox listBox)
            {
                var sampleItem = listBox.FindDescendantOfType<ListBoxItem>();
                if (sampleItem != null && sampleItem.Bounds.Height > 0)
                {
                    return sampleItem.Bounds.Height;
                }

                if (listBox.DataContext is ExplorerPaneViewModel vm)
                {
                    return vm.ThumbnailCellHeight + 8.0;
                }
            }
            return 214.0;
        }

        return 26.0;
    }

    public static double CalculateScrollDistance(
        ExplorerScrollMode mode,
        MouseWheelSettings settings,
        double deltaY,
        double rowHeight,
        double viewportHeight)
    {
        if (deltaY == 0) return 0;
        if (settings.ScrollLines <= 0 && !settings.IsPageScroll) return 0;

        double effectiveRowHeight = Math.Max(1.0, rowHeight);

        if (settings.IsPageScroll)
        {
            double pageDistance = viewportHeight > 0 ? viewportHeight : effectiveRowHeight * 10;
            return deltaY * pageDistance;
        }

        if (mode == ExplorerScrollMode.Details)
        {
            double notchPixels = settings.ScrollLines * effectiveRowHeight;
            return deltaY * notchPixels;
        }

        if (mode == ExplorerScrollMode.Thumbnails)
        {
            // Translate the user's setting into row movement:
            // Standard Windows default is 3 lines -> 1 row of thumbnails per notch.
            double linesRatio = Math.Max(0.1, settings.ScrollLines / 3.0);
            double notchPixels = linesRatio * effectiveRowHeight;

            // Prevent large thumbnails from jumping absurd distances beyond the viewport
            if (viewportHeight > 0)
            {
                double maxJump = Math.Max(effectiveRowHeight, viewportHeight * 0.85);
                if (notchPixels > maxJump)
                {
                    notchPixels = maxJump;
                }
            }

            return deltaY * notchPixels;
        }

        return deltaY * (settings.ScrollLines * effectiveRowHeight);
    }
}
