using System;
using System.Runtime.InteropServices;

namespace ClankerExplorer.Models;

public class AppSettings
{
    // Theme Presets: "CyberDark", "MidnightNavy", "MatrixEmerald", "MonokaiCharcoal", "NordicSlate", "DraculaViolet", "TokyoNight", "SolarizedDark", "GruvboxDark", "OLEDBlack", "CyberpunkNeon"
    public string ThemePreset { get; set; } = "CyberDark";

    // Custom Color Palette (Hex)
    public string BackgroundColor { get; set; } = "#080C14";
    public string SurfaceColor { get; set; } = "#0F1626";
    public string BorderColor { get; set; } = "#1E293B";
    public string AccentColor { get; set; } = "#0284C7";
    public string HighlightColor { get; set; } = "#38BDF8";
    public string TextColor { get; set; } = "#F8FAFC";
    public string SecondaryTextColor { get; set; } = "#94A3B8";

    // Typography
    public string UiFontFamily { get; set; } = "Inter, Segoe UI, sans-serif";
    public string MonoFontFamily { get; set; } = "Consolas, JetBrains Mono, Fira Code, monospace";
    public double BaseFontSize { get; set; } = 12.0;
    public double DataGridRowHeight { get; set; } = 28.0;

    // Preferences & Behavior
    public string DefaultPath { get; set; } = OperatingSystem.IsWindows() ? @"C:\" : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string StartupBehavior { get; set; } = "RestoreSession"; // "RestoreSession", "OpenPinned", "OpenDefaultPath"
    public int MaxTabsRestoredOnStartup { get; set; } = 8;
    public int MaxTabsAllowed { get; set; } = 30;
    public double InspectorWidth { get; set; } = 320.0;
    public bool StartInDualPane { get; set; } = false;
    public bool ShowInspectorOnStartup { get; set; } = true;
    public bool ConfirmBeforeDelete { get; set; } = true;

    // Configurable Grid Columns (Cross-Platform Windows & Linux)
    public bool ShowColumnExt { get; set; } = true;
    public bool ShowColumnSize { get; set; } = true;
    public bool ShowColumnDateModified { get; set; } = true;
    public bool ShowColumnDateCreated { get; set; } = true;
    public bool ShowColumnDateAccessed { get; set; } = false;
    public bool ShowColumnAttributes { get; set; } = true;
    public bool ShowColumnItemType { get; set; } = false;
    public bool ShowColumnPermissions { get; set; } = false; // Linux/POSIX Permissions (e.g. rwxr-xr-x / 0755)
    public bool ShowColumnOwnerGroup { get; set; } = false;  // Linux/POSIX User & Group

    // Smart Column Sizing & Custom Widths
    public bool SmartColumnSizing { get; set; } = true;
    public double ColumnWidthName { get; set; } = 280;
    public double ColumnWidthExt { get; set; } = 65;
    public double ColumnWidthSize { get; set; } = 95;
    public double ColumnWidthDateModified { get; set; } = 150;
    public double ColumnWidthDateCreated { get; set; } = 150;
    public double ColumnWidthDateAccessed { get; set; } = 150;
    public double ColumnWidthItemType { get; set; } = 110;
    public double ColumnWidthAttributes { get; set; } = 90;
    public double ColumnWidthPermissions { get; set; } = 110;
    public double ColumnWidthOwnerGroup { get; set; } = 110;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            ThemePreset = this.ThemePreset,
            BackgroundColor = this.BackgroundColor,
            SurfaceColor = this.SurfaceColor,
            BorderColor = this.BorderColor,
            AccentColor = this.AccentColor,
            HighlightColor = this.HighlightColor,
            TextColor = this.TextColor,
            SecondaryTextColor = this.SecondaryTextColor,
            UiFontFamily = this.UiFontFamily,
            MonoFontFamily = this.MonoFontFamily,
            BaseFontSize = this.BaseFontSize,
            DataGridRowHeight = this.DataGridRowHeight,
            DefaultPath = this.DefaultPath,
            StartupBehavior = this.StartupBehavior,
            MaxTabsRestoredOnStartup = this.MaxTabsRestoredOnStartup,
            MaxTabsAllowed = this.MaxTabsAllowed,
            InspectorWidth = this.InspectorWidth,
            StartInDualPane = this.StartInDualPane,
            ShowInspectorOnStartup = this.ShowInspectorOnStartup,
            ConfirmBeforeDelete = this.ConfirmBeforeDelete,
            ShowColumnExt = this.ShowColumnExt,
            ShowColumnSize = this.ShowColumnSize,
            ShowColumnDateModified = this.ShowColumnDateModified,
            ShowColumnDateCreated = this.ShowColumnDateCreated,
            ShowColumnDateAccessed = this.ShowColumnDateAccessed,
            ShowColumnAttributes = this.ShowColumnAttributes,
            ShowColumnItemType = this.ShowColumnItemType,
            ShowColumnPermissions = this.ShowColumnPermissions,
            ShowColumnOwnerGroup = this.ShowColumnOwnerGroup,
            SmartColumnSizing = this.SmartColumnSizing,
            ColumnWidthName = this.ColumnWidthName,
            ColumnWidthExt = this.ColumnWidthExt,
            ColumnWidthSize = this.ColumnWidthSize,
            ColumnWidthDateModified = this.ColumnWidthDateModified,
            ColumnWidthDateCreated = this.ColumnWidthDateCreated,
            ColumnWidthDateAccessed = this.ColumnWidthDateAccessed,
            ColumnWidthItemType = this.ColumnWidthItemType,
            ColumnWidthAttributes = this.ColumnWidthAttributes,
            ColumnWidthPermissions = this.ColumnWidthPermissions,
            ColumnWidthOwnerGroup = this.ColumnWidthOwnerGroup
        };
    }
}
