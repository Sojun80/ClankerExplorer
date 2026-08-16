using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class InspectorPanelView : UserControl
{
    public InspectorPanelView()
    {
        InitializeComponent();

        Loaded += (s, e) =>
        {
            if (VideoHost != null)
            {
                VideoHost.NativeHwndCreated += hwnd =>
                {
                    if (DataContext is InspectorViewModel vm)
                    {
                        vm.RegisterVideoHostHwnd(hwnd);
                    }
                };

                VideoHost.BoundsChangedNotification += bounds =>
                {
                    if (DataContext is InspectorViewModel vm)
                    {
                        vm.UpdateVideoHostBounds((int)bounds.Width, (int)bounds.Height);
                    }
                };
            }
        };

        DataContextChanged += (s, e) =>
        {
            if (DataContext is InspectorViewModel vm && VideoHost != null && VideoHost.ChildHwnd != IntPtr.Zero)
            {
                vm.RegisterVideoHostHwnd(VideoHost.ChildHwnd);
                vm.UpdateVideoHostBounds((int)VideoHost.Bounds.Width, (int)VideoHost.Bounds.Height);
            }
        };
    }

    private void OnVideoSliderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is Slider slider && DataContext is InspectorViewModel vm)
        {
            vm.SeekVideoCommand.Execute(slider.Value);
        }
    }

    private void OnImagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is InspectorViewModel vm && (vm.IsImagePreview || vm.IsPdfPreview))
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || !vm.IsFitMode)
            {
                if (e.Delta.Y > 0)
                {
                    vm.ZoomInCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Delta.Y < 0)
                {
                    vm.ZoomOutCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
    }

    private void OnImageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is InspectorViewModel vm && (vm.IsImagePreview || vm.IsPdfPreview))
        {
            vm.ToggleFitOrActualCommand.Execute(null);
            e.Handled = true;
        }
    }
}
