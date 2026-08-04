// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Platforms;

namespace Omen.Core.Tests;

public class PlatformFactoryDiscoveryTests
{
    [Fact]
    public void DiscoverExternalSdks_NoDirectory_ReturnsEmpty()
    {
        var result = PlatformFactory.DiscoverExternalSdks(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverExternalSdks_NonexistentDirectory_ReturnsEmpty()
    {
        var result = PlatformFactory.DiscoverExternalSdks("/does/not/exist");
        result.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverExternalSdks_DirectoryWithNoAssemblies_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(PlatformFactoryDiscoveryTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var result = PlatformFactory.DiscoverExternalSdks(dir);

        result.Should().BeEmpty();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void DiscoverExternalSdks_DirectoryWithMalformedDll_SkipsAndContinues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(PlatformFactoryDiscoveryTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // Create a malformed .dll file (plain text, not a valid assembly)
        var malformedDll = Path.Combine(dir, "malformed.dll");
        File.WriteAllText(malformedDll, "This is not a valid assembly");

        // Should not throw, should return empty since the malformed DLL is skipped
        var result = PlatformFactory.DiscoverExternalSdks(dir);

        result.Should().BeEmpty();
        Directory.Delete(dir, recursive: true);
    }
}
