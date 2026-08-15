using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClankerExplorer.Views;

public partial class RenameWindow : Window
{
    public string NewName { get; private set; } = string.Empty;

    public RenameWindow()
    {
        InitializeComponent();
    }

    public RenameWindow(string initialName) : this()
    {
        TxtInput.Text = initialName;

        Loaded += (s, e) =>
        {
            TxtInput.Focus();
            TxtInput.SelectAll();
        };
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
        }
        else if (e.Key == Key.Escape)
        {
            Close(false);
        }
    }

    private void OnRenameClicked(object? sender, RoutedEventArgs e)
    {
        Confirm();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void Confirm()
    {
        var text = TxtInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            TxtError.Text = "Name cannot be empty";
            TxtError.IsVisible = true;
            return;
        }

        NewName = text;
        Close(true);
    }
}
