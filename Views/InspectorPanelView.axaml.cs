using System;
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

    private void OnImagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is InspectorViewModel vm && vm.IsImagePreview)
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
        if (DataContext is InspectorViewModel vm && vm.IsImagePreview)
        {
            vm.ToggleFitOrActualCommand.Execute(null);
            e.Handled = true;
        }
    }
}
