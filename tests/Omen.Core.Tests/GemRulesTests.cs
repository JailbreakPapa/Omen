// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class GemRulesTests : IDisposable
{
    private readonly string _projectRoot;

    public GemRulesTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(GemRulesTests), Guid.NewGuid().ToString("N"));
        var gemDir = Path.Combine(_projectRoot, "Gems", "Camera");
        Directory.CreateDirectory(gemDir);
        File.WriteAllText(Path.Combine(gemDir, "gem.json"), """
        { "gem_name": "Camera", "version": "0.1.0", "dependencies": ["Atom_RPI"] }
        """);
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

            DefineFlavor(GemFlavorKind.Static);

            var runtime = DefineFlavor(GemFlavorKind.Runtime);
            runtime.BinaryType = TargetType.SharedLibrary;
            runtime.PrivateDependencies.Add($"{Name}.Static");

            var editor = DefineFlavor(GemFlavorKind.Editor);
            editor.BinaryType = TargetType.SharedLibrary;

            CreateAlias("Clients", GemFlavorKind.Runtime);
            CreateAlias("Tools", GemFlavorKind.Editor);
        }
    }

    [Fact]
    public void LoadManifest_SetsNameFromGemJson()
    {
        var gem = new TestCameraGem(CreateContext());
        gem.Name.Should().Be("Camera");
        gem.Manifest.Should().NotBeNull();
        gem.Manifest!.Dependencies.Should().Contain("Atom_RPI");
    }

    [Fact]
    public void DefineFlavor_RegistersFlavorByKind()
    {
        var gem = new TestCameraGem(CreateContext());
        gem.Flavors.Should().ContainKey(GemFlavorKind.Static);
        gem.Flavors.Should().ContainKey(GemFlavorKind.Runtime);
        gem.Flavors[GemFlavorKind.Runtime].BinaryType.Should().Be(TargetType.SharedLibrary);
        gem.Flavors[GemFlavorKind.Runtime].PrivateDependencies.Should().Contain("Camera.Static");
    }

    [Fact]
    public void DefineFlavor_Static_DefaultsBinaryTypeToStaticLibrary()
    {
        // A "Static" flavor that isn't actually a static library is never what a gem author
        // meant - it must default to producing its own linkable artifact rather than silently
        // folding into whatever aggregate consumes it (see ActionGraphBuilder fix for the
        // matching "folded dependency" failure mode).
        var gem = new TestCameraGem(CreateContext());
        gem.Flavors[GemFlavorKind.Static].BinaryType.Should().Be(TargetType.StaticLibrary);
    }

    private sealed class GemWithUndefaultedTools : GemRules
    {
        public GemWithUndefaultedTools(BuildContext context) : base(context)
        {
            LoadManifest("Gems/Camera");
            DefineFlavor(GemFlavorKind.Tools); // intentionally left without setting BinaryType
        }
    }

    [Fact]
    public void DefineFlavor_NonStaticKinds_LeaveBinaryTypeUnsetByDefault()
    {
        var gem = new GemWithUndefaultedTools(CreateContext());
        gem.Flavors[GemFlavorKind.Tools].BinaryType.Should().BeNull();
    }

    [Fact]
    public void CreateAlias_MapsAliasToFlavor()
    {
        var gem = new TestCameraGem(CreateContext());
        gem.Aliases["Clients"].Should().Be(GemFlavorKind.Runtime);
        gem.Aliases["Tools"].Should().Be(GemFlavorKind.Editor);
    }

    private sealed class GemWithBadAlias : GemRules
    {
        public GemWithBadAlias(BuildContext context) : base(context)
        {
            LoadManifest("Gems/Camera");
            CreateAlias("Clients", GemFlavorKind.Runtime); // Runtime flavor never defined
        }
    }

    [Fact]
    public void CreateAlias_UndefinedFlavor_Throws()
    {
        var act = () => new GemWithBadAlias(CreateContext());
        act.Should().Throw<InvalidOperationException>().WithMessage("*undefined flavor*");
    }
}
