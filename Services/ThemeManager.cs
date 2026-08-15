using System;
using Avalonia;
using Avalonia.Media;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public static class ThemeManager
{
    public static void ApplyTheme(AppSettings s)
    {
        if (Application.Current == null) return;
        var res = Application.Current.Resources;

        if (Color.TryParse(s.BackgroundColor, out var bg))
            res["AppBgBrush"] = new SolidColorBrush(bg);

        if (Color.TryParse(s.SurfaceColor, out var surface))
        {
            res["AppSurfaceBrush"] = new SolidColorBrush(surface);
            byte r = (byte)Math.Min(255, surface.R + 25);
            byte g = (byte)Math.Min(255, surface.G + 25);
            byte b = (byte)Math.Min(255, surface.B + 30);
            res["AppSurfaceHoverBrush"] = new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }

        if (Color.TryParse(s.BorderColor, out var border))
            res["AppBorderBrush"] = new SolidColorBrush(border);

        if (Color.TryParse(s.AccentColor, out var accent))
            res["AppAccentBrush"] = new SolidColorBrush(accent);

        if (Color.TryParse(s.HighlightColor, out var highlight))
            res["AppHighlightBrush"] = new SolidColorBrush(highlight);

        if (Color.TryParse(s.TextColor, out var text))
            res["AppTextBrush"] = new SolidColorBrush(text);

        if (!string.IsNullOrWhiteSpace(s.UiFontFamily))
        {
            try { res["AppUiFont"] = new FontFamily(s.UiFontFamily); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(s.MonoFontFamily))
        {
            try { res["AppMonoFont"] = new FontFamily(s.MonoFontFamily); } catch { }
        }
    }
}
