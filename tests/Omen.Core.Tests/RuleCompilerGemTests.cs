// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class RuleCompilerGemTests : IDisposable
{
    private readonly string _projectRoot;

    public RuleCompilerGemTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleCompilerGemTests), Guid.NewGuid().ToString("N"));
        var gemDir = Path.Combine(_projectRoot, "Gems", "Camera");
        Directory.CreateDirectory(gemDir);
        File.WriteAllText(Path.Combine(gemDir, "gem.json"), """
        { "gem_name": "Camera", "version": "0.1.0", "dependencies": [] }
        """);
        File.WriteAllText(Path.Combine(gemDir, "Camera.gem.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraGem : GemRules
        {
            public CameraGem(BuildContext context) : base(context)
            {
                LoadManifest("Gems/Camera");

                DefineFlavor(GemFlavorKind.Static);

                var runtime = DefineFlavor(GemFlavorKind.Runtime);
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
    public async Task CompileRulesAsync_DiscoversGemFiles()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var gems = compiledRules.CreateGemRules(CreateContext());

        gems.Should().ContainSingle(g => g.Name == "Camera");
    }

    [Fact]
    public async Task CreateModuleRules_IncludesOneModulePerGemFlavor()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext());

        modules.Should().Contain(m => m.Name == "Camera.Static");
        modules.Should().Contain(m => m.Name == "Camera.Runtime" && m.BinaryType == TargetType.SharedLibrary);
        modules.First(m => m.Name == "Camera.Runtime").PrivateDependencies.Should().Contain("Camera.Static");
    }
}
