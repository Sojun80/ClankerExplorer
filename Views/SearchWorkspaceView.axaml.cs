using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class SearchWorkspaceView : UserControl
{
    private SearchWorkspaceViewModel? _wiredVm;

    public SearchWorkspaceView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wiredVm != null)
        {
            _wiredVm.RequestSetClipboardText -= OnRequestSetClipboardText;
            _wiredVm = null;
        }

        if (DataContext is SearchWorkspaceViewModel vm)
        {
            _wiredVm = vm;
            vm.RequestSetClipboardText += OnRequestSetClipboardText;
        }
    }

    private async void OnRequestSetClipboardText(string text)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null && !string.IsNullOrEmpty(text))
        {
            try
            {
                await topLevel.Clipboard.SetTextAsync(text);
            }
            catch
            {
                // Clipboard write failure
            }
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        FocusSearchBox();
    }

    public void FocusSearchBox()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SearchTextBox?.Focus();
            SearchTextBox?.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnSearchTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SearchWorkspaceViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            vm.SubmitSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && ResultsDataGrid != null && vm.Results.Count > 0)
        {
            ResultsDataGrid.Focus();
            if (vm.SelectedResult == null)
            {
                vm.SelectedResult = vm.Results[0];
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(vm.Query))
            {
                vm.ClearQuery();
                e.Handled = true;
            }
            else
            {
                vm.CloseWorkspace();
                e.Handled = true;
            }
        }
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SearchWorkspaceViewModel vm || vm.SelectedResult == null) return;

        // Ensure double-tap occurred on a data row/cell, not on scrollbar or header
        var source = e.Source as Visual;
        bool isRow = false;
        var curr = source;
        while (curr != null && curr != ResultsDataGrid)
        {
            if (curr is Avalonia.Controls.Primitives.ScrollBar ||
                curr is Avalonia.Controls.Primitives.Thumb ||
                curr is DataGridColumnHeader ||
                curr is Button)
            {
                return;
            }

            if (curr is DataGridRow || curr is DataGridCell)
            {
                isRow = true;
                break;
            }

            curr = curr.GetVisualParent();
        }

        if (isRow)
        {
            vm.OpenItem(vm.SelectedResult);
            e.Handled = true;
        }
    }

    private void OnResultsDataGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SearchWorkspaceViewModel vm || vm.SelectedResult == null) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.Enter)
        {
            if (ctrl || shift)
            {
                vm.OpenContainingFolder(vm.SelectedResult);
            }
            else
            {
                vm.OpenItem(vm.SelectedResult);
            }
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key == Key.C)
        {
            vm.CopyPath(vm.SelectedResult);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CloseWorkspace();
            e.Handled = true;
        }
    }
}
