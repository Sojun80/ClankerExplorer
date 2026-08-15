using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _originalSettings;

    public event Action? RequestClose;
    public event Action? RequestExport;
    public event Action? RequestImport;
    public event Action<AppSettings>? RequestPreview;

    public ObservableCollection<string> AvailablePresets { get; }
    public ObservableCollection<string> AvailableFonts { get; }
    public ObservableCollection<string> AvailableMonoFonts { get; }

    [ObservableProperty]
    private string _themePreset = "CyberDark";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundColorPicker))]
    private string _backgroundColor = "#080C14";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SurfaceColorPicker))]
    private string _surfaceColor = "#0F1626";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccentColorPicker))]
    private string _accentColor = "#0284C7";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HighlightColorPicker))]
    private string _highlightColor = "#38BDF8";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BorderColorPicker))]
    private string _borderColor = "#1E293B";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextColorPicker))]
    private string _textColor = "#F8FAFC";

    public Color BackgroundColorPicker
    {
        get => Color.TryParse(BackgroundColor, out var c) ? c : Color.FromRgb(8, 12, 20);
        set
        {
            var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (BackgroundColor != hex)
            {
                BackgroundColor = hex;
            }
        }
    }

    public Color SurfaceColorPicker
    {
        get => Color.TryParse(SurfaceColor, out var c) ? c : Color.FromRgb(15, 22, 38);
        set
        {
            var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (SurfaceColor != hex)
            {
                SurfaceColor = hex;
            }
        }
    }

    public Color AccentColorPicker
    {
        get => Color.TryParse(AccentColor, out var c) ? c : Color.FromRgb(2, 132, 199);
        set
        {
            var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (AccentColor != hex)
            {
                AccentColor = hex;
            }
        }
    }

    public Color HighlightColorPicker
    {
        get => Color.TryParse(HighlightColor, out var c) ? c : Color.FromRgb(56, 189, 248);
        set
        {
            var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (HighlightColor != hex)
            {
                HighlightColor = hex;
            }
        }
    }

    public Color BorderColorPicker
    {
        get => Color.TryParse(BorderColor, out var c) ? c : Color.FromRgb(30, 41, 59);
        set
        {
            var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (BorderColor != hex)
            {
                BorderColor = hex;
            }
        }
    }

    public Color TextColorPicker
    {
        get => Color.TryParse(TextColor, out var c) ? c : Color.FromRgb(248, 250, 252);
        set
        {
            var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (TextColor != hex)
            {
                TextColor = hex;
            }
        }
    }

    [ObservableProperty]
    private string _uiFontFamily = "Inter";

    [ObservableProperty]
    private string _monoFontFamily = "Consolas";

    [ObservableProperty]
    private double _baseFontSize = 12.0;

    [ObservableProperty]
    private double _dataGridRowHeight = 28.0;

    [ObservableProperty]
    private string _defaultPath = @"C:\";

    [ObservableProperty]
    private bool _startInDualPane = false;

    [ObservableProperty]
    private bool _showInspectorOnStartup = true;

    [ObservableProperty]
    private bool _confirmBeforeDelete = true;

    // Configurable Grid Columns
    [ObservableProperty]
    private bool _showColumnExt = true;

    [ObservableProperty]
    private bool _showColumnSize = true;

    [ObservableProperty]
    private bool _showColumnDateModified = true;

    [ObservableProperty]
    private bool _showColumnDateCreated = true;

    [ObservableProperty]
    private bool _showColumnDateAccessed = false;

    [ObservableProperty]
    private bool _showColumnAttributes = true;

    [ObservableProperty]
    private bool _showColumnItemType = false;

    [ObservableProperty]
    private bool _showColumnPermissions = false;

    [ObservableProperty]
    private bool _showColumnOwnerGroup = false;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public string SettingsLocationText => SettingsService.Instance.SettingsFilePath;

    public SettingsViewModel()
    {
        _originalSettings = SettingsService.Instance.CurrentSettings.Clone();

        AvailablePresets = new ObservableCollection<string>(SettingsService.PresetNames);
        AvailableFonts = new ObservableCollection<string>(new[] { "Inter", "Segoe UI", "Segoe UI Variable", "Roboto", "Outfit", "Arial", "Ubuntu" });
        AvailableMonoFonts = new ObservableCollection<string>(new[] { "Consolas", "JetBrains Mono", "Cascadia Code", "Fira Code", "Courier New", "monospace" });

        LoadFromSettings(_originalSettings);
    }

    private void LoadFromSettings(AppSettings s)
    {
        ThemePreset = s.ThemePreset;
        BackgroundColor = s.BackgroundColor;
        SurfaceColor = s.SurfaceColor;
        AccentColor = s.AccentColor;
        HighlightColor = s.HighlightColor;
        BorderColor = s.BorderColor;
        TextColor = s.TextColor;
        UiFontFamily = s.UiFontFamily;
        MonoFontFamily = s.MonoFontFamily;
        BaseFontSize = s.BaseFontSize;
        DataGridRowHeight = s.DataGridRowHeight;
        DefaultPath = s.DefaultPath;
        StartInDualPane = s.StartInDualPane;
        ShowInspectorOnStartup = s.ShowInspectorOnStartup;
        ConfirmBeforeDelete = s.ConfirmBeforeDelete;
        ShowColumnExt = s.ShowColumnExt;
        ShowColumnSize = s.ShowColumnSize;
        ShowColumnDateModified = s.ShowColumnDateModified;
        ShowColumnDateCreated = s.ShowColumnDateCreated;
        ShowColumnDateAccessed = s.ShowColumnDateAccessed;
        ShowColumnAttributes = s.ShowColumnAttributes;
        ShowColumnItemType = s.ShowColumnItemType;
        ShowColumnPermissions = s.ShowColumnPermissions;
        ShowColumnOwnerGroup = s.ShowColumnOwnerGroup;
    }

    partial void OnThemePresetChanged(string value)
    {
        var preset = SettingsService.GetPreset(value);
        BackgroundColor = preset.BackgroundColor;
        SurfaceColor = preset.SurfaceColor;
        AccentColor = preset.AccentColor;
        HighlightColor = preset.HighlightColor;
        BorderColor = preset.BorderColor;
        TextColor = preset.TextColor;

        TriggerLivePreview();
    }

    partial void OnBackgroundColorChanged(string value) => TriggerLivePreview();
    partial void OnSurfaceColorChanged(string value) => TriggerLivePreview();
    partial void OnAccentColorChanged(string value) => TriggerLivePreview();
    partial void OnHighlightColorChanged(string value) => TriggerLivePreview();
    partial void OnBorderColorChanged(string value) => TriggerLivePreview();
    partial void OnTextColorChanged(string value) => TriggerLivePreview();
    partial void OnUiFontFamilyChanged(string value) => TriggerLivePreview();
    partial void OnMonoFontFamilyChanged(string value) => TriggerLivePreview();

    private void TriggerLivePreview()
    {
        var preview = BuildSettingsObject();
        RequestPreview?.Invoke(preview);
    }

    private AppSettings BuildSettingsObject()
    {
        return new AppSettings
        {
            ThemePreset = ThemePreset,
            BackgroundColor = BackgroundColor,
            SurfaceColor = SurfaceColor,
            AccentColor = AccentColor,
            HighlightColor = HighlightColor,
            BorderColor = BorderColor,
            TextColor = TextColor,
            UiFontFamily = UiFontFamily,
            MonoFontFamily = MonoFontFamily,
            BaseFontSize = BaseFontSize,
            DataGridRowHeight = DataGridRowHeight,
            DefaultPath = DefaultPath,
            StartInDualPane = StartInDualPane,
            ShowInspectorOnStartup = ShowInspectorOnStartup,
            ConfirmBeforeDelete = ConfirmBeforeDelete,
            ShowColumnExt = ShowColumnExt,
            ShowColumnSize = ShowColumnSize,
            ShowColumnDateModified = ShowColumnDateModified,
            ShowColumnDateCreated = ShowColumnDateCreated,
            ShowColumnDateAccessed = ShowColumnDateAccessed,
            ShowColumnAttributes = ShowColumnAttributes,
            ShowColumnItemType = ShowColumnItemType,
            ShowColumnPermissions = ShowColumnPermissions,
            ShowColumnOwnerGroup = ShowColumnOwnerGroup
        };
    }

    public AppSettings GetOriginalState() => _originalSettings;

    [RelayCommand]
    public void Save()
    {
        var settings = BuildSettingsObject();
        SettingsService.Instance.SaveSettings(settings);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    public void Cancel()
    {
        RequestPreview?.Invoke(_originalSettings);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    public void ResetDefaults()
    {
        var def = new AppSettings();
        LoadFromSettings(def);
        TriggerLivePreview();
        StatusMessage = "Reset to default settings.";
    }

    [RelayCommand]
    public void TriggerExport() => RequestExport?.Invoke();

    [RelayCommand]
    public void TriggerImport() => RequestImport?.Invoke();

    [RelayCommand]
    public void OpenSettingsFolder()
    {
        var dir = Path.GetDirectoryName(SettingsLocationText);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            FileSystemService.Instance.OpenItem(dir);
        }
    }
}
