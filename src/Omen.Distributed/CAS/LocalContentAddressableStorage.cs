// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using Omen.Core.Interfaces;

namespace Omen.Distributed.CAS;

/// <summary>
/// Local file-based content-addressable storage.
/// </summary>
public sealed class LocalContentAddressableStorage : IContentAddressableStorage
{
    private readonly string _storageDirectory;
    private readonly IDigestCalculator _digestCalculator;
    private readonly ConcurrentDictionary<string, bool> _knownBlobs = new();
    private long _uploadCount;
    private long _downloadCount;
    
    public LocalContentAddressableStorage(string storageDirectory, IDigestCalculator digestCalculator)
    {
        _storageDirectory = storageDirectory;
        _digestCalculator = digestCalculator;
        Directory.CreateDirectory(storageDirectory);
        
        // Index existing blobs
        IndexExistingBlobs();
    }
    
    public Task<bool> ContainsAsync(ContentDigest digest, CancellationToken ct = default)
    {
        var path = GetBlobPath(digest);
        var exists = File.Exists(path);
        return Task.FromResult(exists);
    }
    
    public async Task<IReadOnlyList<ContentDigest>> FindMissingAsync(
        IEnumerable<ContentDigest> digests, 
        CancellationToken ct = default)
    {
        var missing = new List<ContentDigest>();
        
        foreach (var digest in digests)
        {
            if (!await ContainsAsync(digest, ct))
            {
                missing.Add(digest);
            }
        }
        
        return missing;
    }
    
    public async Task<ContentDigest> UploadAsync(Stream content, long size, CancellationToken ct = default)
    {
        // Read content into memory to compute hash
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        
        var digest = _digestCalculator.ComputeDigest(bytes);
        var path = GetBlobPath(digest);
        
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct);
            _knownBlobs[digest.Hash] = true;
        }
        
        Interlocked.Increment(ref _uploadCount);
        return digest;
    }
    
    public async Task<ContentDigest> UploadFileAsync(string filePath, CancellationToken ct = default)
    {
        var digest = await _digestCalculator.ComputeFileDigestAsync(filePath, ct);
        var blobPath = GetBlobPath(digest);
        
        if (!File.Exists(blobPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            File.Copy(filePath, blobPath, overwrite: true);
            _knownBlobs[digest.Hash] = true;
        }
        
        Interlocked.Increment(ref _uploadCount);
        return digest;
    }
    
    public Task<Stream> DownloadAsync(ContentDigest digest, CancellationToken ct = default)
    {
        var path = GetBlobPath(digest);
        
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Blob not found: {digest}");
        }
        
        Interlocked.Increment(ref _downloadCount);
        return Task.FromResult<Stream>(File.OpenRead(path));
    }
    
    public async Task DownloadToFileAsync(ContentDigest digest, string filePath, CancellationToken ct = default)
    {
        var blobPath = GetBlobPath(digest);
        
        if (!File.Exists(blobPath))
        {
            throw new FileNotFoundException($"Blob not found: {digest}");
        }
        
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        
        // Use hard link if on same filesystem, otherwise copy
        try
        {
            File.Copy(blobPath, filePath, overwrite: true);
        }
        catch
        {
            await using var source = File.OpenRead(blobPath);
            await using var dest = File.Create(filePath);
            await source.CopyToAsync(dest, ct);
        }
        
        Interlocked.Increment(ref _downloadCount);
    }
    
    public async Task<IReadOnlyList<ContentDigest>> BatchUploadAsync(
        IEnumerable<(Stream Content, long Size)> blobs,
        CancellationToken ct = default)
    {
        var results = new List<ContentDigest>();
        
        foreach (var (content, size) in blobs)
        {
            var digest = await UploadAsync(content, size, ct);
            results.Add(digest);
        }
        
        return results;
    }
    
    public Task<StorageStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        long totalSize = 0;
        long totalBlobs = 0;
        
        if (Directory.Exists(_storageDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(_storageDirectory, "*", SearchOption.AllDirectories))
            {
                totalBlobs++;
                totalSize += new FileInfo(file).Length;
            }
        }
        
        return Task.FromResult(new StorageStatistics
        {
            TotalBlobs = totalBlobs,
            TotalSizeBytes = totalSize,
            UploadCount = _uploadCount,
            DownloadCount = _downloadCount,
            DeduplicatedBytes = 0 // Would need more tracking for this
        });
    }
    
    /// <summary>
    /// Cleans up old blobs to stay under the size limit.
    /// </summary>
    public async Task<long> CleanupAsync(long maxSizeBytes, CancellationToken ct = default)
    {
        var stats = await GetStatisticsAsync(ct);
        if (stats.TotalSizeBytes <= maxSizeBytes)
            return 0;
        
        // Get all blobs sorted by last access time
        var blobs = Directory.EnumerateFiles(_storageDirectory, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.LastAccessTimeUtc)
            .ToList();
        
        long freedBytes = 0;
        long currentSize = stats.TotalSizeBytes;
        
        foreach (var blob in blobs)
        {
            if (currentSize <= maxSizeBytes)
                break;
            
            ct.ThrowIfCancellationRequested();
            
            try
            {
                var size = blob.Length;
                blob.Delete();
                freedBytes += size;
                currentSize -= size;
                _knownBlobs.TryRemove(blob.Name, out _);
            }
            catch
            {
                // Ignore deletion errors
            }
        }
        
        return freedBytes;
    }
    
    private string GetBlobPath(ContentDigest digest)
    {
        // Use first 2 characters as subdirectory to avoid too many files in one directory
        var prefix = digest.Hash[..2];
        return Path.Combine(_storageDirectory, prefix, digest.Hash);
    }
    
    private void IndexExistingBlobs()
    {
        if (!Directory.Exists(_storageDirectory))
            return;
        
        foreach (var file in Directory.EnumerateFiles(_storageDirectory, "*", SearchOption.AllDirectories))
        {
            var hash = Path.GetFileName(file);
            _knownBlobs[hash] = true;
        }
    }
}
