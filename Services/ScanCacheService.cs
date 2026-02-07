using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskWise.Models;

namespace DiskWise.Services;

/// <summary>
/// Manages persistent caching of scan results
/// </summary>
public class ScanCacheService
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiskWise",
        "scan-cache");

    private readonly ConcurrentDictionary<string, CachedScanResult> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

    // Default cache expiration: 7 days
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initialize the cache service and load existing cache index
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                Directory.CreateDirectory(CacheDirectory);
                return;
            }

            // Load cache index to memory for quick lookups
            var indexPath = Path.Combine(CacheDirectory, "index.json");
            if (File.Exists(indexPath))
            {
                var json = await File.ReadAllTextAsync(indexPath);
                var index = JsonSerializer.Deserialize<CacheIndex>(json, JsonOptions);
                if (index?.Entries != null)
                {
                    foreach (var entry in index.Entries)
                    {
                        if (!IsExpired(entry.CachedAt))
                        {
                            _memoryCache[entry.Path] = new CachedScanResult
                            {
                                Path = entry.Path,
                                CachedAt = entry.CachedAt,
                                TotalSize = entry.TotalSize,
                                FileCount = entry.FileCount,
                                FolderCount = entry.FolderCount,
                                CacheFileName = entry.CacheFileName
                            };
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore initialization errors
        }
    }

    /// <summary>
    /// Check if a scan result exists in cache and is valid
    /// </summary>
    public bool HasValidCache(string path)
    {
        if (_memoryCache.TryGetValue(path, out var cached))
        {
            return !IsExpired(cached.CachedAt);
        }
        return false;
    }

    /// <summary>
    /// Get cached scan result (returns null if not found or expired)
    /// </summary>
    public async Task<FileSystemItem?> GetCachedScanAsync(string path)
    {
        if (!_memoryCache.TryGetValue(path, out var cached) || IsExpired(cached.CachedAt))
        {
            return null;
        }

        try
        {
            var cacheFile = Path.Combine(CacheDirectory, cached.CacheFileName);
            if (!File.Exists(cacheFile))
            {
                _memoryCache.TryRemove(path, out _);
                return null;
            }

            var json = await File.ReadAllTextAsync(cacheFile);
            var cachedData = JsonSerializer.Deserialize<CachedFileSystemItem>(json, JsonOptions);

            return cachedData != null ? ConvertToFileSystemItem(cachedData) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Save scan result to cache
    /// </summary>
    public async Task SaveScanAsync(string path, FileSystemItem item)
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                Directory.CreateDirectory(CacheDirectory);
            }

            var cacheFileName = GetCacheFileName(path);
            var cacheFile = Path.Combine(CacheDirectory, cacheFileName);

            var cachedData = ConvertToCachedItem(item);
            var json = JsonSerializer.Serialize(cachedData, JsonOptions);
            await File.WriteAllTextAsync(cacheFile, json);

            var result = new CachedScanResult
            {
                Path = path,
                CachedAt = DateTime.Now,
                TotalSize = item.Size,
                FileCount = item.FileCount,
                FolderCount = item.FolderCount,
                CacheFileName = cacheFileName
            };

            _memoryCache[path] = result;

            // Update index
            await SaveIndexAsync();
        }
        catch
        {
            // Ignore save errors
        }
    }

    /// <summary>
    /// Remove a specific path from cache
    /// </summary>
    public async Task RemoveCacheAsync(string path)
    {
        if (_memoryCache.TryRemove(path, out var cached))
        {
            try
            {
                var cacheFile = Path.Combine(CacheDirectory, cached.CacheFileName);
                if (File.Exists(cacheFile))
                {
                    File.Delete(cacheFile);
                }
                await SaveIndexAsync();
            }
            catch
            {
                // Ignore delete errors
            }
        }
    }

    /// <summary>
    /// Clear all cached data
    /// </summary>
    public async Task ClearAllCacheAsync()
    {
        _memoryCache.Clear();

        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                foreach (var file in Directory.GetFiles(CacheDirectory))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Ignore clear errors
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Get all cached paths for searching
    /// </summary>
    public IEnumerable<string> GetCachedPaths()
    {
        return _memoryCache.Keys.Where(p => !IsExpired(_memoryCache[p].CachedAt));
    }

    /// <summary>
    /// Search within all cached scan results
    /// </summary>
    public async Task<List<SearchResult>> SearchInCacheAsync(string rootPath, string searchQuery, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();

        if (string.IsNullOrWhiteSpace(searchQuery))
            return results;

        // Get the cached scan for the root path
        var cachedItem = await GetCachedScanAsync(rootPath);
        if (cachedItem == null)
            return results;

        // Search recursively
        SearchRecursive(cachedItem, searchQuery.ToLowerInvariant(), results, cancellationToken);

        return results;
    }

    private void SearchRecursive(FileSystemItem item, string query, List<SearchResult> results, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        // Check if name matches
        if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new SearchResult
            {
                Item = item,
                MatchType = item.IsDirectory ? SearchMatchType.Folder : SearchMatchType.File
            });
        }

        // Search in children
        foreach (var child in item.Children)
        {
            SearchRecursive(child, query, results, cancellationToken);
        }
    }

    private bool IsExpired(DateTime cachedAt)
    {
        return DateTime.Now - cachedAt > _cacheExpiration;
    }

    private string GetCacheFileName(string path)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16] + ".json";
    }

    private async Task SaveIndexAsync()
    {
        try
        {
            var index = new CacheIndex
            {
                Entries = _memoryCache.Values.Select(c => new CacheIndexEntry
                {
                    Path = c.Path,
                    CachedAt = c.CachedAt,
                    TotalSize = c.TotalSize,
                    FileCount = c.FileCount,
                    FolderCount = c.FolderCount,
                    CacheFileName = c.CacheFileName
                }).ToList()
            };

            var json = JsonSerializer.Serialize(index, JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(CacheDirectory, "index.json"), json);
        }
        catch
        {
            // Ignore index save errors
        }
    }

    private static CachedFileSystemItem ConvertToCachedItem(FileSystemItem item)
    {
        return new CachedFileSystemItem
        {
            Path = item.Path,
            Name = item.Name,
            Size = item.Size,
            IsDirectory = item.IsDirectory,
            IsHidden = item.IsHidden,
            IsSystem = item.IsSystem,
            LastModified = item.LastModified,
            FileCount = item.FileCount,
            FolderCount = item.FolderCount,
            Children = item.Children.Select(ConvertToCachedItem).ToList()
        };
    }

    private static FileSystemItem ConvertToFileSystemItem(CachedFileSystemItem cached, FileSystemItem? parent = null)
    {
        var item = new FileSystemItem
        {
            Path = cached.Path,
            Name = cached.Name,
            Size = cached.Size,
            IsDirectory = cached.IsDirectory,
            IsHidden = cached.IsHidden,
            IsSystem = cached.IsSystem,
            LastModified = cached.LastModified,
            FileCount = cached.FileCount,
            FolderCount = cached.FolderCount,
            IsScanned = true,
            Parent = parent
        };

        foreach (var child in cached.Children)
        {
            item.Children.Add(ConvertToFileSystemItem(child, item));
        }

        return item;
    }
}

#region Cache Data Models

public class CachedScanResult
{
    public string Path { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
    public long TotalSize { get; set; }
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public string CacheFileName { get; set; } = string.Empty;
}

public class CacheIndex
{
    public List<CacheIndexEntry> Entries { get; set; } = [];
}

public class CacheIndexEntry
{
    public string Path { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
    public long TotalSize { get; set; }
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public string CacheFileName { get; set; } = string.Empty;
}

public class CachedFileSystemItem
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsHidden { get; set; }
    public bool IsSystem { get; set; }
    public DateTime LastModified { get; set; }
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public List<CachedFileSystemItem> Children { get; set; } = [];
}

public class SearchResult
{
    public FileSystemItem Item { get; set; } = null!;
    public SearchMatchType MatchType { get; set; }
}

public enum SearchMatchType
{
    File,
    Folder
}

#endregion
