using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services.Search;

/// <summary>
/// Service coordinating search providers and executing search requests.
/// Allows clean registration of additional providers (such as Everything in V2)
/// while keeping consumers decoupled from individual search engines.
/// </summary>
public class SearchService
{
    private static readonly Lazy<SearchService> _instance = new(() => new SearchService());
    public static SearchService Instance => _instance.Value;

    private readonly List<ISearchProvider> _providers = new();
    private ISearchProvider _activeProvider;

    public SearchService(ISearchProvider? defaultProvider = null)
    {
        var native = defaultProvider ?? new NativeSearchProvider();
        _providers.Add(native);
        _activeProvider = native;
    }

    public ISearchProvider ActiveProvider
    {
        get => _activeProvider;
        set => _activeProvider = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IReadOnlyList<ISearchProvider> AvailableProviders => _providers.Where(p => p.IsAvailable).ToList();

    public void RegisterProvider(ISearchProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_providers.Any(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _providers.Add(provider);
        }
    }

    public IAsyncEnumerable<SearchResultItem> SearchAsync(
        SearchRequest request,
        IProgress<SearchProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _activeProvider.SearchAsync(request, progress, cancellationToken);
    }
}
