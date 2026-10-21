// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class PluginEnablementTests : IDisposable
{
    private readonly string _projectRoot;

    public PluginEnablementTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(PluginEnablementTests), Guid.NewGuid().ToString("N"));
        WritePluginJson("Shared", []);
        WritePluginJson("PluginA", ["Shared"]);
        WritePluginJson("PluginB", ["Shared"]);
        WritePluginJson("Cyclic1", ["Cyclic2"]);
        WritePluginJson("Cyclic2", ["Cyclic1"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }

    private void WritePluginJson(string pluginName, string[] dependencies)
    {
        var pluginDir = Path.Combine(_projectRoot, "Plugins", pluginName);
        Directory.CreateDirectory(pluginDir);
        var deps = string.Join(",", dependencies.Select(d => $"\"{d}\""));
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"),
            $$"""{ "plugin_name": "{{pluginName}}", "version": "0.1.0", "dependencies": [{{deps}}] }""");
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

    private sealed class TestPlugin : PluginRules
    {
        public TestPlugin(BuildContext context, string pluginDirectoryName) : base(context)
        {
            LoadManifest($"Plugins/{pluginDirectoryName}");
            var runtime = DefineFlavor(PluginFlavorKind.Runtime);
            var editor = DefineFlavor(PluginFlavorKind.Editor);
            _ = runtime;
            _ = editor;
            CreateAlias("Clients", PluginFlavorKind.Runtime);
            CreateAlias("Unified", PluginFlavorKind.Runtime);
            CreateAlias("Servers", PluginFlavorKind.Editor);
            // Deliberately no "Tools" alias - proves ResolveEnabledTargets skips a plugin that
            // doesn't expose a requested variant instead of throwing or fabricating one.
        }
    }

    private IReadOnlyList<PluginRules> AllPlugins()
    {
        var ctx = CreateContext();
        return
        [
            new TestPlugin(ctx, "Shared"),
            new TestPlugin(ctx, "PluginA"),
            new TestPlugin(ctx, "PluginB"),
            new TestPlugin(ctx, "Cyclic1"),
            new TestPlugin(ctx, "Cyclic2")
        ];
    }

    [Fact]
    public void ResolveEnabledPluginNames_DiamondDependency_SharedPluginAppearsOnce()
    {
        var names = PluginEnablement.ResolveEnabledPluginNames(["PluginA", "PluginB"], AllPlugins());

        names.Where(n => n == "Shared").Should().ContainSingle();
        names.Should().Contain(["PluginA", "PluginB", "Shared"]);
        names.Should().HaveCount(3);
    }

    [Fact]
    public void ResolveEnabledPluginNames_DependencyCycle_DoesNotInfiniteLoop()
    {
        var names = PluginEnablement.ResolveEnabledPluginNames(["Cyclic1"], AllPlugins());

        names.Should().BeEquivalentTo(["Cyclic1", "Cyclic2"]);
    }

    [Fact]
    public void ResolveEnabledPluginNames_UnknownPlugin_Throws()
    {
        var act = () => PluginEnablement.ResolveEnabledPluginNames(["Nonexistent"], AllPlugins());
        act.Should().Throw<InvalidOperationException>().WithMessage("*Nonexistent*");
    }

    [Fact]
    public void ResolveEnabledTargets_ClientsVariant_ResolvesRuntimeTargets()
    {
        var targets = PluginEnablement.ResolveEnabledTargets(["PluginA", "PluginB"], PluginVariant.Clients, AllPlugins());

        targets.Should().BeEquivalentTo(["PluginA", "Shared", "PluginB"]);
    }

    [Fact]
    public void ResolveEnabledTargets_ServersVariant_ResolvesDifferentTargetsThanClients()
    {
        // The entire point of the variant axis: Clients and Servers must not resolve to the
        // same set of concrete targets for the same enabled-plugin list.
        var targets = PluginEnablement.ResolveEnabledTargets(["PluginA", "PluginB"], PluginVariant.Servers, AllPlugins());

        targets.Should().BeEquivalentTo(["PluginA.Editor", "Shared.Editor", "PluginB.Editor"]);
    }

    [Fact]
    public void ResolveEnabledTargets_VariantNotAliased_SkipsPluginSilently()
    {
        var targets = PluginEnablement.ResolveEnabledTargets(["PluginA"], PluginVariant.Tools, AllPlugins());

        targets.Should().BeEmpty();
    }

    [Fact]
    public void ResolveEnabledTargets_DiamondDependency_TargetAppearsOnce()
    {
        var targets = PluginEnablement.ResolveEnabledTargets(["PluginA", "PluginB"], PluginVariant.Unified, AllPlugins());

        targets.Where(t => t == "Shared").Should().ContainSingle();
    }
}
