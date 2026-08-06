// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Options;
using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public class OptionsOrchestratorTests : IDisposable
{
    private readonly string _projectRoot;

    public OptionsOrchestratorTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(OptionsOrchestratorTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    private string WriteTargetWithOption()
    {
        var targetFile = Path.Combine(_projectRoot, "Sample.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Options;
            using Omen.Core.Rules;

            public class SampleTarget : TargetRules
            {
                public SampleTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable feature X", false);
                }
            }
            """);
        return targetFile;
    }

    [Fact]
    public async Task DiscoverAsync_RuleCompilationFails_ReturnsNull()
    {
        var targetFile = Path.Combine(_projectRoot, "Broken.target.cs");
        File.WriteAllText(targetFile, "this is not valid C#");
        var orchestrator = new OptionsOrchestrator();
        var events = new List<OrchestratorEvent>();

        var result = await orchestrator.DiscoverAsync(
            new OptionsOrchestratorRequest { TargetFile = targetFile },
            new Progress<OrchestratorEvent>(events.Add));

        result.Should().BeNull();
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Error);
    }

    [Fact]
    public async Task DiscoverAsync_TargetDeclaresOption_ReturnsItWithDefaultValue()
    {
        var targetFile = WriteTargetWithOption();
        var orchestrator = new OptionsOrchestrator();

        var result = await orchestrator.DiscoverAsync(new OptionsOrchestratorRequest { TargetFile = targetFile }, events: null);

        result.Should().NotBeNull();
        result!.Should().ContainSingle(o => o.Name == "ENABLE_FEATURE_X" && o.EffectiveValue == "false");
    }

    [Fact]
    public async Task DiscoverAsync_AfterSaveOptions_ReturnsOverriddenValue()
    {
        var targetFile = WriteTargetWithOption();
        var orchestrator = new OptionsOrchestrator();

        orchestrator.SaveOptions(targetFile, new Dictionary<string, string> { ["ENABLE_FEATURE_X"] = "true" });
        var result = await orchestrator.DiscoverAsync(new OptionsOrchestratorRequest { TargetFile = targetFile }, events: null);

        result.Should().NotBeNull();
        result!.Single(o => o.Name == "ENABLE_FEATURE_X").EffectiveValue.Should().Be("true");
    }

    [Fact]
    public void SaveOptions_WritesToIntermediateOmenCacheJson()
    {
        var targetFile = WriteTargetWithOption();
        var orchestrator = new OptionsOrchestrator();

        orchestrator.SaveOptions(targetFile, new Dictionary<string, string> { ["X"] = "1" });

        File.Exists(Path.Combine(_projectRoot, "Intermediate", "omen-cache.json")).Should().BeTrue();
    }
}
