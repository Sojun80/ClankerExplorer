using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClankerExplorer.AppLayer;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class BatchRenameWindow : Window
{
    public BatchRenameWindow()
    {
        InitializeComponent();
    }

    public BatchRenameWindow(
        IEnumerable<string> paths,
        IFileOperationService? fileOperationService = null) : this()
    {
        var vm = new BatchRenameViewModel(paths, fileOperationService);
        vm.RequestClose += () => Close(true);
        DataContext = vm;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
