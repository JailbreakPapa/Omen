// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

/// <summary>
/// Regression coverage for the Round 9 flavor-suffix-tail decision (see
/// docs/superpowers/sdd/omen-flavor-suffix-tail-report.md): first-class PluginFlavorKind members
/// for the RHI-backend-family suffixes that recur across 6-14 plugins each (Reflect, Private,
/// Private.Static, Interface, Public, Headless, Builders, Builders.Static, Edit, Mocks, Headers,
/// Tools.Static, Tests.Static - confirmed against the real Atom/RHI*/Atom/Feature/Common
/// CMakeLists.txt), plus the PluginFlavorKind.Custom escape hatch for suffixes that are genuinely
/// one plugin's own business (ScriptCanvas's "${plugin_name}Debugger" product family, Atom_RHI's
/// ".Profiler"/".Profiler.RenderDoc"/".Profiler.PIX" trio). None of this compiles against the
/// pre-Round-9 PluginRules.cs - the enum members and the DefineFlavor(string, PluginModuleRole)
/// overload don't exist there.
/// </summary>
public class PluginFlavorSuffixTailTests : IDisposable
{
    private readonly string _projectRoot;

    public PluginFlavorSuffixTailTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(PluginFlavorSuffixTailTests), Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Atom_RHI_DX12");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
        { "plugin_name": "Atom_RHI_DX12", "version": "0.1.0", "dependencies": [] }
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

    // Mirrors the real Atom_RHI_DX12 CMakeLists.txt shape: Reflect/Private.Static/Interface
    // (always STATIC) + Private/Builders (PAL-driven module type) + Builders.Static (STATIC).
    private sealed class TestRhiBackendPlugin : PluginRules
    {
        public TestRhiBackendPlugin(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Atom_RHI_DX12");

            DefineFlavor(PluginFlavorKind.Reflect);
            DefineFlavor(PluginFlavorKind.PrivateStatic);
            DefineFlavor(PluginFlavorKind.Interface);
            DefineFlavor(PluginFlavorKind.Private);
            DefineFlavor(PluginFlavorKind.Public);
            DefineFlavor(PluginFlavorKind.Headless);
            DefineFlavor(PluginFlavorKind.Builders);
            DefineFlavor(PluginFlavorKind.BuildersStatic);
            DefineFlavor(PluginFlavorKind.Edit);
            DefineFlavor(PluginFlavorKind.Mocks);
            DefineFlavor(PluginFlavorKind.Headers);
            DefineFlavor(PluginFlavorKind.ToolsStatic);
            DefineFlavor(PluginFlavorKind.TestsStatic);
        }
    }

    [Fact]
    public void DefineFlavor_AlwaysStaticRhiKinds_DefaultBinaryTypeToStaticLibrary()
    {
        // Reflect/Private.Static/Interface/Builders.Static/Tools.Static/Tests.Static are STATIC
        // in every real O3DE instance read (Atom/RHI/DX12, /Vulkan, /Metal, /Null, /AGC) - same
        // "never what a plugin author meant otherwise" reasoning DefineFlavor already applies to
        // Static/PrivateObject.
        var plugin = new TestRhiBackendPlugin(CreateContext());
        plugin.Flavors[PluginFlavorKind.Reflect].BinaryType.Should().Be(TargetType.StaticLibrary);
        plugin.Flavors[PluginFlavorKind.PrivateStatic].BinaryType.Should().Be(TargetType.StaticLibrary);
        plugin.Flavors[PluginFlavorKind.Interface].BinaryType.Should().Be(TargetType.StaticLibrary);
        plugin.Flavors[PluginFlavorKind.BuildersStatic].BinaryType.Should().Be(TargetType.StaticLibrary);
        plugin.Flavors[PluginFlavorKind.ToolsStatic].BinaryType.Should().Be(TargetType.StaticLibrary);
        plugin.Flavors[PluginFlavorKind.TestsStatic].BinaryType.Should().Be(TargetType.StaticLibrary);
    }

    [Fact]
    public void DefineFlavor_HeaderOnlyRhiKinds_DefaultBinaryTypeToHeaderOnly()
    {
        var plugin = new TestRhiBackendPlugin(CreateContext());
        plugin.Flavors[PluginFlavorKind.Mocks].BinaryType.Should().Be(TargetType.HeaderOnly);
        plugin.Flavors[PluginFlavorKind.Headers].BinaryType.Should().Be(TargetType.HeaderOnly);
    }

