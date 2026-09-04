using System;
using System.Collections.Generic;

namespace ClankerExplorer.Services.Search;

/// <summary>
/// Defines the scope for filesystem search execution.
/// </summary>
public enum SearchScope
{
    /// <summary>
    /// Search only the current folder (top-level, non-recursive).
    /// </summary>
    CurrentFolder,

    /// <summary>
    /// Search the current folder and all nested subfolders recursively.
    /// </summary>
    CurrentFolderAndSubfolders,

    /// <summary>
    /// Search all accessible filesystem roots on the current platform.
    /// </summary>
    Everywhere
}

/// <summary>
/// Immutable specification of a search query and its constraints.
/// Designed to support future providers (e.g., Everything) without contract churn.
/// </summary>
public sealed record SearchRequest(
    string Query,
    SearchScope Scope,
    string? CurrentFolder = null,
    IReadOnlyList<string>? CustomRoots = null)
{
    /// <summary>
    /// Whether the search should be case-sensitive. Default is false (case-insensitive).
    /// </summary>
    public bool CaseSensitive { get; init; } = false;

    /// <summary>
    /// Whether to match partial path segments in addition to item filenames.
    /// </summary>
    public bool MatchPath { get; init; } = true;

    /// <summary>
    /// Optional minimum file size filter in bytes (reserved for future query syntax).
    /// </summary>
    public long? MinSizeBytes { get; init; } = null;

    /// <summary>
    /// Optional maximum file size filter in bytes (reserved for future query syntax).
    /// </summary>
    public long? MaxSizeBytes { get; init; } = null;
}

/// <summary>
/// Progress information reported periodically during search execution.
/// </summary>
public sealed record SearchProgressReport(
    int FoldersSkipped,
    int MatchesFound,
    string? CurrentFolder = null);
