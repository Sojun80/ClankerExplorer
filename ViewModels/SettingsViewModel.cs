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
    [NotifyPropertyChangedFor(nameof(IsStartupRestoreSession))]
    [NotifyPropertyChangedFor(nameof(IsStartupOpenPinned))]
    [NotifyPropertyChangedFor(nameof(IsStartupOpenDefault))]
    private string _startupBehavior = "RestoreSession";

    public bool IsStartupRestoreSession
    {
        get => StartupBehavior == "RestoreSession";
        set { if (value) StartupBehavior = "RestoreSession"; }
    }

    public bool IsStartupOpenPinned
    {
        get => StartupBehavior == "OpenPinned";
        set { if (value) StartupBehavior = "OpenPinned"; }
    }

    public bool IsStartupOpenDefault
    {
        get => StartupBehavior == "OpenDefaultPath";
        set { if (value) StartupBehavior = "OpenDefaultPath"; }
    }

    [ObservableProperty]
    private double _tabWidth = 150.0;

    [ObservableProperty]
    private string _viewMode = "Details";

    [ObservableProperty]
    private double _thumbnailSize = 144.0;

    [ObservableProperty]
    private int _maxTabsRestoredOnStartup = 8;

    [ObservableProperty]
    private int _maxTabsAllowed = 30;

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

    // Smart Column Sizing & Custom Widths
    [ObservableProperty]
    private bool _smartColumnSizing = true;

    [ObservableProperty]
    private double _columnWidthName = 280;

    [ObservableProperty]
    private double _columnWidthExt = 65;

    [ObservableProperty]
    private double _columnWidthSize = 95;

    [ObservableProperty]
    private double _columnWidthDateModified = 150;

    [ObservableProperty]
    private double _columnWidthDateCreated = 150;

    [ObservableProperty]
    private double _columnWidthDateAccessed = 150;

    [ObservableProperty]
    private double _columnWidthItemType = 110;

    [ObservableProperty]
    private double _columnWidthAttributes = 90;

    [ObservableProperty]
    private double _columnWidthPermissions = 110;

    [ObservableProperty]
    private double _columnWidthOwnerGroup = 110;

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
        StartupBehavior = string.IsNullOrWhiteSpace(s.StartupBehavior) ? "RestoreSession" : s.StartupBehavior;
        TabWidth = double.IsFinite(s.TabWidth) && s.TabWidth >= 80 && s.TabWidth <= 280
            ? s.TabWidth
            : 150.0;
        ViewMode = s.ViewMode == "Thumbnails" ? "Thumbnails" : "Details";
        ThumbnailSize = s.ThumbnailSize >= 64 && s.ThumbnailSize <= 320 ? s.ThumbnailSize : 144.0;
        MaxTabsRestoredOnStartup = s.MaxTabsRestoredOnStartup > 0 ? s.MaxTabsRestoredOnStartup : 8;
        MaxTabsAllowed = s.MaxTabsAllowed > 0 ? s.MaxTabsAllowed : 30;
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

        SmartColumnSizing = s.SmartColumnSizing;
        ColumnWidthName = s.ColumnWidthName > 0 ? s.ColumnWidthName : 280;
        ColumnWidthExt = s.ColumnWidthExt > 0 ? s.ColumnWidthExt : 65;
        ColumnWidthSize = s.ColumnWidthSize > 0 ? s.ColumnWidthSize : 95;
        ColumnWidthDateModified = s.ColumnWidthDateModified > 0 ? s.ColumnWidthDateModified : 150;
        ColumnWidthDateCreated = s.ColumnWidthDateCreated > 0 ? s.ColumnWidthDateCreated : 150;
        ColumnWidthDateAccessed = s.ColumnWidthDateAccessed > 0 ? s.ColumnWidthDateAccessed : 150;
        ColumnWidthItemType = s.ColumnWidthItemType > 0 ? s.ColumnWidthItemType : 110;
        ColumnWidthAttributes = s.ColumnWidthAttributes > 0 ? s.ColumnWidthAttributes : 90;
        ColumnWidthPermissions = s.ColumnWidthPermissions > 0 ? s.ColumnWidthPermissions : 110;
        ColumnWidthOwnerGroup = s.ColumnWidthOwnerGroup > 0 ? s.ColumnWidthOwnerGroup : 110;
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
            StartupBehavior = StartupBehavior,
            TabWidth = TabWidth,
            ViewMode = ViewMode,
            ThumbnailSize = ThumbnailSize,
            MaxTabsRestoredOnStartup = MaxTabsRestoredOnStartup,
            MaxTabsAllowed = MaxTabsAllowed,
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
            ShowColumnOwnerGroup = ShowColumnOwnerGroup,
            SmartColumnSizing = SmartColumnSizing,
            ColumnWidthName = ColumnWidthName,
            ColumnWidthExt = ColumnWidthExt,
            ColumnWidthSize = ColumnWidthSize,
            ColumnWidthDateModified = ColumnWidthDateModified,
            ColumnWidthDateCreated = ColumnWidthDateCreated,
            ColumnWidthDateAccessed = ColumnWidthDateAccessed,
            ColumnWidthItemType = ColumnWidthItemType,
            ColumnWidthAttributes = ColumnWidthAttributes,
            ColumnWidthPermissions = ColumnWidthPermissions,
            ColumnWidthOwnerGroup = ColumnWidthOwnerGroup
        };
    }

    public AppSettings GetOriginalState() => _originalSettings;

    public void ReloadFromCurrent()
    {
        LoadFromSettings(SettingsService.Instance.CurrentSettings);
        TriggerLivePreview();
    }

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
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    [RelayCommand]
    public void OpenGitHub()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Sojun80/ClankerExplorer",
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    public void OpenGitHubIssues()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Sojun80/ClankerExplorer/issues",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
