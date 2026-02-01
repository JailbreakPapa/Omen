// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Interfaces;

/// <summary>
/// Interface for the action cache - stores results of previously executed actions.
/// </summary>
public interface IActionCache
{
    /// <summary>
    /// Gets a cached action result by its digest.
    /// </summary>
    Task<CachedActionResult?> GetAsync(ContentDigest actionDigest, CancellationToken ct = default);
    
    /// <summary>
    /// Stores an action result.
    /// </summary>
    Task StoreAsync(ContentDigest actionDigest, CachedActionResult result, CancellationToken ct = default);
    
    /// <summary>
    /// Checks if an action result exists in cache.
    /// </summary>
    Task<bool> ContainsAsync(ContentDigest actionDigest, CancellationToken ct = default);
    
    /// <summary>
    /// Removes an action result from cache.
    /// </summary>
    Task RemoveAsync(ContentDigest actionDigest, CancellationToken ct = default);
    
    /// <summary>
    /// Clears all cached results.
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    Task<CacheStatistics> GetStatisticsAsync(CancellationToken ct = default);
}

/// <summary>
/// A cached action result.
/// </summary>
public sealed class CachedActionResult
{
    public required bool Success { get; init; }
    public required IReadOnlyList<ContentDigest> OutputDigests { get; init; }
    public required IReadOnlyList<string> OutputPaths { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public int ExitCode { get; init; }
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Cache statistics.
/// </summary>
public sealed class CacheStatistics
{
    public long TotalEntries { get; init; }
    public long TotalSizeBytes { get; init; }
    public long HitCount { get; init; }
    public long MissCount { get; init; }
    public double HitRate => HitCount + MissCount > 0 ? (double)HitCount / (HitCount + MissCount) : 0;
}
