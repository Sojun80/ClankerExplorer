using System;
using System.Collections.Generic;

namespace ClankerExplorer.Services.Search;

/// <summary>
/// Helper for filesystem path case-sensitivity and cycle detection across Windows, WSL, and Linux paths.
/// </summary>
public static class SearchPathHelper
{
    /// <summary>
    /// Determines whether a given path is case-sensitive.
    /// Ordinary Windows paths are case-insensitive.
    /// Linux/WSL UNC paths (\\wsl$\..., \\wsl.localhost\...) and Unix root paths are case-sensitive.
    /// </summary>
    public static bool IsCaseSensitivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return !OperatingSystem.IsWindows();
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string p = path.Trim();
        if (p.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("//wsl$/", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static StringComparison GetPathStringComparison(string? path)
    {
        return IsCaseSensitivePath(path) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    public static StringComparer GetPathStringComparer(string? path)
    {
        return IsCaseSensitivePath(path) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    }
}

/// <summary>
/// Comparer for directory cycle detection that respects case-sensitivity based on path type.
/// Ordinary Windows paths compare case-insensitively, while WSL/Linux paths distinguish case
/// (e.g. 'Foo' and 'foo' are separate directories in WSL).
/// </summary>
public sealed class PathCycleComparer : IEqualityComparer<string>
{
    public static readonly PathCycleComparer Instance = new();

    public bool Equals(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x == null || y == null) return false;

        bool isCaseSensitive = SearchPathHelper.IsCaseSensitivePath(x) || SearchPathHelper.IsCaseSensitivePath(y);
        return string.Equals(x, y, isCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(string obj)
    {
        if (obj == null) return 0;
        if (SearchPathHelper.IsCaseSensitivePath(obj))
        {
            return StringComparer.Ordinal.GetHashCode(obj);
        }
        return StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
    }
}
