// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class GemAliasResolverTests : IDisposable
{
    private readonly string _projectRoot;

    public GemAliasResolverTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(GemAliasResolverTests), Guid.NewGuid().ToString("N"));
        var gemDir = Path.Combine(_projectRoot, "Gems", "Camera");
        Directory.CreateDirectory(gemDir);
        File.WriteAllText(Path.Combine(gemDir, "gem.json"), """{ "gem_name": "Camera", "version": "0.1.0" }""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }

    private BuildContext CreateContext() => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = _projectRoot,
        OutputDirectory = Path.Combine(_projectRoot, "Binaries"),
        IntermediateDirectory = Path.Combine(_projectRoot, "Intermediate")
    };

    private sealed class TestCameraGem : GemRules
    {
        public TestCameraGem(BuildContext context) : base(context)
        {
            LoadManifest("Gems/Camera");
            DefineFlavor(GemFlavorKind.Runtime);
            CreateAlias("Clients", GemFlavorKind.Runtime);
        }
    }

    [Fact]
    public void Resolve_PlainModuleName_ReturnsUnchanged()
    {
        GemAliasResolver.Resolve("Core", []).Should().Be("Core");
    }

    [Fact]
    public void Resolve_GemAliasReference_ReturnsConcreteModuleName()
    {
        var gem = new TestCameraGem(CreateContext());
        GemAliasResolver.Resolve("Gem::Camera.Clients", [gem]).Should().Be("Camera.Runtime");
    }

    [Fact]
    public void Resolve_UnknownGem_Throws()
    {
        var act = () => GemAliasResolver.Resolve("Gem::Nonexistent.Clients", []);
        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown gem*");
    }

    [Fact]
    public void Resolve_UnknownAlias_Throws()
    {
        var gem = new TestCameraGem(CreateContext());
        var act = () => GemAliasResolver.Resolve("Gem::Camera.Servers", [gem]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*no alias 'Servers'*");
    }

    [Fact]
    public void Resolve_MalformedReference_Throws()
    {
        var act = () => GemAliasResolver.Resolve("Gem::Camera", []);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Gem::<GemName>.<Alias>*");
    }
}
