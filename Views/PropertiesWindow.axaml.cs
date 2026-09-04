using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClankerExplorer.Models;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow()
    {
        InitializeComponent();
    }

    public PropertiesWindow(FileItem item) : this()
    {
        DataContext = new PropertiesViewModel(item);
    }

    public PropertiesWindow(string filePath) : this()
    {
        DataContext = new PropertiesViewModel(filePath);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is IDisposable disp)
        {
            disp.Dispose();
        }
    }
}
