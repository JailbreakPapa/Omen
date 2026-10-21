// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class RuleCompilerPluginTests : IDisposable
{
    private readonly string _projectRoot;

    public RuleCompilerPluginTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleCompilerPluginTests), Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Camera");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
        { "plugin_name": "Camera", "version": "0.1.0", "dependencies": [] }
        """);
        File.WriteAllText(Path.Combine(pluginDir, "Camera.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Camera");

                DefineFlavor(PluginFlavorKind.Static);

                var runtime = DefineFlavor(PluginFlavorKind.Runtime);
                runtime.BinaryType = TargetType.SharedLibrary;
                runtime.PrivateDependencies.Add(Name + ".Static");
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

    [Fact]
    public async Task CompileRulesAsync_TwoCompilersSharingOneCache_BothSucceed()
    {
        // `omen generate` run while `omen build` is running: both miss the same cache key and both
        // write it. That used to kill one of them during rule compilation with "The process cannot
        // access the file ... because it is being used by another process", before it did any work.
        var cache = Path.Combine(_projectRoot, "RuleCache");

        var first = new RuleCompiler(cache).CompileRulesAsync(_projectRoot);
        var second = new RuleCompiler(cache).CompileRulesAsync(_projectRoot);

        var results = await Task.WhenAll(first, second);

        results[0].Should().NotBeNull();
        results[1].Should().NotBeNull();
    }

    [Fact]
    public async Task CompileRulesAsync_DiscoversPluginFiles()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var plugins = compiledRules.CreatePluginRules(CreateContext());

        plugins.Should().ContainSingle(g => g.Name == "Camera");
    }

    [Fact]
    public async Task CreateModuleRules_IncludesOneModulePerPluginFlavor()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext());

        modules.Should().Contain(m => m.Name == "Camera.Static");
        modules.Should().Contain(m => m.Name == "Camera" && m.BinaryType == TargetType.SharedLibrary);
        modules.First(m => m.Name == "Camera").PrivateDependencies.Should().Contain("Camera.Static");
    }
}

/// <summary>
/// Regression coverage for plugin test flavors: real O3DE plugins (e.g. Plugins/AFR, Plugins/Archive)
/// declare a separate ${plugin_name}.Tests and ${plugin_name}.Editor.Tests target, each with its
/// own sources/dependencies/ModuleType, privately linking the plugin's own Static/Editor
/// flavor (O3DE's ${plugin_name}.Private.Object / ${plugin_name}.Editor.Private.Object) instead
/// of the shared runtime module. Without PluginFlavorKind.Tests/EditorTests, a generator
/// transcribing that CMakeLists.txt has nowhere to put the second target - it would either
/// be dropped or silently overwrite the first one in PluginRules.Flavors.
/// </summary>
public class RuleCompilerPluginTestFlavorTests : IDisposable
{
    private readonly string _projectRoot;

    public RuleCompilerPluginTestFlavorTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleCompilerPluginTestFlavorTests), Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Camera");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
        { "plugin_name": "Camera", "version": "0.1.0", "dependencies": [] }
        """);
        File.WriteAllText(Path.Combine(pluginDir, "Camera.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Camera");

                var staticFlavor = DefineFlavor(PluginFlavorKind.Static);
                staticFlavor.SourceFiles.Add("Camera.cpp");

                var editor = DefineFlavor(PluginFlavorKind.Editor);
                editor.BinaryType = TargetType.SharedLibrary;
                editor.SourceFiles.Add("CameraEditorModule.cpp");
                editor.PrivateDependencies.Add(Name + ".Static");

                var tests = DefineFlavor(PluginFlavorKind.Tests);
                tests.SourceFiles.Add("RuntimeTests.cpp");
                tests.PrivateDependencies.Add("AZ::AzTest");
                tests.PrivateDependencies.Add(Name + ".Static");

                var editorTests = DefineFlavor(PluginFlavorKind.EditorTests);
                editorTests.SourceFiles.Add("EditorTests.cpp");
                editorTests.PrivateDependencies.Add("AZ::AzTest");
                editorTests.PrivateDependencies.Add(Name + ".Editor");
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

    [Fact]
    public async Task CreateModuleRules_TestsAndEditorTests_ProduceTwoDistinctModules()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext());

        // Both test flavors survive - neither overwrote the other in plugin.Flavors.
        var runtimeTests = modules.Single(m => m.Name == "Camera.Tests");
        // Named to match O3DE's ${plugin_name}.Editor.Tests exactly (not "Camera.EditorTests"),
        // since downstream tooling (CTest, seed lists, AzTestRunner) keys off the exact name.
        var editorTests = modules.Single(m => m.Name == "Camera.Editor.Tests");

        // ly_add_googletest is registration, not a new build shape: Omen models "this module
        // is a discoverable test suite" by reusing the pre-existing ModuleType.Test value,
        // not a new flag.
        runtimeTests.Type.Should().Be(ModuleType.Test);
        editorTests.Type.Should().Be(ModuleType.Test);

        // No cross-contamination: each test flavor only carries its own sources...
        runtimeTests.SourceFiles.Should().ContainSingle().Which.Should().Be("RuntimeTests.cpp");
        editorTests.SourceFiles.Should().ContainSingle().Which.Should().Be("EditorTests.cpp");

        // ...and links the plugin's own object-producing flavor privately (Omen's stand-in for
        // O3DE's ${plugin_name}.Private.Object / ${plugin_name}.Editor.Private.Object), not each
        // other's runtime module.
        runtimeTests.PrivateDependencies.Should().Contain("Camera.Static");
        runtimeTests.PrivateDependencies.Should().NotContain("Camera.Editor");
        editorTests.PrivateDependencies.Should().Contain("Camera.Editor");
        editorTests.PrivateDependencies.Should().NotContain("Camera.Static");
    }
}

