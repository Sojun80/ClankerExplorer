using System;

namespace ClankerExplorer.Models;

public class DriveModel
{
    public string Letter { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string DriveType { get; set; } = "Fixed";
    public bool IsNetworkDrive { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double PercentUsed => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100.0 : 0.0;
    public string FormattedTotal { get; set; } = string.Empty;
    public string FormattedFree { get; set; } = string.Empty;
    public string FormattedUsed { get; set; } = string.Empty;

    public string DriveTypeTag => IsNetworkDrive ? "NETWORK" : "LOCAL";
    public string IconSymbol => IsNetworkDrive ? "🌐" : "💾";

    public string DisplayName
    {
        get
        {
            if (IsNetworkDrive)
            {
                return string.IsNullOrWhiteSpace(VolumeLabel) ? $"{Letter} (Network Share)" : $"{Letter} {VolumeLabel}";
            }
            return string.IsNullOrWhiteSpace(VolumeLabel) ? $"{Letter} (Fixed)" : $"{Letter} {VolumeLabel}";
        }
    }
}
