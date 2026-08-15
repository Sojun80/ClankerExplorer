using System;
using Avalonia.Media;

namespace ClankerExplorer.Services;

/// <summary>
/// Provides logarithmic scaling and smooth color interpolation for the compact file-size visualization bar in the Size column.
/// </summary>
public static class FileSizeVisualizerHelper
{
    // Constants for logarithmic scaling
    public const long MinSizeBytes = 1024L; // 1 KB
    public const long MaxSizeBytes = 50L * 1024L * 1024L * 1024L; // 50 GB

    private static readonly double LogMin = Math.Log(MinSizeBytes);
    private static readonly double LogMax = Math.Log(MaxSizeBytes);
    private static readonly double LogRange = LogMax - LogMin;

    // Pre-allocated immutable brushes for normalized fill percentages 0..100 (0 allocations during scroll)
    private static readonly IBrush[] BrushTable = new IBrush[101];

    static FileSizeVisualizerHelper()
    {
        for (int i = 0; i <= 100; i++)
        {
            double fill = i / 100.0;
            var color = InterpolateColor(fill);
            BrushTable[i] = new SolidColorBrush(color);
        }
    }

    /// <summary>
    /// Calculates normalized 0.0 to 1.0 fill factor using a logarithmic scale.
    /// - 0 B or folders: 0.0
    /// - 1 KB: ~0.01 (minimum visible / effectively empty)
    /// - 1 MB: ~0.39 (39%)
    /// - 100 MB: ~0.65 (65%)
    /// - 1 GB: ~0.78 (78%, within 75-85% target)
    /// - 10 GB: ~0.91 (91%)
    /// - 50 GB: 1.0 (100%)
    /// - > 50 GB: clamped to 1.0
    /// </summary>
    public static double CalculateFill(long sizeBytes, bool isDirectory)
    {
        if (isDirectory || sizeBytes <= 0)
        {
            return 0.0;
        }

        if (sizeBytes < MinSizeBytes)
        {
            // For regular files under 1 KB (> 0 bytes), provide a minimal visible sliver (1%)
            return 0.01;
        }

        if (sizeBytes >= MaxSizeBytes)
        {
            return 1.0;
        }

        double logSize = Math.Log(sizeBytes);
        double fill = (logSize - LogMin) / LogRange;
        return Math.Clamp(fill, 0.01, 1.0);
    }

    /// <summary>
    /// Returns the cached, pre-allocated SolidColorBrush corresponding to the normalized fill (0.0 - 1.0).
    /// </summary>
    public static IBrush GetBrush(double fill)
    {
        int index = Math.Clamp((int)Math.Round(fill * 100.0), 0, 100);
        return BrushTable[index];
    }

    /// <summary>
    /// Smoothly maps normalized fill (0.0 to 1.0) across:
    /// - Small files (Teal / Green): 0.0 to 0.35
    /// - Medium files (Green / Yellow): 0.35 to 0.60
    /// - Large files (Yellow / Orange): 0.60 to 0.80
    /// - Very large files (Orange / Red): 0.80 to 1.00
    /// Using gentle opacity (~0.28 - 0.35) for subtle, legible rendering behind text.
    /// </summary>
    public static Color InterpolateColor(double fill)
    {
        fill = Math.Clamp(fill, 0.0, 1.0);

        // Keyframe color stops: (R, G, B, A)
        // 0.00: Teal (20, 184, 166, 70)
        // 0.35: Emerald Green (34, 197, 94, 75)
        // 0.60: Yellow (234, 179, 8, 80)
        // 0.80: Warm Orange (249, 115, 22, 85)
        // 1.00: Crimson Red (239, 68, 68, 90)

        if (fill <= 0.35)
        {
            double factor = fill / 0.35;
            return LerpColor(20, 184, 166, 70, 34, 197, 94, 75, factor);
        }
        else if (fill <= 0.60)
        {
            double factor = (fill - 0.35) / 0.25;
            return LerpColor(34, 197, 94, 75, 234, 179, 8, 80, factor);
        }
        else if (fill <= 0.80)
        {
            double factor = (fill - 0.60) / 0.20;
            return LerpColor(234, 179, 8, 80, 249, 115, 22, 85, factor);
        }
        else
        {
            double factor = (fill - 0.80) / 0.20;
            return LerpColor(249, 115, 22, 85, 239, 68, 68, 90, factor);
        }
    }

    private static Color LerpColor(byte r1, byte g1, byte b1, byte a1, byte r2, byte g2, byte b2, byte a2, double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        byte r = (byte)(r1 + (r2 - r1) * factor);
        byte g = (byte)(g1 + (g2 - g1) * factor);
        byte b = (byte)(b1 + (b2 - b1) * factor);
        byte a = (byte)(a1 + (a2 - a1) * factor);
        return Color.FromArgb(a, r, g, b);
    }
}
