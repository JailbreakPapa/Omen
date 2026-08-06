// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Options;

namespace Omen.Core.Tests;

public class OptionCacheStoreTests : IDisposable
{
    private readonly string _path;
    private readonly string _testDir;

    public OptionCacheStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(OptionCacheStoreTests), Guid.NewGuid().ToString());
        _path = Path.Combine(_testDir, "cache.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void Load_FileDoesNotExist_ReturnsEmptyDictionary()
    {
        var store = new OptionCacheStore(_path);
        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new OptionCacheStore(_path);
        var values = new Dictionary<string, string> { ["ENABLE_FEATURE_X"] = "true", ["MAX_WORKERS"] = "8" };

        store.Save(values);
        var loaded = store.Load();

        loaded.Should().BeEquivalentTo(values);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsEmptyDictionaryInsteadOfThrowing()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(_path, "{ not valid json");

        var store = new OptionCacheStore(_path);

        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void Save_CreatesParentDirectoryIfMissing()
    {
        var store = new OptionCacheStore(_path);
        Directory.Exists(Path.GetDirectoryName(_path)).Should().BeFalse();

        store.Save(new Dictionary<string, string> { ["X"] = "1" });

        File.Exists(_path).Should().BeTrue();
    }
}
