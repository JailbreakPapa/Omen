// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public class ProjectGenerationOrchestratorTests : IDisposable
{
    private readonly string _projectRoot;

    public ProjectGenerationOrchestratorTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ProjectGenerationOrchestratorTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    [Theory]
    [InlineData(IdeKind.VS2019)]
    [InlineData(IdeKind.VS2022)]
    [InlineData(IdeKind.VS2026)]
    public async Task GenerateAsync_VisualStudio_DoesNotEvaluateForeignPlatformRules(IdeKind ide)
    {
        File.WriteAllText(Path.Combine(_projectRoot, "Mixed.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;
            public class MixedTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows, TargetPlatform.Linux)
                    .For(TargetArchitecture.X64).For(BuildConfiguration.Development);
                public MixedTarget(BuildContext context) : base(context) { }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "Mixed.module.cs"), """
            using System;
            using Omen.Core.Configuration;
            using Omen.Core.Rules;
            public class MixedModule : ModuleRules
            {
                public MixedModule(BuildContext context) : base(context)
                {
                    if (context.Platform != TargetPlatform.Windows)
                        throw new InvalidOperationException("Foreign SDK must not be evaluated for Visual Studio");
                }
            }
            """);
        var success = await new ProjectGenerationOrchestrator().GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = ide }, null);
        success.Should().BeTrue();
        File.Exists(Path.Combine(_projectRoot, "Mixed.sln")).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_NoTargetFile_ReportsErrorAndReturnsFalse()
    {
        var orchestrator = new ProjectGenerationOrchestrator();
        var events = new List<OrchestratorEvent>();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.CMake },
            new SynchronousProgress<OrchestratorEvent>(events.Add));

        success.Should().BeFalse();
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Error && e.Message.Contains("No target file found"));
    }

    [Fact]
    public async Task GenerateAsync_CMake_WritesCMakeListsTxt()
    {
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class SampleTarget : TargetRules
            {
                public SampleTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                }
            }
            """);
        var orchestrator = new ProjectGenerationOrchestrator();
        var events = new List<OrchestratorEvent>();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.CMake },
            new SynchronousProgress<OrchestratorEvent>(events.Add));

        success.Should().BeTrue();
        File.Exists(Path.Combine(_projectRoot, "CMakeLists.txt")).Should().BeTrue();
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Success);
    }

    [Fact]
    public async Task GenerateAsync_CMake_PrivateObjectFlavor_EmitsO3dePrivateTargetProperty()
    {
        // Round-trips PluginFlavor.IsPrivateTarget all the way to generated CMake text - the same
        // set_target_properties(... O3DE_PRIVATE_TARGET TRUE) call the real O3DE plugin source
        // sets, that Omen's rule model previously had nowhere to represent.
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class SampleTarget : TargetRules
            {
                public SampleTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                }
            }
            """);
        var pluginDir = Path.Combine(_projectRoot, "Plugins", "Archive");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """{ "plugin_name": "Archive", "version": "0.1.0" }""");
        File.WriteAllText(Path.Combine(pluginDir, "Archive.cpp"), "// tu\n");
        File.WriteAllText(Path.Combine(pluginDir, "Archive.plugin.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class ArchivePlugin : PluginRules
            {
                public ArchivePlugin(BuildContext context) : base(context)
                {
                    LoadManifest("Plugins/Archive");
                    var privateObject = DefineFlavor(PluginFlavorKind.PrivateObject);
                    privateObject.SourceFiles.Add("Archive.cpp");
                }
            }
            """);
        var orchestrator = new ProjectGenerationOrchestrator();
        var events = new List<OrchestratorEvent>();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.CMake },
            new SynchronousProgress<OrchestratorEvent>(events.Add));

        success.Should().BeTrue();
        var cmakeLists = await File.ReadAllTextAsync(Path.Combine(_projectRoot, "CMakeLists.txt"));
        cmakeLists.Should().Contain("set_target_properties(Archive.Private.Object PROPERTIES O3DE_PRIVATE_TARGET TRUE)");
    }

    [Fact]
    public async Task GenerateAsync_VSCode_WritesDotVscodeFiles()
    {
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class SampleTarget : TargetRules
            {
                public SampleTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                }
            }
            """);
        var orchestrator = new ProjectGenerationOrchestrator();
        var events = new List<OrchestratorEvent>();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.VSCode },
            new SynchronousProgress<OrchestratorEvent>(events.Add));

        success.Should().BeTrue();
        File.Exists(Path.Combine(_projectRoot, ".vscode", "tasks.json")).Should().BeTrue();
        File.Exists(Path.Combine(_projectRoot, ".vscode", "launch.json")).Should().BeTrue();
        File.Exists(Path.Combine(_projectRoot, ".vscode", "c_cpp_properties.json")).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_TargetWithoutSupportedTargets_UsesTodaysSingleConfigurationList()
    {
        var sourceDir = Path.Combine(_projectRoot, "Source", "App", "Private");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "Main.cpp"), "int main() { return 0; }\n");
        File.WriteAllText(Path.Combine(_projectRoot, "Source", "App", "App.module.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppModule : ModuleRules
            {
                public AppModule(BuildContext context) : base(context)
                {
                    Type = ModuleType.Runtime;
                    SourceDirectory = "Source/App";
                }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "App.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppTarget : TargetRules
            {
                public AppTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "AppModule";
                    ExtraModules.Add("AppModule");
                }
            }
            """);
        var orchestrator = new ProjectGenerationOrchestrator();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.VS2022 },
            events: null);

        success.Should().BeTrue();
        var vcxproj = File.ReadAllText(Directory.GetFiles(_projectRoot, "AppModule.vcxproj", SearchOption.AllDirectories).Single());
        vcxproj.Should().Contain("Debug|x64");
        vcxproj.Should().Contain("Shipping|ARM64");
    }

    [Fact]
    public async Task GenerateAsync_TargetWithSupportedTargets_EmitsOneProjectConfigurationPerResolvedCombination()
    {
        var sourceDir = Path.Combine(_projectRoot, "Source", "App", "Private");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "Main.cpp"), "int main() { return 0; }\n");
        File.WriteAllText(Path.Combine(_projectRoot, "Source", "App", "App.module.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppModule : ModuleRules
            {
                public AppModule(BuildContext context) : base(context)
                {
                    Type = ModuleType.Runtime;
                    SourceDirectory = "Source/App";
                }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "App.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows)
                    .For(TargetArchitecture.X64, TargetArchitecture.ARM64)
                    .For(BuildConfiguration.Debug);

                public AppTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "AppModule";
                    ExtraModules.Add("AppModule");
                }
            }
            """);
        var orchestrator = new ProjectGenerationOrchestrator();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.VS2022 },
            events: null);

        success.Should().BeTrue();
        var vcxproj = File.ReadAllText(Directory.GetFiles(_projectRoot, "AppModule.vcxproj", SearchOption.AllDirectories).Single());
        vcxproj.Should().Contain("Debug|x64");
        vcxproj.Should().Contain("Debug|ARM64");
        vcxproj.Should().NotContain("Shipping"); // matrix declared only Debug - today's hardcoded list must not leak in.
    }

    private void WriteSampleTargetAndWidgetPlugin()
    {
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class SampleTarget : TargetRules
            {
                public SampleTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                }
            }
            """);
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
                    DefineFlavor(PluginFlavorKind.Runtime);
                    CreateAlias("Clients", PluginFlavorKind.Runtime);
                    CreateAlias("Unified", PluginFlavorKind.Runtime);
                }
            }
            """);
    }

    [Fact]
    public async Task GenerateAsync_NoProjectDefinition_IncludesEveryPluginFlavor_Unchanged()
    {
        // Backward compatibility: no *.omenproject.cs at all means no EnabledPlugins anywhere,
        // so generation includes every compiled plugin exactly as it did before PluginEnablement
        // was wired in here.
        WriteSampleTargetAndWidgetPlugin();
        var orchestrator = new ProjectGenerationOrchestrator();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.CMake },
            events: null);

        success.Should().BeTrue();
        var cmake = File.ReadAllText(Path.Combine(_projectRoot, "CMakeLists.txt"));
        cmake.Should().Contain("# Module: Widget");
    }

    [Fact]
    public async Task GenerateAsync_ProjectDeclaresEnabledPlugins_FiltersOutTheDisabledPlugin()
    {
        WriteSampleTargetAndWidgetPlugin();
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.omenproject.cs"), """
            using Omen.Core.Rules;

            public class SampleProject : OmenProjectRules
            {
                public SampleProject()
                {
                    SourceRoots.Add(".");
                    EnabledPlugins = [];
                }
            }
            """);
        var orchestrator = new ProjectGenerationOrchestrator();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.CMake },
            events: null);

        success.Should().BeTrue();
        var cmake = File.ReadAllText(Path.Combine(_projectRoot, "CMakeLists.txt"));
        cmake.Should().NotContain("Widget");
    }

    [Fact]
    public async Task GenerateAsync_CompileCommands_CarryTheProjectsEngineWideToolchainOptions()
    {
        // The generated compile_commands.json has to describe the compile the real build
        // performs. It did not: project generation never read OmenProjectRules.ToolchainOptions,
        // so every replayed command line was missing O3DE's engine-wide /Zc:preprocessor and
        // died in NightFoxCore/variadic.h with C1112.
        var sourceDir = Path.Combine(_projectRoot, "Source", "App", "Private");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "Main.cpp"), "int main() { return 0; }" + Environment.NewLine);
        File.WriteAllText(Path.Combine(_projectRoot, "Source", "App", "App.module.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppModule : ModuleRules
            {
                public AppModule(BuildContext context) : base(context)
                {
                    Type = ModuleType.Runtime;
                    SourceDirectory = "Source/App";
                }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "App.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows)
                    .For(TargetArchitecture.X64)
                    .For(BuildConfiguration.Development);

                public AppTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "AppModule";
                    ExtraModules.Add("AppModule");
                }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.omenproject.cs"), """
            using Omen.Core.Rules;

            public class SampleProject : OmenProjectRules
            {
                public SampleProject()
                {
                    SourceRoots.Add(".");
                    var msvc = ToolchainOptionsFor(CompilerFamily.Msvc);
                    msvc.Common.CompilerOptions.Add("/Zc:preprocessor");
                    msvc.Common.Definitions.Add("_HAS_EXCEPTIONS=0");
                }
            }
            """);
        var orchestrator = new ProjectGenerationOrchestrator();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.VS2022 },
            events: null);

        success.Should().BeTrue();
        var compileCommands = File.ReadAllText(Path.Combine(_projectRoot, "compile_commands.json"));
        compileCommands.Should().Contain("/Zc:preprocessor");
        compileCommands.Should().Contain("/D_HAS_EXCEPTIONS=0");
    }

    [Fact]
    public async Task GenerateAsync_SeveralTargets_EachLaunchModuleBecomesItsOwnTargetsProject()
    {
        // The engine tree's shape: a dozen *.target.cs files, one solution. Only the first
        // target reflection listed used to reach the generator, and even then a launch module
        // was only recognized through ExtraModules - so ModelDoc.target.cs's launch module came
        // out a plain static library and no .exe project existed anywhere in the solution.
        void WriteModule(string name)
        {
            var privateDir = Path.Combine(_projectRoot, "Source", name, "Private");
            Directory.CreateDirectory(privateDir);
            File.WriteAllText(Path.Combine(privateDir, $"{name}.cpp"), "int f() { return 0; }\n");
            File.WriteAllText(Path.Combine(_projectRoot, "Source", name, $"{name}.module.cs"), $$"""
                using Omen.Core.Configuration;
                using Omen.Core.Rules;

                public class {{name}}Module : ModuleRules
                {
                    public {{name}}Module(BuildContext context) : base(context, explicitName: "{{name}}")
                    {
                        SourceDirectory = "Source/{{name}}";
                    }
                }
                """);
        }
        WriteModule("Tool");
        WriteModule("Lib");
        // Launch modules named ONLY through LaunchModuleName, exactly like ModelDoc.target.cs.
        File.WriteAllText(Path.Combine(_projectRoot, "Tool.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class ToolTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows)
                    .For(TargetArchitecture.X64)
                    .For(BuildConfiguration.Debug);

                public ToolTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "Tool";
                }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "Lib.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class LibTarget : TargetRules
            {
                public LibTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.SharedLibrary;
                    LaunchModuleName = "Lib";
                }
            }
            """);

        var success = await new ProjectGenerationOrchestrator().GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.VS2026 },
            events: null);

        success.Should().BeTrue();
        var slnx = System.Xml.Linq.XDocument.Load(Directory.GetFiles(_projectRoot, "*.slnx").Single());
        slnx.Descendants("Project").Should().HaveCount(2);
        var slnxTool = slnx.Descendants("Project").Single(p => ((string)p.Attribute("Path")!).EndsWith("/Tool.vcxproj"));
        slnxTool.Elements("Build").Single(b => (string?)b.Attribute("Solution") == "Shipping|ARM64")
            .Attribute("Project")!.Value.Should().Be("false");
        string Project(string name) =>
            File.ReadAllText(Path.Combine(_projectRoot, "Intermediate", "ProjectFiles", name, $"{name}.vcxproj"));

        var tool = Project("Tool");
        tool.Should().Contain("<ConfigurationType>Makefile</ConfigurationType>");
        tool.Should().Contain("omen build Tool --configuration Debug");
        tool.Should().Contain(@"\Tool.exe</NMakeOutput>");
        // Its own target declares Debug|x64 only - Lib's default Shipping/ARM64 rows must not leak
        // in, or VS would run `omen build Tool -c Shipping` against a row the target refuses.
        tool.Should().NotContain("Shipping");
        tool.Should().NotContain("ARM64");

        var lib = Project("Lib");
        lib.Should().Contain("<ConfigurationType>Makefile</ConfigurationType>");
        lib.Should().Contain("omen build Lib --configuration Shipping --arch ARM64");

        // Every project maps every solution configuration, or Visual Studio flags it
        // "configuration marked incomplete". Tool only declares Debug|x64; the solution has
        // Lib's six. Undeclared ones map to Tool's real row with no Build.0.
        var solution = File.ReadAllText(Directory.GetFiles(_projectRoot, "*.sln").Single());
        var toolGuid = System.Text.RegularExpressions.Regex.Match(solution, @"= ""Tool"", ""[^""]*"", ""(\{[0-9A-F-]+\})""").Groups[1].Value;
        var solutionConfigs = System.Text.RegularExpressions.Regex.Matches(solution, @"^\t\t(\w+\|\w+) = \1", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        solutionConfigs.Should().HaveCount(6);
        foreach (var solutionConfig in solutionConfigs)
            solution.Should().Contain($"{toolGuid}.{solutionConfig}.ActiveCfg = Debug|x64");
        System.Text.RegularExpressions.Regex.Matches(solution, $@"{System.Text.RegularExpressions.Regex.Escape(toolGuid)}\.[^.]+\.Build\.0")
            .Should().HaveCount(1, "only the declared Debug|x64 row builds");
    }
}

