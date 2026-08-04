// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class GemManifestTests : IDisposable
{
    private readonly string _path;

    public GemManifestTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(GemManifestTests), Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_ParsesNameVersionDependenciesAndTags()
    {
        // Arrange - shape matches Gems/Camera/gem.json in NightFox
        File.WriteAllText(_path, """
        {
            "gem_name": "Camera",
            "version": "0.1.0",
            "user_tags": ["Rendering", "Utility"],
            "dependencies": ["Atom_RPI"]
        }
        """);

        // Act
        var manifest = GemManifest.Load(_path);

        // Assert
        manifest.GemName.Should().Be("Camera");
        manifest.Version.Should().Be("0.1.0");
        manifest.Dependencies.Should().Contain("Atom_RPI");
        manifest.Tags.Should().BeEquivalentTo(["Rendering", "Utility"]);
    }

    [Fact]
    public void Load_MissingDependenciesAndTags_DefaultsToEmpty()
    {
        File.WriteAllText(_path, """{ "gem_name": "Minimal", "version": "1.0.0" }""");

        var manifest = GemManifest.Load(_path);

        manifest.Dependencies.Should().BeEmpty();
        manifest.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Load_MissingGemName_Throws()
    {
        File.WriteAllText(_path, """{ "version": "1.0.0" }""");

        var act = () => GemManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*gem_name*");
    }

    [Fact]
    public void Load_FileDoesNotExist_ThrowsFileNotFoundException()
    {
        var act = () => GemManifest.Load("/does/not/exist/gem.json");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Load_GemNameIsNotString_Throws()
    {
        File.WriteAllText(_path, """{ "gem_name": 123, "version": "1.0.0" }""");

        var act = () => GemManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*gem_name*must be a string*");
    }

    [Fact]
    public void Load_VersionIsNotString_Throws()
    {
        File.WriteAllText(_path, """{ "gem_name": "Test", "version": 123 }""");

        var act = () => GemManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*version*must be a string*");
    }

    [Fact]
    public void Load_DependencyIsNotString_Throws()
    {
        File.WriteAllText(_path, """
        {
            "gem_name": "Test",
            "version": "1.0.0",
            "dependencies": ["Valid", 123]
        }
        """);

        var act = () => GemManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*dependencies[1]*must be a string*");
    }

    [Fact]
    public void Load_TagIsNotString_Throws()
    {
        File.WriteAllText(_path, """
        {
            "gem_name": "Test",
            "version": "1.0.0",
            "user_tags": ["Valid", null]
        }
        """);

        var act = () => GemManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*user_tags[1]*must be a string*");
    }
}
