using System;
using System.Collections.Generic;

namespace ClankerExplorer.Services;

public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer OrdinalIgnoreCase { get; } = new();

    private NaturalStringComparer() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int xi = 0;
        int yi = 0;
        while (xi < x.Length && yi < y.Length)
        {
            bool xDigit = char.IsDigit(x[xi]);
            bool yDigit = char.IsDigit(y[yi]);

            if (xDigit && yDigit)
            {
                int xStart = xi;
                int yStart = yi;
                while (xi < x.Length && char.IsDigit(x[xi])) xi++;
                while (yi < y.Length && char.IsDigit(y[yi])) yi++;

                int xSignificant = xStart;
                int ySignificant = yStart;
                while (xSignificant < xi - 1 && x[xSignificant] == '0') xSignificant++;
                while (ySignificant < yi - 1 && y[ySignificant] == '0') ySignificant++;

                int xLength = xi - xSignificant;
                int yLength = yi - ySignificant;
                if (xLength != yLength) return xLength.CompareTo(yLength);

                int numericComparison = string.Compare(
                    x, xSignificant,
                    y, ySignificant,
                    xLength,
                    StringComparison.Ordinal);
                if (numericComparison != 0) return numericComparison;

                int runLengthComparison = (xi - xStart).CompareTo(yi - yStart);
                if (runLengthComparison != 0) return runLengthComparison;
                continue;
            }

            int charComparison = char.ToUpperInvariant(x[xi]).CompareTo(char.ToUpperInvariant(y[yi]));
            if (charComparison != 0) return charComparison;
            xi++;
            yi++;
        }

        int lengthComparison = (x.Length - xi).CompareTo(y.Length - yi);
        return lengthComparison != 0 ? lengthComparison : string.Compare(x, y, StringComparison.Ordinal);
    }
}
