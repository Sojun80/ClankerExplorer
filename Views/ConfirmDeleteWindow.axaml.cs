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

    public ConfirmDeleteWindow(string itemName, string fullPath, bool permanent = false) : this()
    {
        TxtItemName.Text = $"📁 {itemName}";
        TxtItemPath.Text = fullPath;

        if (permanent)
        {
            TxtTitle.Text = "Permanently Delete Item";
            TxtHeader.Text = "Are you sure you want to permanently delete this item?";
            TxtWarning.IsVisible = true;
            BtnDelete.Content = "Permanently Delete";
        }
        else
        {
            TxtTitle.Text = "Move to Recycle Bin";
            TxtHeader.Text = "Are you sure you want to move this item to the Recycle Bin?";
            TxtWarning.IsVisible = false;
            BtnDelete.Content = "Move to Recycle Bin";
        }

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
