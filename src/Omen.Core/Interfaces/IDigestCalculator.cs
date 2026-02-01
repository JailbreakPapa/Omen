// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Interfaces;

/// <summary>
/// Interface for computing content digests (hashes) for caching and CAS.
/// </summary>
public interface IDigestCalculator
{
    /// <summary>
    /// Computes the digest of a file.
    /// </summary>
    Task<ContentDigest> ComputeFileDigestAsync(string filePath, CancellationToken ct = default);
    
    /// <summary>
    /// Computes the digest of byte content.
    /// </summary>
    ContentDigest ComputeDigest(ReadOnlySpan<byte> content);
    
    /// <summary>
    /// Computes the digest of a stream.
    /// </summary>
    ContentDigest ComputeDigest(Stream stream);
    
    /// <summary>
    /// Computes the digest of a string.
    /// </summary>
    ContentDigest ComputeDigest(string content);
    
    /// <summary>
    /// Algorithm name (e.g., "SHA256").
    /// </summary>
    string AlgorithmName { get; }
}

/// <summary>
/// Represents a content-addressable digest.
/// </summary>
public readonly struct ContentDigest : IEquatable<ContentDigest>
{
    public string Hash { get; }
    public long Size { get; }
    
    public ContentDigest(string hash, long size)
    {
        Hash = hash;
        Size = size;
    }
    
    public bool Equals(ContentDigest other) => Hash == other.Hash && Size == other.Size;
    public override bool Equals(object? obj) => obj is ContentDigest other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Hash, Size);
    public override string ToString() => $"{Hash}/{Size}";
    
    public static bool operator ==(ContentDigest left, ContentDigest right) => left.Equals(right);
    public static bool operator !=(ContentDigest left, ContentDigest right) => !left.Equals(right);
    
    public static ContentDigest Parse(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !long.TryParse(parts[1], out var size))
            throw new FormatException($"Invalid digest format: {value}");
        return new ContentDigest(parts[0], size);
    }
}
