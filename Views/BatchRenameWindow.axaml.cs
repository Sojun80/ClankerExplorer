using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class BatchRenameWindow : Window
{
    public BatchRenameWindow()
    {
        InitializeComponent();
    }

    public BatchRenameWindow(IEnumerable<string> paths) : this()
    {
        var vm = new BatchRenameViewModel(paths);
        vm.RequestClose += () => Close(true);
        DataContext = vm;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
