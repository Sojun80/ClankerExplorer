using System;

namespace ClankerExplorer.Platform;

public readonly record struct MouseWheelSettings(
    int ScrollLines,
    bool IsPageScroll);

public interface IScrollSettingsProvider
{
    MouseWheelSettings GetMouseWheelSettings();
}

public sealed class DefaultScrollSettingsProvider : IScrollSettingsProvider
{
    public static DefaultScrollSettingsProvider Instance { get; } = new();

    public MouseWheelSettings GetMouseWheelSettings() => new(3, false);
}

public static class ScrollSettings
{
    private static IScrollSettingsProvider? _current;

    public static IScrollSettingsProvider Current
    {
        get => _current ?? (OperatingSystem.IsWindows()
            ? WindowsScrollSettingsProvider.Instance
            : DefaultScrollSettingsProvider.Instance);
        set => _current = value;
    }
}
