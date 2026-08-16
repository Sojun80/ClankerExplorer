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

    private Point _last3DPoint;
    private bool _is3DDragging;
    private bool _is3DRightDragging;

    private void On3DPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(sender as Visual).Properties;
        if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
        {
            _last3DPoint = e.GetPosition(sender as Visual);
            _is3DDragging = props.IsLeftButtonPressed;
            _is3DRightDragging = props.IsRightButtonPressed;
            e.Pointer.Capture(sender as Control);
            e.Handled = true;
        }
    }

    private void On3DPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_is3DDragging && !_is3DRightDragging) return;
        if (DataContext is not InspectorViewModel vm || !vm.IsStlPreview) return;

        var currentPoint = e.GetPosition(sender as Visual);
        var delta = currentPoint - _last3DPoint;
        _last3DPoint = currentPoint;

        if (_is3DRightDragging || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Pan
            _ = vm.PanStlAsync(delta.X, -delta.Y);
        }
        else if (_is3DDragging)
        {
            // Orbit Rotate
            _ = vm.RotateStlAsync(delta.X * 0.7, -delta.Y * 0.7);
        }

        e.Handled = true;
    }

    private void On3DPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _is3DDragging = false;
        _is3DRightDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void On3DPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is InspectorViewModel vm && vm.IsStlPreview)
        {
            if (e.Delta.Y > 0)
            {
                _ = vm.ZoomStlInCommand.ExecuteAsync(null);
            }
            else if (e.Delta.Y < 0)
            {
                _ = vm.ZoomStlOutCommand.ExecuteAsync(null);
            }
            e.Handled = true;
        }
    }

    private void On3DDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is InspectorViewModel vm && vm.IsStlPreview)
        {
            _ = vm.ResetStlViewCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }
}

