using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClankerExplorer.Views;

public partial class NetworkShareWindow : Window
{
    public string NetworkPath { get; private set; } = string.Empty;

    public NetworkShareWindow()
    {
        InitializeComponent();

        Loaded += (s, e) =>
        {
            TxtPath.Focus();
            TxtPath.CaretIndex = TxtPath.Text?.Length ?? 0;
        };
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Connect();
        }
    }

    private void OnConnectClicked(object? sender, RoutedEventArgs e)
    {
        Connect();
    }

    private void Connect()
    {
        var text = TxtPath.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text == "\\")
        {
            TxtError.Text = "Please enter a valid network path (e.g. \\\\server\\share)";
            TxtError.IsVisible = true;
            return;
        }

        NetworkPath = text;
        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
