using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClankerExplorer.Views;

public partial class ConfirmDeleteWindow : Window
{
    public ConfirmDeleteWindow()
    {
        InitializeComponent();
    }

    public ConfirmDeleteWindow(string itemName, string fullPath) : this()
    {
        TxtItemName.Text = $"📁 {itemName}";
        TxtItemPath.Text = fullPath;

        Loaded += (s, e) =>
        {
            BtnDelete.Focus();
        };

        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Close(true);
            }
            else if (e.Key == Key.Escape)
            {
                Close(false);
            }
        };
    }

    private void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