/// <summary>
/// Regression coverage for the real O3DE Camera plugin's CMakeLists.txt vs its hand-written
/// Camera.plugin.cs: BUILD_DEPENDENCIES PUBLIC entries (Plugin::Atom_RPI.Public, AZ::AtomCore, etc.)
/// are transitive - consumers of Camera.Static must inherit them - but PluginFlavor previously
/// had no Public* surface at all, so a generator faithfully transcribing that CMakeLists.txt
/// had nowhere to put them.
/// </summary>
public class RuleCompilerPluginPublicSurfaceTests : IDisposable
{
    private readonly string _projectRoot;

    public RuleCompilerPluginPublicSurfaceTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleCompilerPluginPublicSurfaceTests), Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Camera");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
        { "plugin_name": "Camera", "version": "0.1.0", "dependencies": [] }
        """);
        File.WriteAllText(Path.Combine(pluginDir, "Camera.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Camera");

                var staticFlavor = DefineFlavor(PluginFlavorKind.Static);
                staticFlavor.PublicDependencies.Add("Plugin::Atom_RPI.Public");
                staticFlavor.PublicDependencies.Add("AZ::AzCore");
                staticFlavor.PublicIncludePaths.Add("Public");
                staticFlavor.PublicDefinitions.Add("CAMERA_PUBLIC_DEF");
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

    [Fact]
    public async Task CreateModuleRules_PropagatesPluginFlavorPublicSurfaceToSynthesizedModule()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext());
        var staticModule = modules.Single(m => m.Name == "Camera.Static");

        // The synthesized module (what a consumer/downstream module actually links against
        // and reads dependency data from) must carry the flavor's public surface, not just
        // the private one PluginFlavorModuleRules forwarded before.
        staticModule.PublicDependencies.Should().Contain("Plugin::Atom_RPI.Public");
        staticModule.PublicDependencies.Should().Contain("AZ::AzCore");
        staticModule.PublicIncludePaths.Should().Contain("Public");
        staticModule.PublicDefinitions.Should().Contain("CAMERA_PUBLIC_DEF");
    }
}

/// <summary>
/// Regression coverage for O3DE's modern four-target plugin layout (21 of 95 plugins, e.g.
/// Plugins/Archive, Plugins/AFR): `${plugin_name}.API` (INTERFACE) + `${plugin_name}.Private.Object`
/// (STATIC, the real TUs) + `${plugin_name}` (the module, PUBLIC-depends on .API, PRIVATE on
/// .Private.Object), mirrored for Editor. Unlike the classic Static/Runtime/Editor layout,
/// these plugins have no `.Static` target at all - without PluginFlavorKind.Api/EditorApi/
/// PrivateObject/EditorPrivateObject, a generator transcribing this shape has nowhere to put
/// the split and a ".Private.Object -> .Static" rewrite would dangle.
/// </summary>
public class RuleCompilerPluginModernLayoutTests : IDisposable
{
    private readonly string _projectRoot;

    public RuleCompilerPluginModernLayoutTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleCompilerPluginModernLayoutTests), Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Archive");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
        { "plugin_name": "Archive", "version": "0.1.0", "dependencies": [] }
        """);
        // Public headers only for the API flavor - a stray .cpp here would be the wrong test
        // for "HeaderOnly compiles nothing" (extension filtering would mask the real bug).
        File.WriteAllText(Path.Combine(pluginDir, "ArchiveApi.h"), "#pragma once\n");
        File.WriteAllText(Path.Combine(pluginDir, "Archive.cpp"), "// tu\n");
        File.WriteAllText(Path.Combine(pluginDir, "Archive.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class ArchivePlugin : PluginRules
        {
            public ArchivePlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Archive");

                var api = DefineFlavor(PluginFlavorKind.Api);
                api.SourceFiles.Add("ArchiveApi.h");
                api.PublicIncludePaths.Add(".");

                var privateObject = DefineFlavor(PluginFlavorKind.PrivateObject);
                privateObject.SourceFiles.Add("Archive.cpp");
                privateObject.PublicDependencies.Add(Name + ".API");

                var runtime = DefineFlavor(PluginFlavorKind.Runtime);
                runtime.BinaryType = TargetType.SharedLibrary;
                runtime.PublicDependencies.Add(Name + ".API");
                runtime.PrivateDependencies.Add(Name + ".Private.Object");

                CreateAlias("Clients.API", PluginFlavorKind.Api);
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

    [Fact]
    public async Task CreateModuleRules_ModernLayout_ProducesCorrectlyNamedModules()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext());

        // Exact O3DE target names, dots and all - not "Archive.Api"/"Archive.PrivateObject".
        var api = modules.Single(m => m.Name == "Archive.API");
        var privateObject = modules.Single(m => m.Name == "Archive.Private.Object");
        var runtime = modules.Single(m => m.Name == "Archive");

        api.BinaryType.Should().Be(TargetType.HeaderOnly);
        privateObject.BinaryType.Should().Be(TargetType.StaticLibrary);
        runtime.BinaryType.Should().Be(TargetType.SharedLibrary);

        runtime.PublicDependencies.Should().Contain("Archive.API");
        runtime.PrivateDependencies.Should().Contain("Archive.Private.Object");
        // No dangling ".Static" reference for a layout that never had one.
        runtime.PrivateDependencies.Should().NotContain("Archive.Static");
    }

    [Fact]
    public async Task CreateModuleRules_PrivateObjectFlavor_ForwardsIsPrivateTargetToSynthesizedModule()
    {
        // O3DE's set_target_properties(Archive.Private.Object PROPERTIES O3DE_PRIVATE_TARGET
        // TRUE) - PluginFlavor.IsPrivateTarget defaults true for PrivateObject and must reach the
        // synthesized module the same way BinaryType already does. Archive.Runtime is not a
        // private target, so it must default false rather than inheriting true from anywhere.
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext());

        modules.Single(m => m.Name == "Archive.Private.Object").IsPrivateTarget.Should().BeTrue();
        modules.Single(m => m.Name == "Archive").IsPrivateTarget.Should().BeFalse();
    }
}

