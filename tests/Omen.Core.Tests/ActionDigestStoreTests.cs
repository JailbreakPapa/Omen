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
        if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
        if (File.Exists(_path + ".lock")) File.Delete(_path + ".lock");
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

    [Fact]
    public void Save_IsAtomic_LeavesNoTempFileAndIgnoresAStaleOne()
    {
        // A build killed mid-save must not leave a truncated store; a stale temp file from such
        // a kill must not break the next save either.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", "{ garbage from a killed build");
        var store = new ActionDigestStore(_path);
        store.Set("C:/out/Baz.obj", new ContentDigest("0a1b", 3));

        store.Save();

        File.Exists(_path + ".tmp").Should().BeFalse();
        new ActionDigestStore(_path).TryGet("C:/out/Baz.obj", out var result).Should().BeTrue();
        result.Should().Be(new ContentDigest("0a1b", 3));
    }

    [Fact]
    public void Save_KeepsWhatAnotherStoreSavedSinceLoading()
    {
        // The build server loads the store once; an in-process build (--no-server, or the server
        // unavailable) saves its digests to the same file meanwhile. The server's next save must
        // not overwrite them, or every action that build compiled reruns as no-previous-digest.
        var server = new ActionDigestStore(_path);
        var inProcess = new ActionDigestStore(_path);
        inProcess.Set("C:/out/InProcess.obj", new ContentDigest("aa", 1));
        inProcess.Save();

        server.Set("C:/out/Server.obj", new ContentDigest("bb", 2));
        server.Save();

        var reloaded = new ActionDigestStore(_path);
        reloaded.TryGet("C:/out/InProcess.obj", out _).Should().BeTrue();
        reloaded.TryGet("C:/out/Server.obj", out _).Should().BeTrue();
    }

    [Fact]
    public void Refresh_SeesWhatAnotherStoreSaved_AndKeepsItsOwnUnsavedEntries()
    {
        var server = new ActionDigestStore(_path);
        server.Set("C:/out/Pending.obj", new ContentDigest("cc", 3));
        var inProcess = new ActionDigestStore(_path);
        inProcess.Set("C:/out/InProcess.obj", new ContentDigest("aa", 1));
        inProcess.Set("C:/out/Pending.obj", new ContentDigest("dd", 4));
        inProcess.Save();

        server.Refresh();

        server.TryGet("C:/out/InProcess.obj", out var theirs).Should().BeTrue();
        theirs.Should().Be(new ContentDigest("aa", 1));
        server.TryGet("C:/out/Pending.obj", out var ours).Should().BeTrue();
        ours.Should().Be(new ContentDigest("cc", 3));
    }

    [Fact]
    public void Set_FromManyThreadsWhileSaving_LosesNothing()
    {
        // The orchestrator records digests from executor worker threads and flushes every few
        // completions; without the store's locks this throws or drops entries.
        var store = new ActionDigestStore(_path);

        Parallel.For(0, 2000, i =>
        {
            store.Set($"C:/out/{i}.obj", new ContentDigest(i.ToString("x8"), i));
            if (i % 250 == 0) store.Save();
        });
        store.Save();

        var reloaded = new ActionDigestStore(_path);
        for (var i = 0; i < 2000; i++)
        {
            reloaded.TryGet($"C:/out/{i}.obj", out var digest).Should().BeTrue($"entry {i} must survive");
            digest.Should().Be(new ContentDigest(i.ToString("x8"), i));
        }
    }
}
