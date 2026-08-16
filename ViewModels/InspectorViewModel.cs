using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class InspectorViewModel : ObservableObject
{
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _hashingCts;
    private long _previewGeneration = 0;
    private string? _currentFilePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextPreview))]
    [NotifyPropertyChangedFor(nameof(IsBinaryPreview))]
    [NotifyPropertyChangedFor(nameof(IsHexPreview))]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsImageError))]
    private FilePreviewData? _previewData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsImageError))]
    private Bitmap? _imagePreview;

    [ObservableProperty]
    private string _imageDimensions = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageError))]
    private bool _isLoadingPreview;

    [ObservableProperty]
    private string? _previewErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentDisplay))]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentDisplay))]
    private bool _isFitMode = true;

    public string ZoomPercentDisplay => IsFitMode ? "Fit" : $"{(int)(ZoomLevel * 100)}%";

    public bool IsImagePreview => PreviewData?.PreviewType == "image" && ImagePreview != null;
    public bool IsImageError => PreviewData?.PreviewType == "image" && ImagePreview == null && !IsLoadingPreview;
    public bool IsTextPreview => PreviewData?.PreviewType == "text";
    public bool IsBinaryPreview => PreviewData?.PreviewType == "binary";
    public bool IsHexPreview => PreviewData?.PreviewType == "hex";

    [ObservableProperty]
    private ObservableCollection<HexRow> _hexRows = new();

    [ObservableProperty]
    private string _sha256Hash = string.Empty;

    [ObservableProperty]
    private string _md5Hash = string.Empty;

    [ObservableProperty]
    private bool _isHashing;

    [ObservableProperty]
    private bool _hasHashes;

    [ObservableProperty]
    private string _statusMessage = "Select a file to inspect";

    public async Task LoadPreviewAsync(string? filePath)
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        long generation = Interlocked.Increment(ref _previewGeneration);
        _currentFilePath = filePath;

        // Reset previous preview state immediately to avoid stale flashes
        ImagePreview = null;
        ImageDimensions = string.Empty;
        PreviewErrorMessage = null;
        IsFitMode = true;
        ZoomLevel = 1.0;

        if (string.IsNullOrEmpty(filePath))
        {
            PreviewData = null;
            HexRows.Clear();
            Sha256Hash = string.Empty;
            Md5Hash = string.Empty;
            HasHashes = false;
            IsLoadingPreview = false;
            StatusMessage = "Select a file to inspect";
            return;
        }

        IsLoadingPreview = true;
        StatusMessage = "Loading preview...";
        HasHashes = false;
        Sha256Hash = string.Empty;
        Md5Hash = string.Empty;

        try
        {
            var data = await FileSystemService.Instance.GetPreviewDataAsync(filePath, token);
            if (token.IsCancellationRequested || generation != _previewGeneration || _currentFilePath != filePath) return;

            PreviewData = data;

            if (data.HexRows != null)
            {
                HexRows = new ObservableCollection<HexRow>(data.HexRows);
            }
            else
            {
                HexRows.Clear();
            }

            if (data.PreviewType == "image")
            {
                var imgResult = await ImagePreviewService.Instance.LoadImagePreviewAsync(filePath, token);
                if (token.IsCancellationRequested || generation != _previewGeneration || _currentFilePath != filePath) return;

                if (imgResult.Success && imgResult.Bitmap != null)
                {
                    ImagePreview = imgResult.Bitmap;
                    ImageDimensions = imgResult.FormattedDimensions;
                    PreviewErrorMessage = null;
                }
                else
                {
                    ImagePreview = null;
                    PreviewErrorMessage = imgResult.ErrorMessage ?? "Failed to load image preview";
                }
            }

            StatusMessage = data.PreviewType == "directory" ? "Directory selected" : "";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (generation == _previewGeneration && _currentFilePath == filePath)
            {
                IsLoadingPreview = false;
            }
        }
    }

    [RelayCommand]
    public void ZoomIn()
    {
        IsFitMode = false;
        ZoomLevel = Math.Clamp(ZoomLevel * 1.25, 0.1, 8.0);
    }

    [RelayCommand]
    public void ZoomOut()
    {
        IsFitMode = false;
        ZoomLevel = Math.Clamp(ZoomLevel / 1.25, 0.1, 8.0);
    }

    [RelayCommand]
    public void ResetZoom()
    {
        IsFitMode = true;
        ZoomLevel = 1.0;
    }

    [RelayCommand]
    public void ActualSize()
    {
        IsFitMode = false;
        ZoomLevel = 1.0;
    }

    [RelayCommand]
    public void ToggleFitOrActual()
    {
        if (IsFitMode)
        {
            IsFitMode = false;
            ZoomLevel = 1.0;
        }
        else
        {
            IsFitMode = true;
            ZoomLevel = 1.0;
        }
    }

    [RelayCommand]
    public async Task ComputeHashesAsync()
    {
        if (PreviewData == null || PreviewData.PreviewType == "directory" || string.IsNullOrEmpty(_currentFilePath)) return;

        _hashingCts?.Cancel();
        _hashingCts = new CancellationTokenSource();
        var token = _hashingCts.Token;

        var targetPath = _currentFilePath;
        IsHashing = true;

        try
        {
            var res = await FileSystemService.Instance.ComputeHashesAsync(targetPath, token);
            if (token.IsCancellationRequested || _currentFilePath != targetPath) return;

            Sha256Hash = res.Sha256;
            Md5Hash = res.Md5;
            HasHashes = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_currentFilePath == targetPath)
            {
                StatusMessage = $"Hashing error: {ex.Message}";
            }
        }
        finally
        {
            if (_currentFilePath == targetPath)
            {
                IsHashing = false;
            }
        }
    }
}
