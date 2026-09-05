using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public class BackupProfileData
{
    public string AppVersion { get; set; } = "1.0";
    public DateTime ExportedDate { get; set; } = DateTime.Now;
    public string ExportedPlatform { get; set; } = OperatingSystem.IsWindows() ? "Windows" : (OperatingSystem.IsLinux() ? "Linux" : "macOS");
    public AppSettings Settings { get; set; } = new();
    public List<FolderHistoryEntry> FolderHistory { get; set; } = new();
}

public class SettingsService
{
    public static SettingsService Instance { get; } = new();

    public static readonly string[] PresetNames = new[]
    {
        "CyberDark", "MidnightNavy", "MatrixEmerald", "MonokaiCharcoal",
        "NordicSlate", "DraculaViolet", "TokyoNight", "SolarizedDark",
        "GruvboxDark", "OLEDBlack", "CyberpunkNeon"
    };

    private readonly string _settingsFilePath;
    private readonly string _portableSettingsFilePath;

    public AppSettings CurrentSettings { get; private set; } = new();

    public string SettingsDirectory => Path.GetDirectoryName(_settingsFilePath) ?? "";
    public string ActiveSettingsPath => File.Exists(_portableSettingsFilePath) ? _portableSettingsFilePath : _settingsFilePath;
    public string SettingsFilePath => ActiveSettingsPath;

    public event Action<AppSettings>? SettingsChanged;

    public SettingsService(string? dataDirectory = null)
    {
        var settingsDir = AppStoragePaths.GetDataDirectory(dataDirectory);
        _settingsFilePath = Path.Combine(settingsDir, "settings.json");

        _portableSettingsFilePath = AppStoragePaths.GetPortableFilePath("settings.json", dataDirectory);

        LoadSettings();
    }

    public void LoadSettings()
    {
        try
        {
            string targetPath = File.Exists(_portableSettingsFilePath) ? _portableSettingsFilePath : _settingsFilePath;
            if (File.Exists(targetPath))
            {
                string json = File.ReadAllText(targetPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    CurrentSettings = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            CurrentSettings = new AppSettings();
        }
    }

    public void SaveSettings(AppSettings? settings = null)
    {
        if (settings != null) CurrentSettings = settings;

        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(CurrentSettings, options);

            string targetPath = File.Exists(_portableSettingsFilePath) ? _portableSettingsFilePath : _settingsFilePath;
            File.WriteAllText(targetPath, json);

            SettingsChanged?.Invoke(CurrentSettings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    public void UpdateSettings(Action<AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        update(CurrentSettings);
        SaveSettings();
    }

    public void ExportSettings(string destinationFilePath)
    {
        ExportFullBackup(destinationFilePath);
    }

    public void ImportSettings(string sourceFilePath)
    {
        ImportFullBackup(sourceFilePath);
    }

    public void ExportFullBackup(string destinationFilePath)
    {
        var bundle = new BackupProfileData
        {
            Settings = CurrentSettings,
            FolderHistory = HistoryService.Instance.GetAllHistoryEntries()
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(bundle, options);
        File.WriteAllText(destinationFilePath, json);
    }

    public void ImportFullBackup(string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath)) return;
        string json = File.ReadAllText(sourceFilePath);

        try
        {
            var bundle = JsonSerializer.Deserialize<BackupProfileData>(json);
            if (bundle?.Settings != null)
            {
                SanitizeCrossPlatformPaths(bundle.Settings);
                CurrentSettings = bundle.Settings;
                SaveSettings(CurrentSettings);

                if (bundle.FolderHistory != null && bundle.FolderHistory.Count > 0)
                {
                    HistoryService.Instance.ImportHistoryEntries(bundle.FolderHistory);
                }
                return;
            }
        }
        catch { }

        // Fallback: try raw AppSettings JSON import
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded != null)
            {
                SanitizeCrossPlatformPaths(loaded);
                CurrentSettings = loaded;
                SaveSettings(CurrentSettings);
            }
        }
        catch { }
    }

    private static void SanitizeCrossPlatformPaths(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.DefaultPath)) return;

