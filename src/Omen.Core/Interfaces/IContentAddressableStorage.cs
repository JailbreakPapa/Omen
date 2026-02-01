// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Interfaces;

/// <summary>
/// Interface for content-addressable storage.
/// Stores blobs by their content hash for deduplication.
/// </summary>
public interface IContentAddressableStorage
{
    /// <summary>
    /// Checks if a blob exists in storage.
    /// </summary>
    Task<bool> ContainsAsync(ContentDigest digest, CancellationToken ct = default);
    
    /// <summary>
    /// Finds which digests are missing from storage.
    /// </summary>
    Task<IReadOnlyList<ContentDigest>> FindMissingAsync(
        IEnumerable<ContentDigest> digests, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Uploads a blob to storage.
    /// </summary>
    Task<ContentDigest> UploadAsync(
        Stream content, 
        long size,
        CancellationToken ct = default);
    
    /// <summary>
    /// Uploads a file to storage.
    /// </summary>
    Task<ContentDigest> UploadFileAsync(string filePath, CancellationToken ct = default);
    
    /// <summary>
    /// Downloads a blob from storage.
    /// </summary>
    Task<Stream> DownloadAsync(ContentDigest digest, CancellationToken ct = default);
    
    /// <summary>
    /// Downloads a blob to a file.
    /// </summary>
    Task DownloadToFileAsync(ContentDigest digest, string filePath, CancellationToken ct = default);
    
    /// <summary>
    /// Batch upload multiple blobs.
    /// </summary>
    Task<IReadOnlyList<ContentDigest>> BatchUploadAsync(
        IEnumerable<(Stream Content, long Size)> blobs,
        CancellationToken ct = default);
    
    /// <summary>
    /// Gets storage statistics.
    /// </summary>
    Task<StorageStatistics> GetStatisticsAsync(CancellationToken ct = default);
}

/// <summary>
/// Storage statistics.
/// </summary>
public sealed class StorageStatistics
{
    public long TotalBlobs { get; init; }
    public long TotalSizeBytes { get; init; }
    public long UploadCount { get; init; }
    public long DownloadCount { get; init; }
    public long DeduplicatedBytes { get; init; }
}
