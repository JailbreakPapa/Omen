// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Core.Tests;

public class ActionDigestStoreTests : IDisposable
{
    private readonly string _path;

    public ActionDigestStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionDigestStoreTests), Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void SetThenGet_RoundTripsTheDigest()
    {
        // Arrange
        var store = new ActionDigestStore(_path);
        var digest = new ContentDigest("abc123", 42);

        // Act
        store.Set("C:/out/Foo.obj", digest);

        // Assert
        store.TryGet("C:/out/Foo.obj", out var result).Should().BeTrue();
        result.Should().Be(digest);
    }

    [Fact]
    public void TryGet_UnknownOutput_ReturnsFalse()
    {
        var store = new ActionDigestStore(_path);
        store.TryGet("C:/out/Missing.obj", out _).Should().BeFalse();
    }

    [Fact]
    public void SaveThenReload_PersistsAcrossInstances()
    {
        // Arrange
        var digest = new ContentDigest("def456", 7);
        var store1 = new ActionDigestStore(_path);
        store1.Set("C:/out/Bar.obj", digest);
        store1.Save();

        // Act
        var store2 = new ActionDigestStore(_path);

        // Assert
        store2.TryGet("C:/out/Bar.obj", out var result).Should().BeTrue();
        result.Should().Be(digest);
    }
}
