using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.Views;

public partial class PropertiesWindow : Window
{
    private readonly FileItem? _item;

    public PropertiesWindow()
    {
        InitializeComponent();
    }

    public PropertiesWindow(FileItem item) : this()
    {
        _item = item;
        TxtName.Text = item.Name;
        TxtPath.Text = item.FullPath;
        TxtSize.Text = item.SizeDisplay;
        TxtModified.Text = item.FormattedModifiedTime;
        TxtAttributes.Text = item.AttributesString;

        if (item.IsDirectory)
        {
            BtnComputeHash.IsVisible = false;
        }
    }

    private async void OnComputeHashClicked(object? sender, RoutedEventArgs e)
    {
        if (_item == null || _item.IsDirectory) return;
        BtnComputeHash.IsEnabled = false;
        try
        {
            var res = await FileSystemService.Instance.CalculateHashesAsync(_item.FullPath);
            TxtSha256.Text = res.Sha256;
            TxtMd5.Text = res.Md5;
            PanelHashes.IsVisible = true;
        }
        catch (Exception ex)
        {
            TxtSha256.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BtnComputeHash.IsEnabled = true;
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
