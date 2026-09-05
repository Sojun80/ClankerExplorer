using System.Threading.Tasks;

namespace ClankerExplorer.Services.Preview;

/// <summary>
/// Defines an opportunistic preview component or service that can yield file ownership
/// on-demand when external applications or shell activation require access to the file.
/// </summary>
public interface IPreviewService
{
    /// <summary>
    /// Checks whether the preview component currently owns or holds open resources for the specified file.
    /// </summary>
    bool OwnsFile(string? filePath);

    /// <summary>
    /// Asynchronously yields ownership, releases media/file handles, and stops active playback
    /// for the specified file with real completion semantics before normal file activation proceeds.
    /// </summary>
    Task YieldFileAsync(string filePath);
}
