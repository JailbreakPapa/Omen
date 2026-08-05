// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public class CleanOrchestratorTests : IDisposable
{
    private readonly string _projectRoot;

    public CleanOrchestratorTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(CleanOrchestratorTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    [Fact]
    public async Task CleanAsync_NothingToClean_ReportsWarningAndZeroResult()
    {
        var orchestrator = new CleanOrchestrator();
        var events = new List<OrchestratorEvent>();

        var result = await orchestrator.CleanAsync(
            new CleanOrchestratorRequest { ProjectRoot = _projectRoot },
            new Progress<OrchestratorEvent>(events.Add));

        result.DirectoriesCleaned.Should().Be(0);
        result.DirectoriesFailed.Should().Be(0);
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Warning && e.Message.Contains("Nothing to clean"));
    }

    [Fact]
    public async Task CleanAsync_NoPlatformOrConfiguration_DeletesBothIntermediateAndBinaries()
    {
        var intermediate = Path.Combine(_projectRoot, "Intermediate");
        var binaries = Path.Combine(_projectRoot, "Binaries");
        Directory.CreateDirectory(intermediate);
        Directory.CreateDirectory(binaries);
        File.WriteAllText(Path.Combine(intermediate, "marker.txt"), "x");

        var orchestrator = new CleanOrchestrator();
        var events = new List<OrchestratorEvent>();

        var result = await orchestrator.CleanAsync(
            new CleanOrchestratorRequest { ProjectRoot = _projectRoot },
            new Progress<OrchestratorEvent>(events.Add));

        result.DirectoriesCleaned.Should().Be(2);
        Directory.Exists(intermediate).Should().BeFalse();
        Directory.Exists(binaries).Should().BeFalse();
    }

    [Fact]
    public async Task CleanAsync_WithPlatformAndConfiguration_OnlyDeletesMatchingSubdirectories()
    {
        var intermediate = Path.Combine(_projectRoot, "Intermediate");
        Directory.CreateDirectory(Path.Combine(intermediate, "Windows_Development"));
        Directory.CreateDirectory(Path.Combine(intermediate, "Linux_Development"));

        var orchestrator = new CleanOrchestrator();
        var events = new List<OrchestratorEvent>();

        var result = await orchestrator.CleanAsync(
            new CleanOrchestratorRequest { ProjectRoot = _projectRoot, Platform = "Windows", Configuration = "Development" },
            new Progress<OrchestratorEvent>(events.Add));

        result.DirectoriesCleaned.Should().Be(1);
        Directory.Exists(Path.Combine(intermediate, "Windows_Development")).Should().BeFalse();
        Directory.Exists(Path.Combine(intermediate, "Linux_Development")).Should().BeTrue();
    }
}
