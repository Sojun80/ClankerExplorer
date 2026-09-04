using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Services.Metadata;

namespace ClankerExplorer.ViewModels;

public partial class PropertiesViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _hashCts;
    private bool _isDisposed;

    [ObservableProperty]
    private FileItem? _item;

    [ObservableProperty]
    private FileMetadata? _metadata;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _itemName = string.Empty;

    [ObservableProperty]
    private string _itemPath = string.Empty;

    [ObservableProperty]
    private string _itemTypeDisplay = string.Empty;

    [ObservableProperty]
    private string _sizeDisplay = string.Empty;

    [ObservableProperty]
    private IImage? _icon;

    [ObservableProperty]
    private bool _isDirectory;

    // Checksums
    [ObservableProperty]
    private string _sha256 = string.Empty;

    [ObservableProperty]
    private string _md5 = string.Empty;

    [ObservableProperty]
    private bool _isHashing;

    [ObservableProperty]
    private bool _hasHashes;

    public PropertiesViewModel(FileItem item)
    {
        _item = item;
        ItemName = item.Name;
        ItemPath = item.FullPath;
        ItemTypeDisplay = item.ItemTypeDisplay;
        SizeDisplay = item.SizeDisplay;
        IsDirectory = item.IsDirectory;
        Icon = item.LargeIcon ?? item.FileIcon;

        _ = LoadMetadataAsync(item.FullPath);
    }

    public PropertiesViewModel(string filePath)
    {
        ItemName = Path.GetFileName(filePath);
        ItemPath = filePath;
        IsDirectory = Directory.Exists(filePath);
        ItemTypeDisplay = IsDirectory ? "File folder" : "File";
        SizeDisplay = IsDirectory ? "—" : "Loading...";

        _ = LoadMetadataAsync(filePath);
    }

    public async Task LoadMetadataAsync(string filePath)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var meta = await FileMetadataService.Instance.GetMetadataAsync(filePath, token);
            if (!token.IsCancellationRequested)
            {
                Metadata = meta;
                ItemName = meta.ItemName;
                ItemPath = meta.FilePath;
                ItemTypeDisplay = meta.QuickTypeDisplay;
                SizeDisplay = meta.FormattedSize;
                IsDirectory = meta.IsDirectory;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load metadata: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task ComputeHashesAsync()
    {
        if (IsDirectory || string.IsNullOrEmpty(ItemPath) || !File.Exists(ItemPath) || IsHashing) return;

        _hashCts?.Cancel();
        _hashCts = new CancellationTokenSource();
        var token = _hashCts.Token;

        IsHashing = true;
        HasHashes = false;
        Sha256 = "Computing...";
        Md5 = "Computing...";

        try
        {
            var res = await FileMetadataService.Instance.CalculateHashesAsync(ItemPath, token);
            if (!token.IsCancellationRequested)
            {
                Sha256 = res.Sha256;
                Md5 = res.Md5;
                HasHashes = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Sha256 = $"Error: {ex.Message}";
            Md5 = $"Error: {ex.Message}";
            HasHashes = true;
        }
        finally
        {
            IsHashing = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _loadCts?.Cancel();
        _hashCts?.Cancel();
    }
}
