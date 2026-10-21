// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class PluginManifestTests : IDisposable
{
    private readonly string _path;

    public PluginManifestTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(PluginManifestTests), Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_ParsesNameVersionDependenciesAndTags()
    {
        // Arrange - shape matches Plugins/Camera/plugin.json in NightFox
        File.WriteAllText(_path, """
        {
            "plugin_name": "Camera",
            "version": "0.1.0",
            "user_tags": ["Rendering", "Utility"],
            "dependencies": ["Atom_RPI"]
        }
        """);

        // Act
        var manifest = PluginManifest.Load(_path);

        // Assert
        manifest.PluginName.Should().Be("Camera");
        manifest.Version.Should().Be("0.1.0");
        manifest.Dependencies.Should().Contain("Atom_RPI");
        manifest.Tags.Should().BeEquivalentTo(["Rendering", "Utility"]);
    }

    [Fact]
    public void Load_MissingDependenciesAndTags_DefaultsToEmpty()
    {
        File.WriteAllText(_path, """{ "plugin_name": "Minimal", "version": "1.0.0" }""");

        var manifest = PluginManifest.Load(_path);

        manifest.Dependencies.Should().BeEmpty();
        manifest.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Load_MissingPluginName_Throws()
    {
        File.WriteAllText(_path, """{ "version": "1.0.0" }""");

        var act = () => PluginManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*plugin_name*");
    }

    [Fact]
    public void Load_FileDoesNotExist_ThrowsFileNotFoundException()
    {
        var act = () => PluginManifest.Load("/does/not/exist/plugin.json");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Load_PluginNameIsNotString_Throws()
    {
        File.WriteAllText(_path, """{ "plugin_name": 123, "version": "1.0.0" }""");

        var act = () => PluginManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*plugin_name*must be a string*");
    }

    [Fact]
    public void Load_VersionIsNotString_Throws()
    {
        File.WriteAllText(_path, """{ "plugin_name": "Test", "version": 123 }""");

        var act = () => PluginManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*version*must be a string*");
    }

    [Fact]
    public void Load_DependencyIsNotString_Throws()
    {
        File.WriteAllText(_path, """
        {
            "plugin_name": "Test",
            "version": "1.0.0",
            "dependencies": ["Valid", 123]
        }
        """);

        var act = () => PluginManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*dependencies[1]*must be a string*");
    }

    [Fact]
    public void Load_TagIsNotString_Throws()
    {
        File.WriteAllText(_path, """
        {
            "plugin_name": "Test",
            "version": "1.0.0",
            "user_tags": ["Valid", null]
        }
        """);

        var act = () => PluginManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*user_tags[1]*must be a string*");
    }
}
