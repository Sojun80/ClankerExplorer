using System.Threading;
using System.Threading.Tasks;

namespace ClankerExplorer.Services.Metadata;

/// <summary>
/// Interface for metadata providers that extract information for specific file types or general filesystem items.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>
    /// Execution order (lower runs first, e.g. 0 for core filesystem, 10 for specific types).
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Checks if this provider handles the specified item.
    /// </summary>
    bool CanHandle(MetadataExtractionContext context);

    /// <summary>
    /// Asynchronously extracts metadata fields into the context.
    /// </summary>
    Task ProvideMetadataAsync(MetadataExtractionContext context, CancellationToken cancellationToken);
}
