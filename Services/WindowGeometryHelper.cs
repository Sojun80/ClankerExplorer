using System;
using System.Collections.Generic;
using System.Linq;

namespace ClankerExplorer.Services;

public readonly record struct ScreenBounds(int X, int Y, int Width, int Height, bool IsPrimary);

public static class WindowGeometryHelper
{
    public const double DefaultWindowWidth = 1360.0;
    public const double DefaultWindowHeight = 960.0;
    public const double MinWindowWidth = 900.0;
    public const double MinWindowHeight = 500.0;
    public const double MaxAbsurdDimension = 32767.0;
    public const int MinTitleBarIntersectionHeight = 32;
    public const int MinIntersectionWidth = 100;

    public static (int X, int Y, double Width, double Height) ClampWindowBounds(
        int x,
        int y,
        double width,
        double height,
        IReadOnlyList<ScreenBounds>? screens,
        double defaultWidth = DefaultWindowWidth,
        double defaultHeight = DefaultWindowHeight,
        double minWidth = MinWindowWidth,
        double minHeight = MinWindowHeight)
    {
        if (!double.IsFinite(width) || width <= 0 || width > MaxAbsurdDimension)
        {
            width = defaultWidth;
        }
        if (!double.IsFinite(height) || height <= 0 || height > MaxAbsurdDimension)
        {
            height = defaultHeight;
        }

        width = Math.Max(minWidth, width);
        height = Math.Max(minHeight, height);

        if (screens == null || screens.Count == 0)
        {
            return (x, y, width, height);
        }

        int intWidth = (int)Math.Ceiling(width);
        int intHeight = (int)Math.Ceiling(height);

        ScreenBounds? bestScreen = null;
        long bestArea = -1;

        foreach (var screen in screens)
        {
            int interLeft = Math.Max(x, screen.X);
            int interTop = Math.Max(y, screen.Y);
            int interRight = Math.Min(x + intWidth, screen.X + screen.Width);
            int interBottom = Math.Min(y + intHeight, screen.Y + screen.Height);

            int interWidth = Math.Max(0, interRight - interLeft);
            int interHeight = Math.Max(0, interBottom - interTop);

            if (interWidth >= MinIntersectionWidth && interHeight >= MinTitleBarIntersectionHeight)
            {
                long area = (long)interWidth * interHeight;
                if (area > bestArea)
                {
                    bestArea = area;
                    bestScreen = screen;
                }
            }
        }

        if (bestScreen.HasValue)
        {
            var s = bestScreen.Value;
            if (width > s.Width) width = Math.Max(minWidth, s.Width);
            if (height > s.Height) height = Math.Max(minHeight, s.Height);

            int newIntWidth = (int)Math.Ceiling(width);
            int newIntHeight = (int)Math.Ceiling(height);

            int clampedY = Math.Clamp(y, s.Y, s.Y + s.Height - MinTitleBarIntersectionHeight);
            if (clampedY + newIntHeight > s.Y + s.Height && newIntHeight <= s.Height)
            {
                clampedY = Math.Max(s.Y, s.Y + s.Height - newIntHeight);
            }

            int clampedX;
            if (newIntWidth <= s.Width)
            {
                clampedX = Math.Clamp(x, s.X, s.X + s.Width - newIntWidth);
            }
            else
            {
                clampedX = s.X;
            }

            return (clampedX, clampedY, width, height);
        }
        else
        {
            var target = screens.FirstOrDefault(s => s.IsPrimary);
            if (target.Width <= 0 || target.Height <= 0)
            {
                target = screens[0];
            }

            if (width > target.Width) width = Math.Max(minWidth, target.Width);
            if (height > target.Height) height = Math.Max(minHeight, target.Height);

            int newIntWidth = (int)Math.Ceiling(width);
            int newIntHeight = (int)Math.Ceiling(height);

            int centeredX = target.X + Math.Max(0, (target.Width - newIntWidth) / 2);
            int centeredY = target.Y + Math.Max(0, (target.Height - newIntHeight) / 2);

            return (centeredX, centeredY, width, height);
        }
    }
}
