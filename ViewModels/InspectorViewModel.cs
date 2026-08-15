using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class InspectorViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextPreview))]
    [NotifyPropertyChangedFor(nameof(IsBinaryPreview))]
    private FilePreviewData? _previewData;

    public bool IsTextPreview => PreviewData?.PreviewType == "text";
    public bool IsBinaryPreview => PreviewData?.PreviewType == "binary";

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
        if (string.IsNullOrEmpty(filePath))
        {
            PreviewData = null;
            HexRows.Clear();
            Sha256Hash = string.Empty;
            Md5Hash = string.Empty;
            HasHashes = false;
            StatusMessage = "Select a file to inspect";
            return;
        }

        StatusMessage = "Loading preview...";
        HasHashes = false;
        Sha256Hash = string.Empty;
        Md5Hash = string.Empty;

        var data = await FileSystemService.Instance.GetPreviewDataAsync(filePath);
        PreviewData = data;

        if (data.HexRows != null)
        {
            HexRows = new ObservableCollection<HexRow>(data.HexRows);
        }
        else
        {
            HexRows.Clear();
        }

        StatusMessage = data.PreviewType == "directory" ? "Directory selected" : "";
    }

    [RelayCommand]
    public async Task ComputeHashesAsync()
    {
        if (PreviewData == null || PreviewData.PreviewType == "directory") return;

        IsHashing = true;
        try
        {
            var res = await FileSystemService.Instance.CalculateHashesAsync(PreviewData.FilePath);
            Sha256Hash = res.Sha256;
            Md5Hash = res.Md5;
            HasHashes = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Hashing error: {ex.Message}";
        }
        finally
        {
            IsHashing = false;
        }
    }
}
