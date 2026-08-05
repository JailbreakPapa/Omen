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
}