/// <summary>
/// Covers the four gaps closed on top of the modern-layout/test-flavor work: platform
/// trait gating, RUNTIME_DEPENDENCIES forwarding, the classic layout's ".Editor.Static",
/// and PluginFlavor's Qt surface - see docs/superpowers/sdd/omen-plugin-platform-surface-report.md.
/// </summary>
public class RuleCompilerPluginPlatformSurfaceTests : IDisposable
{
    private readonly string _projectRoot;

    public RuleCompilerPluginPlatformSurfaceTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleCompilerPluginPlatformSurfaceTests), Guid.NewGuid().ToString("N"));
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

                DefineFlavor(PluginFlavorKind.Static);

                var editorStatic = DefineFlavor(PluginFlavorKind.EditorStatic);
                editorStatic.QtVersion = QtVersion.Qt6;
                editorStatic.QtModules.Add("Widgets");
                editorStatic.EnableRcc = false;
                editorStatic.UseUnityBuild = true;
                editorStatic.UnityBuildExclusions.Add("Source/Odd.cpp");
                editorStatic.ThirdPartyDependencies.Add("3rdParty::Lua");
                editorStatic.PlatformIncludeFiles.Add("Platform/Common/${PAL_TRAIT_COMPILER_ID}/widget_editor_${PAL_TRAIT_COMPILER_ID_LOWERCASE}.cmake");

                var editor = DefineFlavor(PluginFlavorKind.Editor);
                editor.BinaryType = TargetType.SharedLibrary;
                editor.PrivateDependencies.Add(Name + ".Editor.Static");
                editor.RuntimeDependencies.Add("Legacy::EditorCommon");
                editor.RequiredTraits.Add("PAL_TRAIT_BUILD_HOST_TOOLS");
            }
        }
        """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }

    private BuildContext CreateContext(IReadOnlyDictionary<string, bool>? platformTraits = null) => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = _projectRoot,
        OutputDirectory = Path.Combine(_projectRoot, "Binaries"),
        IntermediateDirectory = Path.Combine(_projectRoot, "Intermediate"),
        PlatformTraits = platformTraits ?? new Dictionary<string, bool>()
    };

    [Fact]
    public async Task CreateModuleRules_EditorStaticFlavor_ExpandsWithClassicLayoutNameAndStaticLibraryType()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        // Satisfy the Editor flavor's gate so both flavors are present to inspect.
        var modules = compiledRules.CreateModuleRules(CreateContext(new Dictionary<string, bool> { ["PAL_TRAIT_BUILD_HOST_TOOLS"] = true }));

        var editorStatic = modules.Single(m => m.Name == "Widget.Editor.Static");
        editorStatic.BinaryType.Should().Be(Omen.Core.Configuration.TargetType.StaticLibrary);

        var editor = modules.Single(m => m.Name == "Widget.Editor");
        editor.PrivateDependencies.Should().Contain("Widget.Editor.Static");
    }

    [Fact]
    public async Task CreateModuleRules_RequiredTraitKnownFalse_FlavorProducesNoModule()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext(new Dictionary<string, bool> { ["PAL_TRAIT_BUILD_HOST_TOOLS"] = false }));

        modules.Should().NotContain(m => m.Name == "Widget.Editor");
        // The ungated Static flavor is unaffected.
        modules.Should().Contain(m => m.Name == "Widget.Static");
    }

    [Fact]
    public async Task CreateModuleRules_RequiredTraitKnownTrue_FlavorProducesModule()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext(new Dictionary<string, bool> { ["PAL_TRAIT_BUILD_HOST_TOOLS"] = true }));

        modules.Should().Contain(m => m.Name == "Widget.Editor");
    }

    [Fact]
    public async Task CreateModuleRules_RequiredTraitUnknown_DefaultsToIncluded()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        // No entry for PAL_TRAIT_BUILD_HOST_TOOLS at all - "unknown" defaults to satisfied.
        var modules = compiledRules.CreateModuleRules(CreateContext());

        modules.Should().Contain(m => m.Name == "Widget.Editor");
    }

    [Fact]
    public async Task CreateModuleRules_RuntimeDependenciesAndQtFlags_SurviveToSynthesizedModule()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext(new Dictionary<string, bool> { ["PAL_TRAIT_BUILD_HOST_TOOLS"] = true }));

        var editor = modules.Single(m => m.Name == "Widget.Editor");
        editor.RuntimeDependencies.Should().ContainSingle().Which.Should().Be("Legacy::EditorCommon");

        var editorStatic = modules.Single(m => m.Name == "Widget.Editor.Static");
        editorStatic.QtVersion.Should().Be(Omen.Core.Configuration.QtVersion.Qt6);
        editorStatic.QtModules.Should().Contain("Widgets");
        editorStatic.EnableRcc.Should().BeFalse();
        editorStatic.UseUnityBuild.Should().BeTrue();
        editorStatic.UnityBuildExclusions.Should().ContainSingle().Which.Should().Be("Source/Odd.cpp");
        modules.Single(m => m.Name == "Widget.Editor").UseUnityBuild.Should().BeNull();
        editorStatic.EnableMoc.Should().BeTrue(); // untouched default carried through
        editorStatic.IsQtProject.Should().BeTrue();
        // 3rdParty:: dependencies are recorded-only (see ModuleRules.ThirdPartyDependencies) but
        // must still survive PluginFlavor -> synthesized-module forwarding, same as RuntimeDependencies.
        editorStatic.ThirdPartyDependencies.Should().ContainSingle().Which.Should().Be("3rdParty::Lua");
        // Never leaked into the lists ActionGraphBuilder actually links against.
        editorStatic.PublicDependencies.Should().NotContain("3rdParty::Lua");
        editorStatic.PrivateDependencies.Should().NotContain("3rdParty::Lua");
        // PLATFORM_INCLUDE_FILES entries are recorded-only (see ModuleRules.PlatformIncludeFiles)
        // but must still survive PluginFlavor -> synthesized-module forwarding, same as
        // RuntimeDependencies/ThirdPartyDependencies above.
        editorStatic.PlatformIncludeFiles.Should().ContainSingle()
            .Which.Should().Be("Platform/Common/${PAL_TRAIT_COMPILER_ID}/widget_editor_${PAL_TRAIT_COMPILER_ID_LOWERCASE}.cmake");
    }
}

/// <summary>
/// O3DE's target_link_options (e.g. HoudiniEngine's /DELAYLOAD:libHAPIL.dll) had no PluginFlavor spelling.
/// </summary>
public class RuleCompilerPluginLinkerFlagTests : IDisposable
{
    private readonly string _projectRoot;

    public RuleCompilerPluginLinkerFlagTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleCompilerPluginLinkerFlagTests), Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Houdini");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
        { "plugin_name": "Houdini", "version": "0.1.0", "dependencies": [] }
        """);
        File.WriteAllText(Path.Combine(pluginDir, "Houdini.plugin.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class HoudiniPlugin : PluginRules
        {
            public HoudiniPlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Houdini");

                var editor = DefineFlavor(PluginFlavorKind.Editor);
                editor.AdditionalLinkerFlags.Add("/DELAYLOAD:libHAPIL.dll");
            }
        }
        """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }

    [Fact]
    public async Task CreateModuleRules_ForwardsFlavorLinkerFlagsToSynthesizedModule()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(new BuildContext
        {
            Platform = TargetPlatform.Windows,
            Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Debug,
            ProjectRoot = _projectRoot,
            OutputDirectory = Path.Combine(_projectRoot, "Binaries"),
            IntermediateDirectory = Path.Combine(_projectRoot, "Intermediate")
        });

        modules.Single(m => m.Name == "Houdini.Editor").AdditionalLinkerFlags.Should().Contain("/DELAYLOAD:libHAPIL.dll");
    }
}
