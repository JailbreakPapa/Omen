// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

/// <summary>
/// Covers PluginEnablement.FilterEnabledModules - the piece BuildOrchestrator and
/// ProjectGenerationOrchestrator both call to turn an enabled-plugins list into an actual module
/// filter. Uses real compiled ModuleRules/PluginFlavorModuleRules (via RuleCompiler), not hand-built
/// stand-ins, since PluginFlavorModuleRules is internal to Omen.Core and can only be produced by
/// CompiledRules.CreateModuleRules.
/// </summary>
public class PluginEnablementFilterTests : IDisposable
{
    private readonly string _projectRoot;

    public PluginEnablementFilterTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(PluginEnablementFilterTests), Guid.NewGuid().ToString("N"));

        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Widget");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
        { "plugin_name": "Widget", "version": "0.1.0", "dependencies": [] }
        """);
        File.WriteAllText(Path.Combine(pluginDir, "Widget.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class WidgetPlugin : PluginRules
        {
            public WidgetPlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Widget");
                DefineFlavor(PluginFlavorKind.Runtime);
                CreateAlias("Clients", PluginFlavorKind.Runtime);
                CreateAlias("Unified", PluginFlavorKind.Runtime);
            }
        }
        """);

        var beaconDir = Path.Combine(_projectRoot, "Plugins", "Beacon");
        Directory.CreateDirectory(beaconDir);
        File.WriteAllText(Path.Combine(beaconDir, "plugin.json"), """
        { "plugin_name": "Beacon", "version": "0.1.0", "dependencies": [] }
        """);
        File.WriteAllText(Path.Combine(beaconDir, "Beacon.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class BeaconPlugin : PluginRules
        {
            public BeaconPlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Beacon");
                DefineFlavor(PluginFlavorKind.Runtime);
                CreateAlias("Clients", PluginFlavorKind.Runtime);
                CreateAlias("Unified", PluginFlavorKind.Runtime);
            }
        }
        """);

        // A hand-written module - never something ly_enable_plugins governed, so it must survive
        // every enabled-plugins filter regardless of whether Widget is enabled.
        var coreDir = Path.Combine(_projectRoot, "Source", "Core");
        Directory.CreateDirectory(coreDir);
        File.WriteAllText(Path.Combine(_projectRoot, "Core.module.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CoreModule : ModuleRules
        {
            public CoreModule(BuildContext context) : base(context)
            {
                SourceDirectory = "Source/Core";
            }
        }
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

    private async Task<CompiledRules> CompileAsync() =>
        await new RuleCompiler(Path.Combine(_projectRoot, "RuleCache")).CompileRulesAsync(_projectRoot);

    [Fact]
    public async Task FilterEnabledModules_NullEnabledPlugins_ReturnsEveryModuleUnchanged()
    {
        // The backward-compatibility requirement: no enabled-plugins list declared anywhere means
        // no filtering at all, exactly like before PluginEnablement was wired into anything.
        var compiledRules = await CompileAsync();
        var context = CreateContext();
        var modules = compiledRules.CreateModuleRules(context);

        var filtered = PluginEnablement.FilterEnabledModules(modules, null, PluginVariant.Unified, compiledRules.CreatePluginRules(context));

        filtered.Should().BeEquivalentTo(modules);
        filtered.Should().Contain(m => m.Name == "CoreModule");
        filtered.Should().Contain(m => m.Name == "Widget");
    }

    [Fact]
    public async Task FilterEnabledModules_EmptyEnabledPlugins_DropsPluginFlavorsButKeepsHandWrittenModules()
    {
        // An explicitly empty list is "enable nothing", not "unfiltered" - the hand-written
        // module (never something ly_enable_plugins governs) still survives.
        var compiledRules = await CompileAsync();
        var context = CreateContext();
        var modules = compiledRules.CreateModuleRules(context);

        var filtered = PluginEnablement.FilterEnabledModules(modules, [], PluginVariant.Unified, compiledRules.CreatePluginRules(context));

        filtered.Should().Contain(m => m.Name == "CoreModule");
        filtered.Should().NotContain(m => m.Name == "Widget");
    }

    [Fact]
    public async Task FilterEnabledModules_PluginEnabledForRequestedVariant_KeepsItsResolvedTarget()
    {
        var compiledRules = await CompileAsync();
        var context = CreateContext();
        var modules = compiledRules.CreateModuleRules(context);

        var filtered = PluginEnablement.FilterEnabledModules(modules, ["Widget"], PluginVariant.Clients, compiledRules.CreatePluginRules(context));

        filtered.Should().Contain(m => m.Name == "CoreModule");
        filtered.Should().Contain(m => m.Name == "Widget");
    }

    [Fact]
    public async Task FilterEnabledModules_OnlyOnePluginEnabled_DropsTheOtherPluginsFlavorsOnly()
    {
        var compiledRules = await CompileAsync();
        var context = CreateContext();
        var modules = compiledRules.CreateModuleRules(context);

        var filtered = PluginEnablement.FilterEnabledModules(modules, ["Beacon"], PluginVariant.Unified, compiledRules.CreatePluginRules(context));

        filtered.Should().Contain(m => m.Name == "CoreModule");
        filtered.Should().Contain(m => m.Name == "Beacon");
        filtered.Should().NotContain(m => m.Name == "Widget");
    }
}
