using System;

namespace ClankerExplorer.Services;

public static class BuildInfoService
{
    public static string BuildNumber => BuildInfo.BuildNumber;
    public static string BuildTimestamp => BuildInfo.BuildTimestamp;
    public static string ShortDateTime => BuildInfo.ShortDateTime;
    public static string DisplayString => $"Build #{BuildInfo.BuildNumber} • {BuildInfo.ShortDateTime}";
    public static string TooltipString => $"Build #{BuildInfo.BuildNumber}\nCompiled: {BuildInfo.BuildTimestamp}";
}