    [Fact]
    public void DefineFlavor_PalDrivenRhiKinds_LeaveBinaryTypeUnsetByDefault()
    {
        // Private/Public/Headless/Builders/Edit vary in real shape by plugin/platform
        // (${PAL_TRAIT_MONOLITHIC_DRIVEN_*_TYPE}, or STATIC vs library-type for Edit) - same
        // "plugin author's call" treatment Runtime/Editor/Tools already get.
        var plugin = new TestRhiBackendPlugin(CreateContext());
        plugin.Flavors[PluginFlavorKind.Private].BinaryType.Should().BeNull();
        plugin.Flavors[PluginFlavorKind.Public].BinaryType.Should().BeNull();
        plugin.Flavors[PluginFlavorKind.Headless].BinaryType.Should().BeNull();
        plugin.Flavors[PluginFlavorKind.Builders].BinaryType.Should().BeNull();
        plugin.Flavors[PluginFlavorKind.Edit].BinaryType.Should().BeNull();
    }

    private sealed class TestPluginWithCustomFlavors : PluginRules
    {
        public TestPluginWithCustomFlavors(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Atom_RHI_DX12");

            // Mirrors the real Atom_RHI plugin's one-off ".Profiler"/".Profiler.RenderDoc"/
            // ".Profiler.PIX" trio - genuinely this plugin's own business, not a recurring O3DE
            // concept, so no first-class PluginFlavorKind was added for it.
            var profiler = DefineFlavor(".Profiler");
            profiler.BinaryType = TargetType.StaticLibrary;

            var profilerRenderDoc = DefineFlavor(".Profiler.RenderDoc", PluginModuleRole.Editor);
            profilerRenderDoc.PrivateDependencies.Add($"{Name}.Profiler");

            var profilerPix = DefineFlavor(".Profiler.PIX", PluginModuleRole.Editor);
            profilerPix.PrivateDependencies.Add($"{Name}.Profiler");

            CreateCustomAlias("Profilers", ".Profiler");
        }
    }

    [Fact]
    public void DefineFlavor_CustomSuffix_RegistersInCustomFlavorsKeyedBySuffix()
    {
        // A single plugin defining 2+ Custom flavors must not collide - that's exactly why they
        // live in CustomFlavors (keyed by suffix) rather than sharing the one
        // PluginFlavorKind.Custom slot in the enum-keyed Flavors dictionary.
        var plugin = new TestPluginWithCustomFlavors(CreateContext());
        plugin.CustomFlavors.Should().ContainKey(".Profiler");
        plugin.CustomFlavors.Should().ContainKey(".Profiler.RenderDoc");
        plugin.CustomFlavors.Should().ContainKey(".Profiler.PIX");
        plugin.CustomFlavors[".Profiler"].Kind.Should().Be(PluginFlavorKind.Custom);
        plugin.CustomFlavors[".Profiler"].CustomName.Should().Be(".Profiler");
        plugin.Flavors.Should().NotContainKey(PluginFlavorKind.Custom);
    }

    [Fact]
    public void DefineFlavor_CustomSuffix_DefaultsRoleToRuntime()
    {
        var plugin = new TestPluginWithCustomFlavors(CreateContext());
        plugin.CustomFlavors[".Profiler"].Role.Should().Be(PluginModuleRole.Runtime);
        plugin.CustomFlavors[".Profiler.RenderDoc"].Role.Should().Be(PluginModuleRole.Editor);
    }

    [Fact]
    public void CreateCustomAlias_MapsAliasToCustomFlavorSuffix()
    {
        var plugin = new TestPluginWithCustomFlavors(CreateContext());
        plugin.CustomAliases["Profilers"].Should().Be(".Profiler");
    }

    private sealed class PluginWithBadCustomAlias : PluginRules
    {
        public PluginWithBadCustomAlias(BuildContext context) : base(context)
        {
            LoadManifest("Plugins/Atom_RHI_DX12");
            CreateCustomAlias("Profilers", ".Profiler"); // ".Profiler" custom flavor never defined
        }
    }

    [Fact]
    public void CreateCustomAlias_UndefinedCustomFlavor_Throws()
    {
        var act = () => new PluginWithBadCustomAlias(CreateContext());
        act.Should().Throw<InvalidOperationException>().WithMessage("*undefined custom flavor*");
    }