        if (OperatingSystem.IsWindows() && s.DefaultPath.StartsWith("/") && !s.DefaultPath.StartsWith(@"\\"))
        {
            s.DefaultPath = @"C:\";
        }
        else if (!OperatingSystem.IsWindows() && s.DefaultPath.Contains(':'))
        {
            s.DefaultPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    public static AppSettings GetPreset(string presetName)
    {
        var settings = new AppSettings { ThemePreset = presetName };
        switch (presetName)
        {
            case "CyberDark":
                settings.BackgroundColor = "#080C14";
                settings.SurfaceColor = "#0F1626";
                settings.BorderColor = "#1E293B";
                settings.AccentColor = "#0284C7";
                settings.HighlightColor = "#38BDF8";
                settings.TextColor = "#F8FAFC";
                break;

            case "MidnightNavy":
                settings.BackgroundColor = "#070B19";
                settings.SurfaceColor = "#0E172E";
                settings.BorderColor = "#1E293B";
                settings.AccentColor = "#4F46E5";
                settings.HighlightColor = "#818CF8";
                settings.TextColor = "#F8FAFC";
                break;

            case "MatrixEmerald":
                settings.BackgroundColor = "#040D09";
                settings.SurfaceColor = "#0B1E17";
                settings.BorderColor = "#065F46";
                settings.AccentColor = "#059669";
                settings.HighlightColor = "#34D399";
                settings.TextColor = "#ECFDF5";
                break;

            case "MonokaiCharcoal":
                settings.BackgroundColor = "#121212";
                settings.SurfaceColor = "#1E1E1E";
                settings.BorderColor = "#333333";
                settings.AccentColor = "#D97706";
                settings.HighlightColor = "#FBBF24";
                settings.TextColor = "#F5F5F5";
                break;

            case "NordicSlate":
                settings.BackgroundColor = "#0D1117";
                settings.SurfaceColor = "#161B22";
                settings.BorderColor = "#30363D";
                settings.AccentColor = "#0D9488";
                settings.HighlightColor = "#2DD4BF";
                settings.TextColor = "#F0F6FC";
                break;

            case "DraculaViolet":
                settings.BackgroundColor = "#1E1F29";
                settings.SurfaceColor = "#282A36";
                settings.BorderColor = "#44475A";
                settings.AccentColor = "#BD93F9";
                settings.HighlightColor = "#FF79C6";
                settings.TextColor = "#F8F8F2";
                break;

            case "TokyoNight":
                settings.BackgroundColor = "#16161E";
                settings.SurfaceColor = "#1A1B26";
                settings.BorderColor = "#292E42";
                settings.AccentColor = "#7AA2F7";
                settings.HighlightColor = "#7DCFFF";
                settings.TextColor = "#C0CAF5";
                break;

            case "SolarizedDark":
                settings.BackgroundColor = "#00212B";
                settings.SurfaceColor = "#002B36";
                settings.BorderColor = "#073642";
                settings.AccentColor = "#268BD2";
                settings.HighlightColor = "#2AA198";
                settings.TextColor = "#839496";
                break;

            case "GruvboxDark":
                settings.BackgroundColor = "#1D2021";
                settings.SurfaceColor = "#282828";
                settings.BorderColor = "#3C3836";
                settings.AccentColor = "#D65D0E";
                settings.HighlightColor = "#FABD2F";
                settings.TextColor = "#EBDBB2";
                break;

            case "OLEDBlack":
                settings.BackgroundColor = "#000000";
                settings.SurfaceColor = "#0A0A0A";
                settings.BorderColor = "#222222";
                settings.AccentColor = "#0070F3";
                settings.HighlightColor = "#38BDF8";
                settings.TextColor = "#FFFFFF";
                break;

            case "CyberpunkNeon":
                settings.BackgroundColor = "#0D0221";
                settings.SurfaceColor = "#190B28";
                settings.BorderColor = "#3A175C";
                settings.AccentColor = "#FF007F";
                settings.HighlightColor = "#FFE600";
                settings.TextColor = "#F8FAFC";
                break;
        }
        return settings;
    }

    public void ApplyPreset(string presetName, AppSettings settings)
    {
        var preset = GetPreset(presetName);
        settings.ThemePreset = preset.ThemePreset;
        settings.BackgroundColor = preset.BackgroundColor;
        settings.SurfaceColor = preset.SurfaceColor;
        settings.BorderColor = preset.BorderColor;
        settings.AccentColor = preset.AccentColor;
        settings.HighlightColor = preset.HighlightColor;
        settings.TextColor = preset.TextColor;
    }
}
