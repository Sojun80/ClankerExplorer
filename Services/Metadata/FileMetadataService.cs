using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.Models;
using ClankerExplorer.Services.Metadata.Providers;

namespace ClankerExplorer.Services.Metadata;

/// <summary>
/// Central coordinator for filesystem item metadata extraction.
/// Gathers metadata asynchronously across registered providers with built-in LRU caching.
/// </summary>
public class FileMetadataService
{
    private static readonly Lazy<FileMetadataService> _instance = new(() => new FileMetadataService());
    public static FileMetadataService Instance => _instance.Value;

    private readonly List<IMetadataProvider> _providers = new();
    private readonly FileMetadataCache _cache = new();

    public FileMetadataService()
    {
        // Register default providers ordered by priority
        _providers.Add(new FileSystemMetadataProvider());
        _providers.Add(new VideoMetadataProvider());
        _providers.Add(new AudioMetadataProvider());
        _providers.Add(new ImageMetadataProvider());
        _providers.Add(new DocumentMetadataProvider());
        _providers.Add(new ArchiveMetadataProvider());
        _providers.Add(new ExecutableMetadataProvider());
        _providers.Add(new TextMetadataProvider());

        _providers.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    /// <summary>
    /// Asynchronously gathers metadata for a filesystem item. Returns cached result if unchanged.
    /// </summary>
    public async Task<FileMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new FileMetadata(
                string.Empty,
                string.Empty,
                false,
                0,
                "0 B",
                DateTime.UtcNow,
                "None",
                Enumerable.Empty<MetadataSection>());
        }

        // 1. Check cache
        if (_cache.TryGet(filePath, out var cached) && cached != null)
        {
            return cached;
        }

        // 2. Extract metadata
        var context = new MetadataExtractionContext(filePath);

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (provider.CanHandle(context))
            {
                try
                {
                    await provider.ProvideMetadataAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Provider {provider.GetType().Name} failed: {ex.Message}");
                }
            }
        }

        var sections = context.BuildSections();
        var result = new FileMetadata(
            context.FilePath,
            context.ItemName,
            context.IsDirectory,
            context.SizeBytes,
            context.FormattedSize,
            context.ModifiedTimeUtc,
            context.QuickTypeDisplay,
            sections);

        // 3. Save to cache
        _cache.Set(filePath, result);

        return result;
    }

    /// <summary>
    /// Explicit, lazy calculation of SHA-256 and MD5 cryptographic hashes.
    /// Does not block UI thread; streams with 64KB buffer and cancellation.
    /// </summary>
    public async Task<HashResult> CalculateHashesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return new HashResult();
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha256Alg = SHA256.Create();
            using var md5Alg = MD5.Create();

            byte[] buffer = new byte[64 * 1024];
            int bytesRead;

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha256Alg.TransformBlock(buffer, 0, bytesRead, null, 0);
                md5Alg.TransformBlock(buffer, 0, bytesRead, null, 0);
            }

            sha256Alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            md5Alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            return new HashResult
            {
                Sha256 = Convert.ToHexString(sha256Alg.Hash ?? Array.Empty<byte>()).ToLowerInvariant(),
                Md5 = Convert.ToHexString(md5Alg.Hash ?? Array.Empty<byte>()).ToLowerInvariant()
            };
        }, cancellationToken);
    }

    public void InvalidateCache(string filePath) => _cache.Invalidate(filePath);

    public void ClearCache() => _cache.Clear();
}
