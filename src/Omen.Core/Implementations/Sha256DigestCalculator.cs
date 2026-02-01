// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Security.Cryptography;

namespace Omen.Core.Implementations;

using Omen.Core.Interfaces;

/// <summary>
/// SHA-256 based digest calculator for content-addressable storage.
/// </summary>
public sealed class Sha256DigestCalculator : IDigestCalculator
{
    public string AlgorithmName => "SHA256";
    
    public async Task<ContentDigest> ComputeFileDigestAsync(string filePath, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha256.ComputeHashAsync(stream, ct);
        var size = new FileInfo(filePath).Length;
        return new ContentDigest(Convert.ToHexString(hash).ToLowerInvariant(), size);
    }
    
    public ContentDigest ComputeDigest(ReadOnlySpan<byte> content)
    {
        var hash = SHA256.HashData(content);
        return new ContentDigest(Convert.ToHexString(hash).ToLowerInvariant(), content.Length);
    }
    
    public ContentDigest ComputeDigest(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return new ContentDigest(Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }
    
    public ContentDigest ComputeDigest(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return ComputeDigest(bytes);
    }
}
