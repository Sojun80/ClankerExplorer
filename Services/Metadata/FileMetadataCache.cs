using System;
using System.Collections.Generic;
using System.IO;

namespace ClankerExplorer.Services.Metadata;

/// <summary>
/// Thread-safe LRU cache for file metadata, keyed by normalized path, size, and last modified timestamp.
/// Prevents redundant disk I/O and media parsing when switching items or opening Properties.
/// </summary>
public class FileMetadataCache
{
    private const int MaxEntries = 256;
    private readonly object _lock = new();
    private readonly Dictionary<string, CacheNode> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();

    private sealed record CacheNode(FileMetadata Metadata, LinkedListNode<string> Node);

    public static string CreateCacheKey(string filePath)
    {
        try
        {
            string fullPath = Path.GetFullPath(filePath);
            if (OperatingSystem.IsWindows())
            {
                fullPath = fullPath.ToUpperInvariant();
            }

            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                return $"{fullPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            }
            if (Directory.Exists(filePath))
            {
                var di = new DirectoryInfo(filePath);
                return $"{fullPath}|DIR|{di.LastWriteTimeUtc.Ticks}";
            }
            return fullPath;
        }
        catch
        {
            return filePath;
        }
    }

    public bool TryGet(string filePath, out FileMetadata? metadata)
    {
        string key = CreateCacheKey(filePath);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node.Node);
                _lru.AddFirst(node.Node);
                metadata = node.Metadata;
                return true;
            }
        }

        metadata = null;
        return false;
    }

    public void Set(string filePath, FileMetadata metadata)
    {
        string key = CreateCacheKey(filePath);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddFirst(existing.Node);
                _map[key] = new CacheNode(metadata, existing.Node);
                return;
            }

            while (_map.Count >= MaxEntries && _lru.Last != null)
            {
                string oldestKey = _lru.Last.Value;
                _lru.RemoveLast();
                _map.Remove(oldestKey);
            }

            var node = _lru.AddFirst(key);
            _map[key] = new CacheNode(metadata, node);
        }
    }

    public void Invalidate(string filePath)
    {
        string key = CreateCacheKey(filePath);
        lock (_lock)
        {
            if (_map.Remove(key, out var removed))
            {
                _lru.Remove(removed.Node);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
        }
    }
}
