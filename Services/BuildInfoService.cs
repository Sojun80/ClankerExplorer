using System;

namespace ClankerExplorer.Services;

public static class BuildInfoService
{
    public static string BuildNumber => BuildInfo.BuildNumber;
    public static string BuildTimestamp => BuildInfo.BuildTimestamp;
    public static string ShortDateTime => BuildInfo.ShortDateTime;
    public static string DisplayString => $"Build #{BuildInfo.BuildNumber} • {BuildInfo.ShortDateTime}";
    public static string TooltipString => $"Build #{BuildInfo.BuildNumber}\nCompiled: {BuildInfo.BuildTimestamp}";

    public static string AppVersion
    {
        get
        {
            var v = typeof(BuildInfoService).Assembly.GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.4.1";
        }
    }

    public static string VersionDisplay => $"v{AppVersion}";
    public static string TitleWithVersion => $"C-Explorer {VersionDisplay}";
}
