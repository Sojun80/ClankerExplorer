using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClankerExplorer.Services;

namespace ClankerExplorer.Views;

public partial class VideoThumbnailTimeWindow : Window
{
    private readonly string _filePath;
    private TimeSpan _videoDuration = TimeSpan.Zero;

    public TimeSpan TargetTimeSpan { get; private set; } = TimeSpan.Zero;

    public VideoThumbnailTimeWindow()
    {
        InitializeComponent();
        _filePath = string.Empty;
    }

    public VideoThumbnailTimeWindow(string filePath) : this()
    {
        _filePath = filePath;
        TxtFileName.Text = Path.GetFileName(filePath);

        Loaded += async (s, e) =>
        {
            TxtTimeInput.Focus();
            await LoadDurationAsync();
        };
    }

    private async System.Threading.Tasks.Task LoadDurationAsync()
    {
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;

        try
        {
            var duration = await VideoThumbnailService.Instance.GetVideoDurationAsync(_filePath);
            if (duration > TimeSpan.Zero)
            {
                _videoDuration = duration;
                TxtDuration.Text = $"Duration: {FormatTimeSpan(duration)}";

                // Default input to 25% of duration
                var defaultTime = TimeSpan.FromTicks((long)(duration.Ticks * 0.25));
                TxtTimeInput.Text = FormatTimeSpan(defaultTime);
                TxtTimeInput.SelectAll();
            }
            else
            {
                TxtDuration.Text = "Duration: Unknown";
                TxtTimeInput.Text = "00:15";
                TxtTimeInput.SelectAll();
            }
        }
        catch
        {
            TxtDuration.Text = "Duration: Unknown";
            TxtTimeInput.Text = "00:15";
            TxtTimeInput.SelectAll();
        }
    }

    private void OnPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ratio))
        {
            var dur = _videoDuration > TimeSpan.Zero ? _videoDuration : TimeSpan.FromSeconds(60);
            var target = TimeSpan.FromTicks((long)(dur.Ticks * ratio));
            TxtTimeInput.Text = FormatTimeSpan(target);
            TxtTimeInput.Focus();
            TxtTimeInput.SelectAll();
            TxtError.IsVisible = false;
        }
    }

    private void OnTimeInputKeyDown(object? sender, KeyEventArgs e)
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

    private void OnGenerateClicked(object? sender, RoutedEventArgs e)
    {
        Confirm();
    }

    private void Confirm()
    {
        string text = TxtTimeInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            TxtError.Text = "Please enter a timestamp (e.g. 01:30 or 45).";
            TxtError.IsVisible = true;
            return;
        }

        if (!VideoThumbnailService.TryParseTimestamp(text, out var timeSpan))
        {
            TxtError.Text = "Invalid time format. Use mm:ss (e.g. 01:30), hh:mm:ss (e.g. 01:15:00), or seconds.";
            TxtError.IsVisible = true;
            return;
        }

        if (timeSpan < TimeSpan.Zero)
        {
            TxtError.Text = "Timestamp cannot be negative.";
            TxtError.IsVisible = true;
            return;
        }

        if (_videoDuration > TimeSpan.Zero && timeSpan > _videoDuration)
        {
            TxtError.Text = $"Timestamp exceeds video duration ({FormatTimeSpan(_videoDuration)}).";
            TxtError.IsVisible = true;
            return;
        }

        TargetTimeSpan = timeSpan;
        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
