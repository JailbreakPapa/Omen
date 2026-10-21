// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Options;
using Omen.Executors.Orchestration;

namespace Omen.Core.Tests;

public class ManualOptionsStoreTests : IDisposable
{
    private readonly string _path;

    public ManualOptionsStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ManualOptionsStoreTests), Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void AddThenLoad_RoundTripsTheEntry()
    {
        var store = new ManualOptionsStore(_path);
        store.Add(new BuildOptionDeclaration
        {
            Name = "CUSTOM_FLAG",
            Description = "A manually added flag",
            Type = BuildOptionType.Bool,
            DefaultValue = "false",
            EffectiveValue = "false"
        });
        store.Save();

        var reloaded = new ManualOptionsStore(_path);

        reloaded.All.Should().ContainSingle(d => d.Name == "CUSTOM_FLAG");
    }

    [Fact]
    public void Remove_DropsTheEntry()
    {
        var store = new ManualOptionsStore(_path);
        store.Add(new BuildOptionDeclaration
        {
            Name = "CUSTOM_FLAG",
            Description = "A manually added flag",
            Type = BuildOptionType.Bool,
            DefaultValue = "false",
            EffectiveValue = "false"
        });

        store.Remove("CUSTOM_FLAG");

        store.All.Should().BeEmpty();
    }

    [Fact]
    public void Load_NoFileYet_ReturnsEmpty()
    {
        var store = new ManualOptionsStore(_path);
        store.All.Should().BeEmpty();
    }
}
