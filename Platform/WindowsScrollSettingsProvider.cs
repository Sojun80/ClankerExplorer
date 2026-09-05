using System;
using System.Runtime.InteropServices;

namespace ClankerExplorer.Platform;

public sealed class WindowsScrollSettingsProvider : IScrollSettingsProvider
{
    private const uint SPI_GETWHEELSCROLLLINES = 0x0068;
    private const uint WHEEL_PAGESCROLL = 0xFFFFFFFF;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        out uint pvParam,
        uint fWinIni);

    private static readonly Lazy<WindowsScrollSettingsProvider> _instance = new(() => new WindowsScrollSettingsProvider());
    public static WindowsScrollSettingsProvider Instance => _instance.Value;

    public MouseWheelSettings GetMouseWheelSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MouseWheelSettings(3, false);
        }

        try
        {
            if (SystemParametersInfo(SPI_GETWHEELSCROLLLINES, 0, out uint scrollLines, 0))
            {
                if (scrollLines == WHEEL_PAGESCROLL)
                {
                    return new MouseWheelSettings(0, true);
                }

                return new MouseWheelSettings(Math.Clamp((int)scrollLines, 0, 100), false);
            }
        }
        catch
        {
            // Graceful fallback to standard 3 lines if user32 query fails
        }

        return new MouseWheelSettings(3, false);
    }
}
