using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClankerExplorer.Views;

public partial class NewItemWindow : Window
{
    private readonly string _type;
    private readonly string _parentPath;

    public string ItemName { get; private set; } = string.Empty;

    public NewItemWindow()
    {
        InitializeComponent();
        _type = "folder";
        _parentPath = @"C:\";
    }

    public NewItemWindow(string type, string parentPath) : this()
    {
        _type = type;
        _parentPath = parentPath;
        TxtTitle.Text = type == "folder" ? "📁 Create New Folder" : "📄 Create New File";
        TxtInput.Text = type == "folder" ? "New Folder" : "New File.txt";

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
    }

    private void OnCreateClicked(object? sender, RoutedEventArgs e)
    {
        Confirm();
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

        ItemName = text;
        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