    [Fact]
    public async Task CreateModuleRules_DottedStaticHalfKinds_NameAsRealO3deTargets()
    {
        // PluginFlavorNaming special-cases these back to the real dotted O3DE target names, the
        // same way it already does for EditorTests/Api/EditorApi/PrivateObject/
        // EditorPrivateObject/EditorStatic.
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Widget");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """{ "plugin_name": "Widget", "version": "0.1.0" }""");
        File.WriteAllText(Path.Combine(pluginDir, "Widget.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class WidgetPlugin : PluginRules
        {
            public WidgetPlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Widget");
                DefineFlavor(PluginFlavorKind.Reflect);
                DefineFlavor(PluginFlavorKind.PrivateStatic);
                DefineFlavor(PluginFlavorKind.BuildersStatic);
                DefineFlavor(PluginFlavorKind.ToolsStatic);
                DefineFlavor(PluginFlavorKind.TestsStatic);
            }
        }
        """);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);
        var modules = compiledRules.CreateModuleRules(CreateContext());

        modules.Should().Contain(m => m.Name == "Widget.Reflect");
        modules.Should().Contain(m => m.Name == "Widget.Private.Static");
        modules.Should().Contain(m => m.Name == "Widget.Builders.Static");
        modules.Should().Contain(m => m.Name == "Widget.Tools.Static");
        modules.Should().Contain(m => m.Name == "Widget.Tests.Static");

        // TestsStatic is Tests' static half - same Test tier as Tests itself.
        modules.Single(m => m.Name == "Widget.Tests.Static").Type.Should().Be(ModuleType.Test);
    }

    [Fact]
    public async Task CreateModuleRules_HostToolsGatedRhiKinds_ClassifyAsEditor()
    {
        // Builders/Builders.Static/Edit are always wrapped in
        // if(PAL_TRAIT_BUILD_HOST_TOOLS) in the real trees - same Editor tier as
        // Editor/EditorApi/EditorPrivateObject/EditorStatic.
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Widget2");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """{ "plugin_name": "Widget2", "version": "0.1.0" }""");
        File.WriteAllText(Path.Combine(pluginDir, "Widget2.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class Widget2Plugin : PluginRules
        {
            public Widget2Plugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Widget2");
                DefineFlavor(PluginFlavorKind.Builders);
                DefineFlavor(PluginFlavorKind.BuildersStatic);
                DefineFlavor(PluginFlavorKind.Edit);
            }
        }
        """);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);
        var modules = compiledRules.CreateModuleRules(CreateContext());

        modules.Single(m => m.Name == "Widget2.Builders").Type.Should().Be(ModuleType.Editor);
        modules.Single(m => m.Name == "Widget2.Builders.Static").Type.Should().Be(ModuleType.Editor);
        modules.Single(m => m.Name == "Widget2.Edit").Type.Should().Be(ModuleType.Editor);
    }

    [Fact]
    public async Task CreateModuleRules_CustomFlavor_NamesWithLiteralSuffixAndRespectsRole()
    {
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "ScriptCanvas");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """{ "plugin_name": "ScriptCanvas", "version": "0.1.0" }""");
        // Mirrors ScriptCanvas's real "${plugin_name}Debugger" second-product-family residue item:
        // a suffix that isn't even "{plugin}.{suffix}" shaped, entirely this plugin's own business.
        File.WriteAllText(Path.Combine(pluginDir, "ScriptCanvas.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class ScriptCanvasPlugin : PluginRules
        {
            public ScriptCanvasPlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/ScriptCanvas");
                var debugger = DefineFlavor("Debugger", PluginModuleRole.Editor);
                debugger.BinaryType = TargetType.StaticLibrary;
                CreateCustomAlias("DebuggerBuilders", "Debugger");
            }
        }
        """);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);
        var modules = compiledRules.CreateModuleRules(CreateContext());

        var debugger = modules.Single(m => m.Name == "ScriptCanvasDebugger");
        debugger.Type.Should().Be(ModuleType.Editor);
        debugger.BinaryType.Should().Be(TargetType.StaticLibrary);

        var plugins = compiledRules.CreatePluginRules(CreateContext());
        var plugin = plugins.Single(g => g.Name == "ScriptCanvas");
        PluginAliasResolver.Resolve("Plugin::ScriptCanvas.DebuggerBuilders", plugins)
            .Should().Be("ScriptCanvasDebugger");
    }
}
