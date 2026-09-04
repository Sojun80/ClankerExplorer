using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Services.Preview;
using ClankerExplorer.Services.Metadata;

namespace ClankerExplorer.ViewModels;

public partial class InspectorViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _hashingCts;
    private long _previewGeneration = 0;
    private string? _currentFilePath;
    private bool _hasVideoMedia;
    private bool _isSeeking;
    private bool _isDisposed;

    // Video Player
    private readonly NativeVideoPlayer _videoPlayer = new();

    public InspectorViewModel()
    {
        _videoPlayer.TimeChanged += pos =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVideoPlaying && !_isSeeking)
                {
                    VideoPosition = pos;
                }
            });
        };

        _videoPlayer.MediaOpened += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_videoPlayer.Duration > TimeSpan.Zero)
                {
                    VideoDuration = _videoPlayer.Duration;
                }
            });
        };

        _videoPlayer.MediaEnded += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                PauseVideo();
                VideoPosition = VideoDuration;
            });
        };

        _videoPlayer.SetMute(IsVideoMuted);
        _videoPlayer.SetVolume(IsVideoMuted ? 0.0 : VideoVolume);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextPreview))]
    [NotifyPropertyChangedFor(nameof(IsBinaryPreview))]
    [NotifyPropertyChangedFor(nameof(IsHexPreview))]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsImageError))]
    [NotifyPropertyChangedFor(nameof(IsVideoPreview))]
    [NotifyPropertyChangedFor(nameof(IsPdfPreview))]
    [NotifyPropertyChangedFor(nameof(IsZipPreview))]
    [NotifyPropertyChangedFor(nameof(IsStlPreview))]
    private FilePreviewData? _previewData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsVideoPreview))]
    [NotifyPropertyChangedFor(nameof(IsAudioPreview))]
    [NotifyPropertyChangedFor(nameof(IsPdfPreview))]
    [NotifyPropertyChangedFor(nameof(IsZipPreview))]
    [NotifyPropertyChangedFor(nameof(IsStlPreview))]
    [NotifyPropertyChangedFor(nameof(IsTextPreview))]
    [NotifyPropertyChangedFor(nameof(IsBinaryPreview))]
    [NotifyPropertyChangedFor(nameof(IsMetadataFallback))]
    private string _activePreviewType = "none";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMetadataFallback))]
    private FileMetadata? _itemMetadata;

    [ObservableProperty]
    private bool _isLoadingPreview;

    [ObservableProperty]
    private string? _previewErrorMessage;

    // ==========================================
    // IMAGE PREVIEW STATE
    // ==========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsImageError))]
    private Bitmap? _imagePreview;

    [ObservableProperty]
    private string _imageDimensions = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentDisplay))]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentDisplay))]
    private bool _isFitMode = true;

    public string ZoomPercentDisplay => IsFitMode ? "Fit" : $"{(int)(ZoomLevel * 100)}%";
    public bool IsImagePreview => ActivePreviewType == "image" && ImagePreview != null;
    public bool IsImageError => ActivePreviewType == "image" && ImagePreview == null && !IsLoadingPreview;

    // ==========================================
    // VIDEO PREVIEW STATE
    // ==========================================

    [ObservableProperty]
    private Bitmap? _videoPosterImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseButtonIcon))]
    private bool _isVideoPlaying;

    [ObservableProperty]
    private bool _isVideoSessionActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedVideoPosition))]
    [NotifyPropertyChangedFor(nameof(VideoPositionSeconds))]
    private TimeSpan _videoPosition = TimeSpan.Zero;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedVideoDuration))]
    [NotifyPropertyChangedFor(nameof(VideoDurationSeconds))]
    private TimeSpan _videoDuration = TimeSpan.Zero;

    [ObservableProperty]
    private double _videoVolume = VideoPreferencesService.Instance.Volume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteButtonIcon))]
    private bool _isVideoMuted = VideoPreferencesService.Instance.IsMuted;

    [ObservableProperty]
    private bool _isVideoPlaybackAvailable = true;

    public LibVLCSharp.Shared.MediaPlayer? VlcMediaPlayer => _videoPlayer.VlcMediaPlayer;
    public string PlayPauseButtonIcon => IsVideoPlaying ? "⏸" : "▶";
    public string MuteButtonIcon => IsVideoMuted ? "🔇" : "🔊";
    public bool IsVideoPreview => ActivePreviewType == "video";
    public double VideoPositionSeconds => VideoPosition.TotalSeconds;
    public double VideoDurationSeconds => Math.Max(1.0, VideoDuration.TotalSeconds);
    public string FormattedVideoPosition => FormatTime(VideoPosition);
    public string FormattedVideoDuration => FormatTime(VideoDuration);

    // ==========================================
    // PDF PREVIEW STATE
    // ==========================================

    [ObservableProperty]
    private Bitmap? _pdfCurrentPageBitmap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PdfPageDisplay))]
    [NotifyPropertyChangedFor(nameof(CanPdfGoPrev))]
    [NotifyPropertyChangedFor(nameof(CanPdfGoNext))]
    private uint _pdfCurrentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PdfPageDisplay))]
    [NotifyPropertyChangedFor(nameof(CanPdfGoPrev))]
    [NotifyPropertyChangedFor(nameof(CanPdfGoNext))]
    private uint _pdfTotalPages = 1;

    [ObservableProperty]
    private bool _isPdfFitWidth = true;

    public bool IsPdfPreview => ActivePreviewType == "pdf";
    public string PdfPageDisplay => $"Page {PdfCurrentPage} of {PdfTotalPages}";
    public bool CanPdfGoPrev => PdfCurrentPage > 1;
    public bool CanPdfGoNext => PdfCurrentPage < PdfTotalPages;

    // ==========================================
    // ZIP PREVIEW STATE
    // ==========================================

    [ObservableProperty]
    private ObservableCollection<ZipEntryItem> _zipEntries = new();

    [ObservableProperty]
    private string _zipSummaryDisplay = string.Empty;

    public bool IsZipPreview => ActivePreviewType == "zip";

    // ==========================================
    // 3D / STL PREVIEW STATE
    // ==========================================

    [ObservableProperty]
    private WriteableBitmap? _stlBitmap;

    [ObservableProperty]
    private Model3D? _stlModel;

    [ObservableProperty]
    private string _stlDimensionsDisplay = string.Empty;

    [ObservableProperty]
    private string _stlTrianglesDisplay = string.Empty;

    [ObservableProperty]
    private float _stlYaw = 45f;

    [ObservableProperty]
    private float _stlPitch = -25f;

    [ObservableProperty]
    private float _stlZoom = 1.0f;

    [ObservableProperty]
    private System.Numerics.Vector2 _stlPan = System.Numerics.Vector2.Zero;

    [ObservableProperty]
    private bool _stlWireframe;

    public bool IsStlPreview => ActivePreviewType == "stl" || ActivePreviewType == "3d";

    // ==========================================
    // FALLBACK / TEXT / BINARY / HASHES STATE
    // ==========================================

    public bool IsTextPreview => ActivePreviewType == "text";
    public bool IsBinaryPreview => ActivePreviewType == "binary";
    public bool IsHexPreview => ActivePreviewType == "hex";
    public bool IsAudioPreview => ActivePreviewType == "audio";
    public bool IsMetadataFallback => ActivePreviewType == "metadata" ||
        (ItemMetadata != null && ActivePreviewType != "image" && ActivePreviewType != "video" &&
         ActivePreviewType != "audio" && ActivePreviewType != "pdf" && ActivePreviewType != "zip" &&
         ActivePreviewType != "stl" && ActivePreviewType != "text" && ActivePreviewType != "none");

    private static readonly HashSet<string> InspectorAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".wma", ".aac", ".opus", ".aiff", ".aif", ".ape", ".alac"
    };

    private static bool IsInspectorAudioFile(string ext) => InspectorAudioExtensions.Contains(ext);

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

    // ==========================================
    // PREVIEW LIFECYCLE & LOADING
    // ==========================================

    /// <summary>
    /// Completely stops all active media (video playback, audio) and unloads all cached bitmaps/models from memory.
    /// </summary>
    public void UnloadPreview()
    {
        _previewCts?.Cancel();
        _previewCts = null;
        _hashingCts?.Cancel();
        _hashingCts = null;
        _currentFilePath = null;

        StopVideo();
        IsVideoPlaybackAvailable = false;
        VideoPosterImage = null;

        ImagePreview = null;
        ImageDimensions = string.Empty;

        PdfCurrentPageBitmap = null;
        PdfTotalPages = 0;
        PdfCurrentPage = 1;

        ZipEntries.Clear();
        ZipSummaryDisplay = string.Empty;

        StlBitmap = null;
        StlModel = null;
        StlDimensionsDisplay = string.Empty;
        StlTrianglesDisplay = string.Empty;

        PreviewData = null;
        ItemMetadata = null;
        HexRows.Clear();
        Sha256Hash = string.Empty;
        Md5Hash = string.Empty;
        HasHashes = false;
        IsHashing = false;
        PreviewErrorMessage = null;
        ActivePreviewType = "none";
        IsLoadingPreview = false;
        StatusMessage = "Select a file to inspect";
    }

    public async Task LoadPreviewAsync(string? filePath)
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        long generation = Interlocked.Increment(ref _previewGeneration);
        _currentFilePath = filePath;

        // Immediately stop video playback & reset previous preview state
        StopVideo();
        ImagePreview = null;
        ImageDimensions = string.Empty;
        VideoPosterImage = null;
        PdfCurrentPageBitmap = null;
        ZipEntries.Clear();
        ZipSummaryDisplay = string.Empty;
        StlBitmap = null;
        StlModel = null;
        StlDimensionsDisplay = string.Empty;
        StlTrianglesDisplay = string.Empty;
        StlYaw = 45f;
        StlPitch = -25f;
        StlZoom = 1.0f;
        StlPan = System.Numerics.Vector2.Zero;
        StlWireframe = false;
        PreviewErrorMessage = null;
        ActivePreviewType = "none";
        ItemMetadata = null;
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
            string ext = Path.GetExtension(filePath);

            // Asynchronously query reusable metadata service (LRU cached)
            _ = FileMetadataService.Instance.GetMetadataAsync(filePath, token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result != null && generation == _previewGeneration && _currentFilePath == filePath)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _previewGeneration && _currentFilePath == filePath)
                        {
                            ItemMetadata = t.Result;
                        }
                    });
                }
            }, token);

            if (data.HexRows != null)
            {
                HexRows = new ObservableCollection<HexRow>(data.HexRows);
            }
            else
            {
                HexRows.Clear();
            }

            // 1. Image Preview
            if (ImagePreviewService.Instance.IsSupportedImageExtension(ext))
            {
                ActivePreviewType = "image";
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
                    ActivePreviewType = "metadata";
                }
            }
            // 2. Video Preview
            else if (VideoThumbnailService.IsVideoFile(filePath))
            {
                ActivePreviewType = "video";
                IsVideoPlaybackAvailable = true;
                IsVideoPlaying = false;
                VideoPosition = TimeSpan.Zero;

                // Load poster thumbnail and duration concurrently with a short timeout so preview controls show immediately
                var posterTask = ThumbnailService.Instance.GetThumbnailAsync(filePath, File.GetLastWriteTime(filePath), 512, token);
                var durationTask = VideoThumbnailService.Instance.GetVideoDurationAsync(filePath, token);

                var timeoutTask = Task.Delay(400, token);
                await Task.WhenAny(Task.WhenAll(posterTask, durationTask), timeoutTask);

                if (token.IsCancellationRequested || generation != _previewGeneration || _currentFilePath != filePath) return;

                if (posterTask.IsCompletedSuccessfully)
                {
                    VideoPosterImage = posterTask.Result;
                }
                else
                {
                    // Continue background loading poster image without blocking UI
                    _ = posterTask.ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully && t.Result != null && generation == _previewGeneration && _currentFilePath == filePath)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                if (generation == _previewGeneration && _currentFilePath == filePath)
                                {
                                    VideoPosterImage = t.Result;
                                }
                            });
                        }
                    }, token);
                }

                if (durationTask.IsCompletedSuccessfully)
                {
                    VideoDuration = durationTask.Result;
                }
                else
                {
                    // Continue background duration query without blocking UI
                    _ = durationTask.ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully && generation == _previewGeneration && _currentFilePath == filePath)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                if (generation == _previewGeneration && _currentFilePath == filePath)
                                {
                                    VideoDuration = t.Result;
                                }
                            });
                        }
                    }, token);
                }
            }
            // 2b. Audio Preview
            else if (IsInspectorAudioFile(ext))
            {
                ActivePreviewType = "audio";
                IsVideoPlaybackAvailable = true;
                IsVideoPlaying = false;
                VideoPosition = TimeSpan.Zero;

                var durationTask = VideoThumbnailService.Instance.GetVideoDurationAsync(filePath, token);
                _ = durationTask.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result > TimeSpan.Zero && generation == _previewGeneration && _currentFilePath == filePath)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (generation == _previewGeneration && _currentFilePath == filePath)
                            {
                                VideoDuration = t.Result;
                            }
                        });
                    }
                }, token);
            }
            // 3. PDF Preview
            else if (PdfPreviewService.Instance.IsPdfFile(filePath))
            {
                ActivePreviewType = "pdf";
                var pdfInfo = await PdfPreviewService.Instance.GetPdfInfoAsync(filePath, token);
                if (token.IsCancellationRequested || generation != _previewGeneration || _currentFilePath != filePath) return;

                if (pdfInfo.IsValid && pdfInfo.PageCount > 0)
                {
                    PdfTotalPages = pdfInfo.PageCount;
                    PdfCurrentPage = 1;

                    var pageBmp = await PdfPreviewService.Instance.RenderPageAsync(filePath, 0, 1200, token);
                    if (token.IsCancellationRequested || generation != _previewGeneration || _currentFilePath != filePath) return;
                    PdfCurrentPageBitmap = pageBmp;
                }
                else
                {
                    PreviewErrorMessage = pdfInfo.ErrorMessage ?? "Failed to load PDF document.";
                    ActivePreviewType = "metadata";
                }
            }
            // 4. ZIP / RAR / Archive Preview
            else if (ZipPreviewService.Instance.IsArchiveFile(filePath))
            {
                ActivePreviewType = "zip";
                var zipResult = await ZipPreviewService.Instance.LoadArchivePreviewAsync(filePath, token);
                if (token.IsCancellationRequested || generation != _previewGeneration || _currentFilePath != filePath) return;

                if (zipResult.Success)
                {
                    ZipEntries = new ObservableCollection<ZipEntryItem>(zipResult.Entries);
                    ZipSummaryDisplay = $"{zipResult.TotalFileCount} items • {zipResult.FormattedTotalSize} • {zipResult.OverallRatio}";
                }
                else
                {
                    PreviewErrorMessage = zipResult.ErrorMessage ?? "Unable to inspect archive.";
                    ActivePreviewType = "metadata";
                }
            }
            // 5. 3D Model (STL) Preview
            else if (StlPreviewService.Instance.IsStlFile(filePath))
            {
                ActivePreviewType = "stl";
                var stlResult = await StlPreviewService.Instance.LoadStlAsync(filePath, token);
                if (token.IsCancellationRequested || generation != _previewGeneration || _currentFilePath != filePath) return;

                if (stlResult.Success && stlResult.Model != null)
                {
                    StlModel = stlResult.Model;
                    StlDimensionsDisplay = stlResult.Model.FormattedDimensions;
                    StlTrianglesDisplay = stlResult.Model.FormattedTriangleCount;
                    StlYaw = 45f;
                    StlPitch = -25f;
                    StlZoom = 1.0f;
                    StlPan = System.Numerics.Vector2.Zero;
                    StlWireframe = false;

                    await RenderCurrentStlAsync();
                }
                else
                {
                    PreviewErrorMessage = stlResult.ErrorMessage ?? "Unable to preview this STL file.";
                    ActivePreviewType = "metadata";
                }
            }
            // 6. Text / Binary / Directory Fallback
            else if (data.PreviewType == "text")
            {
                ActivePreviewType = "text";
            }
            else
            {
                ActivePreviewType = "metadata";
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

    // ==========================================
    // VIDEO CONTROLS
    // ==========================================

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsVideoPlaying)
        {
            PauseVideo();
        }
        else
        {
            PlayVideo();
        }
    }

    public void PlayVideo()
    {
        if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;

        if (!_hasVideoMedia)
        {
            bool ok = _videoPlayer.Open(_currentFilePath);
            if (!ok)
            {
                IsVideoPlaybackAvailable = false;
                return;
            }
            _hasVideoMedia = true;

            if (VideoPosition > TimeSpan.Zero)
            {
                _videoPlayer.SetPosition(VideoPosition);
            }
        }

        _videoPlayer.SetMute(IsVideoMuted);
        _videoPlayer.SetVolume(IsVideoMuted ? 0.0 : VideoVolume);
        _videoPlayer.Play();
        IsVideoPlaying = true;
        IsVideoSessionActive = true;
        OnPropertyChanged(nameof(VlcMediaPlayer));
    }

    public void PauseVideo()
    {
        if (_videoPlayer.IsInitialized)
        {
            _videoPlayer.Pause();
        }
        IsVideoPlaying = false;
        OnPropertyChanged(nameof(VlcMediaPlayer));
    }

    public void StopVideo()
    {
        _videoPlayer.Stop();
        _videoPlayer.Close();
        _hasVideoMedia = false;
        IsVideoPlaying = false;
        IsVideoSessionActive = false;
        VideoPosition = TimeSpan.Zero;
        OnPropertyChanged(nameof(VlcMediaPlayer));
    }

    [RelayCommand]
    public void SeekVideo(double seconds)
    {
        var target = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, VideoDuration.TotalSeconds));
        _isSeeking = true;
        VideoPosition = target;

        if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath))
        {
            _isSeeking = false;
            return;
        }

        // If media isn't loaded yet, open it
        if (!_hasVideoMedia)
        {
            bool ok = _videoPlayer.Open(_currentFilePath);
            if (!ok)
            {
                _isSeeking = false;
                return;
            }
            _hasVideoMedia = true;
        }

        IsVideoSessionActive = true;

        if (!IsVideoPlaying)
        {
            // Start + immediately pause so the player is in a seekable state
            _videoPlayer.Play();
            _videoPlayer.Pause();
        }

        _videoPlayer.SetPosition(target);
        _isSeeking = false;
    }

    [RelayCommand]
    public void ToggleMute()
    {
        if (IsVideoMuted)
        {
            // Unmute
            IsVideoMuted = false;
            if (VideoVolume <= 0.01)
            {
                VideoVolume = 0.5;
            }
            _videoPlayer.SetMute(false);
            _videoPlayer.SetVolume(VideoVolume);
            VideoPreferencesService.Instance.SetMuted(false);
            VideoPreferencesService.Instance.SetVolume(VideoVolume);
        }
        else
        {
            // Mute
            IsVideoMuted = true;
            _videoPlayer.SetMute(true);
            VideoPreferencesService.Instance.SetMuted(true);
        }
    }

    partial void OnVideoVolumeChanged(double value)
    {
        if (value <= 0.001)
        {
            // Far left: automatically mute
            if (!IsVideoMuted)
            {
                IsVideoMuted = true;
                _videoPlayer.SetMute(true);
                VideoPreferencesService.Instance.SetMuted(true);
            }
            _videoPlayer.SetVolume(0.0);
            VideoPreferencesService.Instance.SetVolume(0.0);
        }
        else
        {
            // Non-zero: automatically unmute
            if (IsVideoMuted)
            {
                IsVideoMuted = false;
                _videoPlayer.SetMute(false);
                VideoPreferencesService.Instance.SetMuted(false);
            }
            _videoPlayer.SetVolume(value);
            VideoPreferencesService.Instance.SetVolume(value);
        }
    }

    // ==========================================
    // PDF CONTROLS
    // ==========================================

    [RelayCommand]
    public async Task NextPdfPageAsync()
    {
        if (PdfCurrentPage < PdfTotalPages && !string.IsNullOrEmpty(_currentFilePath))
        {
            PdfCurrentPage++;
            await RenderCurrentPdfPageAsync();
        }
    }

    [RelayCommand]
    public async Task PrevPdfPageAsync()
    {
        if (PdfCurrentPage > 1 && !string.IsNullOrEmpty(_currentFilePath))
        {
            PdfCurrentPage--;
            await RenderCurrentPdfPageAsync();
        }
    }

    [RelayCommand]
    public void TogglePdfFitWidth()
    {
        IsPdfFitWidth = !IsPdfFitWidth;
    }

    private async Task RenderCurrentPdfPageAsync()
    {
        if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;
        try
        {
            var pageBmp = await PdfPreviewService.Instance.RenderPageAsync(_currentFilePath, PdfCurrentPage - 1, 1400);
            if (pageBmp != null)
            {
                PdfCurrentPageBitmap = pageBmp;
            }
        }
        catch { }
    }

    // ==========================================
    // IMAGE ZOOM CONTROLS
    // ==========================================

    [RelayCommand]
    public void ZoomIn()
    {
        IsFitMode = false;
        ZoomLevel = Math.Clamp(ZoomLevel * 1.25, 0.1, 10.0);
    }

    [RelayCommand]
    public void ZoomOut()
    {
        IsFitMode = false;
        ZoomLevel = Math.Clamp(ZoomLevel / 1.25, 0.1, 10.0);
    }

    [RelayCommand]
    public void SetPdfZoom(double scale)
    {
        ZoomLevel = Math.Clamp(scale, 0.25, 4.0);
        IsFitMode = false;
    }

    // ==========================================
    // 3D / STL CONTROLS
    // ==========================================

    public async Task RenderCurrentStlAsync()
    {
        if (StlModel == null) return;
        var bmp = await StlPreviewService.Instance.RenderPreviewAsync(
            StlModel, 640, 640, StlYaw, StlPitch, StlZoom, StlPan, StlWireframe);
        if (bmp != null)
        {
            StlBitmap = bmp;
        }
    }

    public async Task RotateStlAsync(double deltaYaw, double deltaPitch)
    {
        if (StlModel == null) return;
        StlYaw = (float)((StlYaw + deltaYaw) % 360.0);
        StlPitch = Math.Clamp((float)(StlPitch + deltaPitch), -89f, 89f);
        await RenderCurrentStlAsync();
    }

    public async Task PanStlAsync(double deltaX, double deltaY)
    {
        if (StlModel == null) return;
        StlPan = new System.Numerics.Vector2((float)(StlPan.X + deltaX), (float)(StlPan.Y + deltaY));
        await RenderCurrentStlAsync();
    }

    [RelayCommand]
    public async Task ZoomStlInAsync()
    {
        if (StlModel == null) return;
        StlZoom = Math.Clamp(StlZoom * 1.25f, 0.05f, 20f);
        await RenderCurrentStlAsync();
    }

    [RelayCommand]
    public async Task ZoomStlOutAsync()
    {
        if (StlModel == null) return;
        StlZoom = Math.Clamp(StlZoom / 1.25f, 0.05f, 20f);
        await RenderCurrentStlAsync();
    }

    [RelayCommand]
    public async Task ResetStlViewAsync()
    {
        if (StlModel == null) return;
        StlYaw = 45f;
        StlPitch = -25f;
        StlZoom = 1.0f;
        StlPan = System.Numerics.Vector2.Zero;
        await RenderCurrentStlAsync();
    }

    [RelayCommand]
    public async Task ToggleStlWireframeAsync()
    {
        if (StlModel == null) return;
        StlWireframe = !StlWireframe;
        await RenderCurrentStlAsync();
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
            ActualSize();
        }
        else
        {
            ResetZoom();
        }
    }

    // ==========================================
    // HASHING CONTROLS
    // ==========================================

    [RelayCommand]
    public async Task ComputeHashesAsync()
    {
        if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath) || IsHashing) return;

        _hashingCts?.Cancel();
        _hashingCts = new CancellationTokenSource();
        var token = _hashingCts.Token;

        IsHashing = true;
        HasHashes = false;
        Sha256Hash = "Computing...";
        Md5Hash = "Computing...";

        try
        {
            var (sha256, md5) = await Task.Run(() =>
            {
                using var stream = new FileStream(_currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sha256Alg = System.Security.Cryptography.SHA256.Create();
                using var md5Alg = System.Security.Cryptography.MD5.Create();

                byte[] buffer = new byte[64 * 1024];
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    sha256Alg.TransformBlock(buffer, 0, bytesRead, null, 0);
                    md5Alg.TransformBlock(buffer, 0, bytesRead, null, 0);
                }

                sha256Alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                md5Alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                return (
                    Convert.ToHexString(sha256Alg.Hash ?? Array.Empty<byte>()).ToLowerInvariant(),
                    Convert.ToHexString(md5Alg.Hash ?? Array.Empty<byte>()).ToLowerInvariant()
                );
            }, token);

            if (!token.IsCancellationRequested)
            {
                Sha256Hash = sha256;
                Md5Hash = md5;
                HasHashes = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Sha256Hash = $"Error: {ex.Message}";
            Md5Hash = $"Error: {ex.Message}";
            HasHashes = true;
        }
        finally
        {
            IsHashing = false;
        }
    }

    private static string FormatTime(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _previewCts?.Cancel();
        _hashingCts?.Cancel();
        _videoPlayer.Dispose();
    }
}
