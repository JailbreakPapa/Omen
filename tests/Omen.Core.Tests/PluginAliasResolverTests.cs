// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class PluginAliasResolverTests : IDisposable
{
    private readonly string _projectRoot;

    public PluginAliasResolverTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(PluginAliasResolverTests), Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Camera");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """{ "plugin_name": "Camera", "version": "0.1.0" }""");

        var audioSystemDir = Path.Combine(_projectRoot, "Plugins", "AudioSystem");
        Directory.CreateDirectory(audioSystemDir);
        File.WriteAllText(Path.Combine(audioSystemDir, "plugin.json"), """{ "plugin_name": "AudioSystem", "version": "0.1.0" }""");
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
            DefineFlavor(PluginFlavorKind.Runtime);
            CreateAlias("Clients", PluginFlavorKind.Runtime);
        }
    }

    [Fact]
    public void Resolve_PlainModuleName_ReturnsUnchanged()
    {
        PluginAliasResolver.Resolve("Core", []).Should().Be("Core");
    }

    [Fact]
    public void Resolve_PluginAliasReference_ReturnsConcreteModuleName()
    {
        var plugin = new TestCameraPlugin(CreateContext());
        PluginAliasResolver.Resolve("Plugin::Camera.Clients", [plugin]).Should().Be("Camera");
    }

    [Fact]
    public void Resolve_UnknownPlugin_Throws()
    {
        var act = () => PluginAliasResolver.Resolve("Plugin::Nonexistent.Clients", []);
        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown plugin*");
    }

    [Fact]
    public void Resolve_UnknownAlias_Throws()
    {
        var plugin = new TestCameraPlugin(CreateContext());
        var act = () => PluginAliasResolver.Resolve("Plugin::Camera.Servers", [plugin]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*no alias 'Servers'*");
    }

    [Fact]
    public void Resolve_MalformedReference_Throws()
    {
        var act = () => PluginAliasResolver.Resolve("Plugin::Camera", []);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Plugin::<PluginName>.<Alias>*");
    }

    private sealed class TestModernCameraPlugin : PluginRules
    {
        public TestModernCameraPlugin(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Camera");
            DefineFlavor(PluginFlavorKind.Api);
            CreateAlias("Clients.API", PluginFlavorKind.Api);
        }
    }

    [Fact]
    public void Resolve_DottedAliasReference_ReturnsConcreteModuleName()
    {
        // Real O3DE plugins alias e.g. "${plugin_name}.Clients.API" to the "${plugin_name}.API"
        // target - the alias name itself contains a dot. Resolve splits PluginName from the
        // rest on the FIRST dot only, so everything after it (including further dots)
        // passes through untouched as the alias name.
        var plugin = new TestModernCameraPlugin(CreateContext());
        PluginAliasResolver.Resolve("Plugin::Camera.Clients.API", [plugin]).Should().Be("Camera.API");
    }

    private sealed class TestAudioSystemPlugin : PluginRules
    {
        public TestAudioSystemPlugin(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/AudioSystem");
            DefineFlavor(PluginFlavorKind.Runtime);
        }
    }

    private sealed class TestCameraPluginWithMultiTargetAlias : PluginRules
    {
        public TestCameraPluginWithMultiTargetAlias(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Camera");
            DefineFlavor(PluginFlavorKind.Runtime);
            // Mirrors a real residue entry: alias `Clients` backs the plugin's own plain target
            // plus another plugin's plain target, e.g.
            // ly_create_alias(NAME ${plugin_name}.Clients TARGETS Plugin::${plugin_name} Plugin::AudioSystem)
            CreateAlias("Clients", "Plugin::Camera", "Plugin::AudioSystem");
        }
    }

    [Fact]
    public void ResolveAliasTargets_SingleFlavorAlias_ReturnsOneModuleName()
    {
        var plugin = new TestCameraPlugin(CreateContext());
        PluginAliasResolver.ResolveAliasTargets("Plugin::Camera.Clients", [plugin])
            .Should().BeEquivalentTo(["Camera"]);
    }

    [Fact]
    public void ResolveAliasTargets_MultiTargetAlias_ReturnsEveryTarget()
    {
        // This is the case Resolve() cannot express - it can only back one flavor. Without the
        // multi-target model, "Camera.AudioSystem" would be dropped entirely.
        var camera = new TestCameraPluginWithMultiTargetAlias(CreateContext());
        var audioSystem = new TestAudioSystemPlugin(CreateContext());

        PluginAliasResolver.ResolveAliasTargets("Plugin::Camera.Clients", [camera, audioSystem])
            .Should().BeEquivalentTo(["Camera", "AudioSystem"]);
    }

    [Fact]
    public void ResolveAliasTargets_PlainModuleName_ReturnsUnchanged()
    {
        PluginAliasResolver.ResolveAliasTargets("Core", []).Should().BeEquivalentTo(["Core"]);
    }

    [Fact]
    public void Resolve_MultiTargetAlias_ThrowsWithGuidanceToResolveAliasTargets()
    {
        var camera = new TestCameraPluginWithMultiTargetAlias(CreateContext());
        var audioSystem = new TestAudioSystemPlugin(CreateContext());

        var act = () => PluginAliasResolver.Resolve("Plugin::Camera.Clients", [camera, audioSystem]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*ResolveAliasTargets*");
    }
}

