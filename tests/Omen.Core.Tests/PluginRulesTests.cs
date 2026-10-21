// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class PluginRulesTests : IDisposable
{
    private readonly string _projectRoot;

    public PluginRulesTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(PluginRulesTests), Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Camera");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
        { "plugin_name": "Camera", "version": "0.1.0", "dependencies": ["Atom_RPI"] }
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

    private sealed class TestCameraPlugin : PluginRules
    {
        public TestCameraPlugin(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Camera");

            DefineFlavor(PluginFlavorKind.Static);

            var runtime = DefineFlavor(PluginFlavorKind.Runtime);
            runtime.BinaryType = TargetType.SharedLibrary;
            runtime.PrivateDependencies.Add($"{Name}.Static");

            var editor = DefineFlavor(PluginFlavorKind.Editor);
            editor.BinaryType = TargetType.SharedLibrary;

            CreateAlias("Clients", PluginFlavorKind.Runtime);
            CreateAlias("Tools", PluginFlavorKind.Editor);
        }
    }

    [Fact]
    public void LoadManifest_SetsNameFromPluginJson()
    {
        var plugin = new TestCameraPlugin(CreateContext());
        plugin.Name.Should().Be("Camera");
        plugin.Manifest.Should().NotBeNull();
        plugin.Manifest!.Dependencies.Should().Contain("Atom_RPI");
    }

    [Fact]
    public void DefineFlavor_RegistersFlavorByKind()
    {
        var plugin = new TestCameraPlugin(CreateContext());
        plugin.Flavors.Should().ContainKey(PluginFlavorKind.Static);
        plugin.Flavors.Should().ContainKey(PluginFlavorKind.Runtime);
        plugin.Flavors[PluginFlavorKind.Runtime].BinaryType.Should().Be(TargetType.SharedLibrary);
        plugin.Flavors[PluginFlavorKind.Runtime].PrivateDependencies.Should().Contain("Camera.Static");
    }

    [Fact]
    public void DefineFlavor_Static_DefaultsBinaryTypeToStaticLibrary()
    {
        // A "Static" flavor that isn't actually a static library is never what a plugin author
        // meant - it must default to producing its own linkable artifact rather than silently
        // folding into whatever aggregate consumes it (see ActionGraphBuilder fix for the
        // matching "folded dependency" failure mode).
        var plugin = new TestCameraPlugin(CreateContext());
        plugin.Flavors[PluginFlavorKind.Static].BinaryType.Should().Be(TargetType.StaticLibrary);
    }

    private sealed class PluginWithUndefaultedTools : PluginRules
    {
        public PluginWithUndefaultedTools(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Camera");
            DefineFlavor(PluginFlavorKind.Tools); // intentionally left without setting BinaryType
        }
    }

    [Fact]
    public void DefineFlavor_NonStaticKinds_LeaveBinaryTypeUnsetByDefault()
    {
        var plugin = new PluginWithUndefaultedTools(CreateContext());
        plugin.Flavors[PluginFlavorKind.Tools].BinaryType.Should().BeNull();
    }

    private sealed class PluginWithTestFlavors : PluginRules
    {
        public PluginWithTestFlavors(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Camera");

            DefineFlavor(PluginFlavorKind.Static);

            var tests = DefineFlavor(PluginFlavorKind.Tests);
            tests.PrivateDependencies.Add($"{Name}.Static");

            var editorTests = DefineFlavor(PluginFlavorKind.EditorTests);
            editorTests.PrivateDependencies.Add($"{Name}.Editor");
        }
    }

    [Fact]
    public void DefineFlavor_TestsAndEditorTests_RegisterAsDistinctFlavors()
    {
        // Runtime tests and editor tests are different build units (different sources,
        // different dependencies, different platform gating) - they must occupy separate
        // PluginFlavorKind slots rather than one overwriting the other in plugin.Flavors.
        var plugin = new PluginWithTestFlavors(CreateContext());
        plugin.Flavors.Should().ContainKey(PluginFlavorKind.Tests);
        plugin.Flavors.Should().ContainKey(PluginFlavorKind.EditorTests);
        plugin.Flavors[PluginFlavorKind.Tests].PrivateDependencies.Should().Contain("Camera.Static");
        plugin.Flavors[PluginFlavorKind.EditorTests].PrivateDependencies.Should().Contain("Camera.Editor");
    }

    [Fact]
    public void DefineFlavor_TestsAndEditorTests_LeaveBinaryTypeUnsetByDefault()
    {
        // Same rule as Runtime/Editor/Tools: only Static is defaulted, everything else is
        // the plugin author's call.
        var plugin = new PluginWithTestFlavors(CreateContext());
        plugin.Flavors[PluginFlavorKind.Tests].BinaryType.Should().BeNull();
        plugin.Flavors[PluginFlavorKind.EditorTests].BinaryType.Should().BeNull();
    }

    private sealed class PluginWithModernLayout : PluginRules
    {
        public PluginWithModernLayout(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Camera");

            DefineFlavor(PluginFlavorKind.Api);
            var privateObject = DefineFlavor(PluginFlavorKind.PrivateObject);
            privateObject.PublicDependencies.Add($"{Name}.API");

            var runtime = DefineFlavor(PluginFlavorKind.Runtime);
            runtime.BinaryType = TargetType.SharedLibrary;
            runtime.PublicDependencies.Add($"{Name}.API");
            runtime.PrivateDependencies.Add($"{Name}.Private.Object");

            DefineFlavor(PluginFlavorKind.EditorApi);
            var editorPrivateObject = DefineFlavor(PluginFlavorKind.EditorPrivateObject);
            editorPrivateObject.PublicDependencies.Add($"{Name}.Private.Object");

            var editor = DefineFlavor(PluginFlavorKind.Editor);
            editor.BinaryType = TargetType.SharedLibrary;
            editor.PrivateDependencies.Add($"{Name}.Editor.Private.Object");

            CreateAlias("Clients.API", PluginFlavorKind.Api);
            CreateAlias("Tools.API", PluginFlavorKind.EditorApi);
        }
    }

    [Fact]
    public void DefineFlavor_Api_DefaultsBinaryTypeToHeaderOnly()
    {
        // O3DE's `${plugin_name}.API` is always an INTERFACE target - a generator emitting this
        // mechanically shouldn't also have to remember to set BinaryType every call.
        var plugin = new PluginWithModernLayout(CreateContext());
        plugin.Flavors[PluginFlavorKind.Api].BinaryType.Should().Be(TargetType.HeaderOnly);
        plugin.Flavors[PluginFlavorKind.EditorApi].BinaryType.Should().Be(TargetType.HeaderOnly);
    }

    [Fact]
    public void DefineFlavor_PrivateObject_DefaultsBinaryTypeToStaticLibrary()
    {
        // Same rule as Static: an object-producing flavor that isn't a static library was
        // never what a plugin author meant.
        var plugin = new PluginWithModernLayout(CreateContext());
        plugin.Flavors[PluginFlavorKind.PrivateObject].BinaryType.Should().Be(TargetType.StaticLibrary);
        plugin.Flavors[PluginFlavorKind.EditorPrivateObject].BinaryType.Should().Be(TargetType.StaticLibrary);
    }

    private sealed class PluginWithEditorStatic : PluginRules
    {
        public PluginWithEditorStatic(BuildContext context) : base(context)
        {
            DefineFlavor(PluginFlavorKind.EditorStatic);
        }
    }

    [Fact]
    public void DefineFlavor_EditorStatic_DefaultsBinaryTypeToStaticLibrary()
    {
        // Classic layout's editor-side static library - same rule as Static/PrivateObject:
        // an object-producing flavor that isn't a static library was never what a plugin
        // author meant.
        var plugin = new PluginWithEditorStatic(CreateContext());
        plugin.Flavors[PluginFlavorKind.EditorStatic].BinaryType.Should().Be(TargetType.StaticLibrary);
    }

    [Fact]
    public void CreateAlias_DottedAliasName_ResolvesCorrectly()
    {
        // "Clients.API"/"Tools.API" are real O3DE alias names (ly_create_alias NAME
        // ${plugin_name}.Clients.API) - the alias dictionary is keyed by plain string, so a dot
        // in the alias name itself needs no special handling.
        var plugin = new PluginWithModernLayout(CreateContext());
        plugin.Aliases["Clients.API"].Should().Be(PluginFlavorKind.Api);
        plugin.Aliases["Tools.API"].Should().Be(PluginFlavorKind.EditorApi);
    }

    [Fact]
    public void CreateAlias_MapsAliasToFlavor()
    {
        var plugin = new TestCameraPlugin(CreateContext());
        plugin.Aliases["Clients"].Should().Be(PluginFlavorKind.Runtime);
        plugin.Aliases["Tools"].Should().Be(PluginFlavorKind.Editor);
    }

    private sealed class PluginWithBadAlias : PluginRules
    {
        public PluginWithBadAlias(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Camera");
            CreateAlias("Clients", PluginFlavorKind.Runtime); // Runtime flavor never defined
        }
    }

    [Fact]
    public void CreateAlias_UndefinedFlavor_Throws()
    {
        var act = () => new PluginWithBadAlias(CreateContext());
        act.Should().Throw<InvalidOperationException>().WithMessage("*undefined flavor*");
    }

    [Fact]
    public void DefineFlavor_PrivateObject_DefaultsIsPrivateTargetToTrue()
    {
        // Real O3DE always pairs .Private.Object/.Editor.Private.Object with
        // set_target_properties(... O3DE_PRIVATE_TARGET TRUE) - default it the same way
        // BinaryType is defaulted, so a generator doesn't have to remember it per flavor.
        var plugin = new PluginWithModernLayout(CreateContext());
        plugin.Flavors[PluginFlavorKind.PrivateObject].IsPrivateTarget.Should().BeTrue();
        plugin.Flavors[PluginFlavorKind.EditorPrivateObject].IsPrivateTarget.Should().BeTrue();
    }

    [Fact]
    public void DefineFlavor_Runtime_DefaultsIsPrivateTargetToFalse()
    {
        var plugin = new TestCameraPlugin(CreateContext());
        plugin.Flavors[PluginFlavorKind.Runtime].IsPrivateTarget.Should().BeFalse();
    }

    private sealed class PluginWithMultiTargetAlias : PluginRules
    {
        public PluginWithMultiTargetAlias(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Camera");
            DefineFlavor(PluginFlavorKind.Runtime);
            CreateAlias("Clients", "Plugin::Camera", "Plugin::AudioSystem");
        }
    }

    [Fact]
    public void CreateAlias_MultiTarget_RecordsAllTargetsUnvalidated()
    {
        // Cross-plugin targets can't be checked against Flavors (that's a different plugin's rules,
        // not compiled yet) - validation happens at PluginAliasResolver.ResolveAliasTargets time.
        var plugin = new PluginWithMultiTargetAlias(CreateContext());
        plugin.MultiTargetAliases["Clients"].Should().BeEquivalentTo(["Plugin::Camera", "Plugin::AudioSystem"]);
    }

    private sealed class PluginWithSingleTargetMultiAliasAttempt : PluginRules
    {
        public PluginWithSingleTargetMultiAliasAttempt(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Camera");
            CreateAlias("Clients", "Plugin::Camera");
        }
    }

    [Fact]
    public void CreateAlias_MultiTargetOverload_SingleTarget_RecordsTarget()
    {
        var plugin = new PluginWithSingleTargetMultiAliasAttempt(CreateContext());
        plugin.MultiTargetAliases["Clients"].Should().Equal("Plugin::Camera");
    }
}

