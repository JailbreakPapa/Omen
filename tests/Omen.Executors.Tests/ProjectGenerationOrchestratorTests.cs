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

    [Fact]
    public async Task GenerateAsync_NoTargetFile_ReportsErrorAndReturnsFalse()
    {
        var orchestrator = new ProjectGenerationOrchestrator();
        var events = new List<OrchestratorEvent>();

        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.CMake },
            new Progress<OrchestratorEvent>(events.Add));

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
            new Progress<OrchestratorEvent>(events.Add));

        success.Should().BeTrue();
        File.Exists(Path.Combine(_projectRoot, "CMakeLists.txt")).Should().BeTrue();
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Success);
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
            new Progress<OrchestratorEvent>(events.Add));

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
}
