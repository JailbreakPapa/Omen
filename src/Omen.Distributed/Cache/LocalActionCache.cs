// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using Omen.Core.Interfaces;

namespace Omen.Distributed.Cache;

/// <summary>
/// In-memory action cache with optional disk persistence.
/// </summary>
public sealed class LocalActionCache : IActionCache
{
    private readonly ConcurrentDictionary<string, CachedActionResult> _cache = new();
    private readonly string? _persistPath;
    private long _hitCount;
    private long _missCount;
    
    public LocalActionCache(string? persistPath = null)
    {
        _persistPath = persistPath;
        
        if (_persistPath != null)
        {
            Directory.CreateDirectory(_persistPath);
            LoadFromDisk();
        }
    }
    
    public Task<CachedActionResult?> GetAsync(ContentDigest actionDigest, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(actionDigest.Hash, out var result))
        {
            Interlocked.Increment(ref _hitCount);
            return Task.FromResult<CachedActionResult?>(result);
        }
        
        Interlocked.Increment(ref _missCount);
        return Task.FromResult<CachedActionResult?>(null);
    }
    
    public Task StoreAsync(ContentDigest actionDigest, CachedActionResult result, CancellationToken ct = default)
    {
        _cache[actionDigest.Hash] = result;
        
        if (_persistPath != null)
        {
            _ = Task.Run(() => PersistEntry(actionDigest.Hash, result), ct);
        }
        
        return Task.CompletedTask;
    }
    
    public Task<bool> ContainsAsync(ContentDigest actionDigest, CancellationToken ct = default)
    {
        return Task.FromResult(_cache.ContainsKey(actionDigest.Hash));
    }
    
    public Task RemoveAsync(ContentDigest actionDigest, CancellationToken ct = default)
    {
        _cache.TryRemove(actionDigest.Hash, out _);
        
        if (_persistPath != null)
        {
            var path = Path.Combine(_persistPath, actionDigest.Hash + ".json");
            if (File.Exists(path))
                File.Delete(path);
        }
        
        return Task.CompletedTask;
    }
    
    public Task ClearAsync(CancellationToken ct = default)
    {
        _cache.Clear();
        
        if (_persistPath != null && Directory.Exists(_persistPath))
        {
            foreach (var file in Directory.GetFiles(_persistPath, "*.json"))
            {
                File.Delete(file);
            }
        }
        
        return Task.CompletedTask;
    }
    
    public Task<CacheStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new CacheStatistics
        {
            TotalEntries = _cache.Count,
            TotalSizeBytes = 0, // Would need serialization to calculate
            HitCount = _hitCount,
            MissCount = _missCount
        });
    }
    
    private void LoadFromDisk()
    {
        if (_persistPath == null || !Directory.Exists(_persistPath))
            return;
        
        foreach (var file in Directory.GetFiles(_persistPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var result = System.Text.Json.JsonSerializer.Deserialize<CachedActionResult>(json);
                if (result != null)
                {
                    var hash = Path.GetFileNameWithoutExtension(file);
                    _cache[hash] = result;
                }
            }
            catch
            {
                // Ignore corrupt cache entries
            }
        }
    }
    
    private void PersistEntry(string hash, CachedActionResult result)
    {
        if (_persistPath == null) return;
        
        try
        {
            var path = Path.Combine(_persistPath, hash + ".json");
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore persistence errors
        }
    }
}
