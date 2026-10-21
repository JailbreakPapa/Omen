// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;

namespace Omen.Core.Tests;

public sealed class ProjectPresetsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OmenPresetsTests", Guid.NewGuid().ToString("N"));
    public ProjectPresetsTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private void Write(string name, object value) => File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(value));

    [Fact]
    public void IncludesInheritanceAndUserPresets_PreserveOriginAndOverridePrecedence()
    {
        Directory.CreateDirectory(Path.Combine(root, "shared"));
        Write("shared/base.json", new
        {
            version = 1,
            presets = new[] { new { name = "base", hidden = true,
                commands = new Dictionary<string, string[]> { ["build"] = ["App", "-c", "Debug"], ["test"] = ["--junit", "${fileDir}/results.xml"] },
                environment = new { OMEN_PRESET_TEST_BASE = "base", OMEN_PRESET_TEST_CHAIN = "$env{OMEN_PRESET_TEST_BASE}/child" } } }
        });
        Write("OmenPresets.json", new { version = 1, include = new[] { "shared/base.json" }, presets = new[]
        {
            new { name = "dev", inherits = new[] { "base" }, commands = new Dictionary<string,string[]> { ["build"] = ["App", "-c", "Development"] }, environment = new { OMEN_PRESET_TEST_BASE = "dev" } }
        } });
        Write("OmenUserPresets.json", new { version = 1, presets = new[]
        {
            new { name = "local", inherits = new[] { "dev" }, commands = new Dictionary<string,string[]> { ["package"] = ["--output", "${sourceDir}/${presetName}.zip"] } }
        } });
        var file = new ProjectPresets(root);
        var preset = file.Resolve("local");
        file.List().Select(p => p.Name).Should().Equal("dev", "local");
        preset.Commands["build"].Should().Equal("App", "-c", "Development");
        preset.Commands["test"].Last().Should().Be(Path.Combine(root, "shared") + "/results.xml");
        preset.Commands["package"].Last().Should().Be(root + "/local.zip");
        preset.Environment["OMEN_PRESET_TEST_CHAIN"].Should().Be("dev/child");
        Environment.GetEnvironmentVariable("OMEN_PRESET_TEST_BASE").Should().BeNull("resolving a preset must not change process environment");
    }

    [Fact]
    public void InheritanceAndEnvironmentCycles_AreRejected()
    {
        Write("OmenPresets.json", new { version = 1, presets = new[]
        {
            new { name = "a", inherits = new[] { "b" } }, new { name = "b", inherits = new[] { "a" } }
        } });
        FluentActions.Invoking(() => new ProjectPresets(root).Resolve("a")).Should().Throw<InvalidOperationException>().WithMessage("*cycle*");
        Write("OmenPresets.json", new { version = 1, presets = new[]
        {
            new { name = "env", environment = new Dictionary<string,string> { ["A"] = "$env{B}", ["B"] = "$env{A}" } }
        } });
        FluentActions.Invoking(() => new ProjectPresets(root).Resolve("env")).Should().Throw<InvalidOperationException>().WithMessage("*cycle*");
    }

    [Fact]
    public void IncludeCyclesAndDuplicateNames_AreRejected()
    {
        Write("OmenPresets.json", new { version = 1, include = new[] { "other.json" } });
        Write("other.json", new { version = 1, include = new[] { "OmenPresets.json" } });
        FluentActions.Invoking(() => new ProjectPresets(root)).Should().Throw<InvalidOperationException>().WithMessage("*cycle*");
        Write("OmenPresets.json", new { version = 1, presets = new[] { new { name = "same" }, new { name = "same" } } });
        FluentActions.Invoking(() => new ProjectPresets(root)).Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*");
    }

    [Theory]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":1,\"typo\":true}")]
    [InlineData("{\"version\":1,\"presets\":[{\"name\":\"bad\",\"commands\":{\"build\":[\"--preset\",\"self\"]}}]}")]
    public void InvalidSchemaOrRecursiveArguments_AreRejected(string json)
    {
        File.WriteAllText(Path.Combine(root, "OmenPresets.json"), json);
        FluentActions.Invoking(() => new ProjectPresets(root)).Should().Throw<Exception>();
    }

    [Fact]
    public void MultipleInheritance_EarlierParentWinsAndNullEnvironmentUnsets()
    {
        Write("OmenPresets.json", new { version = 1, presets = new object[]
        {
            new { name = "first", environment = new { A = "one", B = "keep" } },
            new { name = "second", environment = new { A = "two", C = "also" } },
            new { name = "child", inherits = new[] { "first", "second" }, environment = new Dictionary<string,string?> { ["B"] = null } }
        } });
        var preset = new ProjectPresets(root).Resolve("child");
        preset.Environment["A"].Should().Be("one");
        preset.Environment["B"].Should().BeNull();
        preset.Environment["C"].Should().Be("also");
    }

    [Fact]
    public void ExplicitCliValuesOverrideAliasesAndIndividualAssignments()
    {
        var merged = ProjectPresets.MergeArguments(["OldTarget", "--configuration", "Debug", "-j", "1", "--dim", "Api=Vulkan", "--set", "A=1", "--set", "B=2", "--no-server"],
            ["NewTarget", "-c", "Development", "--jobs=4", "--dim", "Api=D3D12", "-D", "A=3"]);
        merged.Should().Equal("--set", "B=2", "--no-server", "NewTarget", "-c", "Development", "--jobs=4", "--dim", "Api=D3D12", "-D", "A=3");
    }

    [Fact]
    public void PlatformFilteringAndWorkflowOrder_AreRetained()
    {
        Write("OmenPresets.json", new { version = 1, presets = new[]
        {
            new { name = "host", hostPlatforms = new[] { ProjectPresets.HostPlatform } },
            new { name = "other", hostPlatforms = new[] { "NeverThisHost" } }
        }, workflows = new[] { new { name = "ci", steps = new[] { new[] { "build", "--preset", "host" }, new[] { "test", "--preset", "host" } } } } });
        var presets = new ProjectPresets(root);
        presets.List().Select(p => p.Name).Should().Equal("host");
        presets.Workflow("ci").Steps.Select(s => s[0]).Should().Equal("build", "test");
    }
}
