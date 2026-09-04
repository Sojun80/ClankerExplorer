using System;
using System.Collections.Generic;
using System.Threading;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services.Search;

/// <summary>
/// Defines a filesystem search provider capable of streaming search results progressively.
/// Designed so that an EverythingSearchProvider or other backend can be added cleanly in the next patch.
/// </summary>
public interface ISearchProvider
{
    /// <summary>
    /// Unique identifier for this provider (e.g., "native", "everything").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// User-friendly name displayed in the UI.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this search provider is currently available in this environment.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Progressively streams search results matching the specified request.
    /// </summary>
    IAsyncEnumerable<SearchResultItem> SearchAsync(
        SearchRequest request,
        IProgress<SearchProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}
