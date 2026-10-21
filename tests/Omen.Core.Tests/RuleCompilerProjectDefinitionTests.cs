// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

/// <summary>
/// Covers the central project definition (OmenProjectRules / `*.omenproject.cs`) RuleCompiler
/// honours when present, and the whole-tree glob fallback it retains when absent - the two
/// requirements that keep every pre-existing project compiling unchanged.
/// </summary>
public class RuleCompilerProjectDefinitionTests : IDisposable
{
    private readonly string _projectRoot;

    public RuleCompilerProjectDefinitionTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleCompilerProjectDefinitionTests), Guid.NewGuid().ToString("N"));

        // A module file inside the declared "Code" root - must always be discovered.
        var codeDir = Path.Combine(_projectRoot, "Code", "Core");
        Directory.CreateDirectory(codeDir);
        File.WriteAllText(Path.Combine(codeDir, "Core.module.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class Core : ModuleRules
        {
            public Core(BuildContext context) : base(context) { }
        }
        """);

        // A second module file OUTSIDE any declared root - only found by the glob fallback.
        var strayDir = Path.Combine(_projectRoot, "Stray");
        Directory.CreateDirectory(strayDir);
        File.WriteAllText(Path.Combine(strayDir, "Stray.module.cs"), """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class Stray : ModuleRules
        {
            public Stray(BuildContext context) : base(context) { }
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
    public async Task CompileRulesAsync_NoProjectFile_GlobsWholeTree()
    {
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        compiledRules.Project.Should().BeNull();
        var modules = compiledRules.CreateModuleRules(CreateContext());
        modules.Should().Contain(m => m.Name == "Core");
        modules.Should().Contain(m => m.Name == "Stray");
    }

    [Fact]
    public async Task CompileRulesAsync_ProjectFileDeclaresSourceRoots_OnlyScansDeclaredRoots()
    {
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.omenproject.cs"), """
        using Omen.Core.Rules;

        public class SampleProject : OmenProjectRules
        {
            public SampleProject()
            {
                Version = "1.2.3";
                SourceRoots.Add("Code");
            }
        }
        """);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        compiledRules.Project.Should().NotBeNull();
        compiledRules.Project!.Name.Should().Be("Sample");
        compiledRules.Project!.Version.Should().Be("1.2.3");

        var modules = compiledRules.CreateModuleRules(CreateContext());
        modules.Should().Contain(m => m.Name == "Core");
        // The stray module outside the declared SourceRoots must NOT be pulled in - this is
        // the whole point of the central project definition over a blind recursive glob.
        modules.Should().NotContain(m => m.Name == "Stray");
    }

    [Fact]
    public async Task CompileRulesAsync_ProjectFileDeclaresExternalSubdirectory_ScansIt()
    {
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.omenproject.cs"), """
        using Omen.Core.Rules;

        public class SampleProject : OmenProjectRules
        {
            public SampleProject()
            {
                SourceRoots.Add("Code");
                ExternalSubdirectories.Add("Stray");
            }
        }
        """);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var modules = compiledRules.CreateModuleRules(CreateContext());
        modules.Should().Contain(m => m.Name == "Core");
        modules.Should().Contain(m => m.Name == "Stray");
    }

    [Fact]
    public async Task DiscoverRuleFilesAsync_MatchesCompileRulesAsync_ForProjectWithRootDefinition()
    {
        // The invariant the ValidateFileAsync/BuildCompilationAsync merge exists to protect: the
        // GUI editor (which discovers rule files via DiscoverRuleFilesAsync/DiscoverRuleFilesCached)
        // and a real build (CompileRulesAsync) must see exactly the same rule-file set for a
        // project with a root *.omenproject.cs - never the editor's old whole-tree glob against
        // the build's SourceRoots-scoped one, which would show the editor errors for files the
        // build never compiles (or hide ones it does).
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.omenproject.cs"), """
        using Omen.Core.Rules;

        public class SampleProject : OmenProjectRules
        {
            public SampleProject()
            {
                SourceRoots.Add("Code");
            }
        }
        """);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));

        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);
        var discovery = await compiler.DiscoverRuleFilesAsync(_projectRoot);

        discovery.ModuleFiles.Should().BeEquivalentTo(compiledRules.ModuleFiles);
        discovery.TargetFiles.Should().BeEquivalentTo(compiledRules.TargetFiles);
        discovery.PluginFiles.Should().BeEquivalentTo(compiledRules.PluginFiles);

        // Concretely: the stray module outside the declared SourceRoots is invisible to both,
        // not just the build - the exact divergence this test guards against.
        discovery.ModuleFiles.Should().Contain(f => f.EndsWith("Core.module.cs"));
        discovery.ModuleFiles.Should().NotContain(f => f.EndsWith("Stray.module.cs"));

        // And the editor's actual validation/IntelliSense entry point - BuildCompilationAsync,
        // which goes through the cached discovery path (DiscoverRuleFilesCached), not the plain
        // async wrapper above - must see the same set: the compiled Compilation's own syntax
        // trees are exactly the declared-SourceRoots file set, never the stray one outside it.
        var coreFile = Path.Combine(_projectRoot, "Code", "Core", "Core.module.cs");
        var (compilation, _) = await compiler.BuildCompilationAsync(_projectRoot, coreFile, File.ReadAllText(coreFile));

        var compiledTreePaths = compilation.SyntaxTrees.Select(t => t.FilePath).ToList();
        compiledTreePaths.Should().BeEquivalentTo(compiledRules.ModuleFiles);
        compiledTreePaths.Should().NotContain(f => f.EndsWith("Stray.module.cs"));
    }

    [Fact]
    public async Task BuildCompilationAsync_ProjectDefinitionEditedMidSession_EventuallyReflectsNewSourceRoots()
    {
        // Guards against the caching added for editor performance (DiscoverRuleFilesCached's
        // 5-second discovery cache, and BuildCompilationAsync's _baseCompilationCache keyed off
        // that discovery) silently outliving a change to the project definition itself - which
        // would break the shared-discovery invariant the test above guards, just via a stale
        // cache instead of a wrong code path. Editing the *.omenproject.cs mid-session (the same
        // RuleCompiler instance, as a long-lived GUI process would have) must still be reflected
        // once the deliberate 5-second staleness window elapses, exactly as an added/removed rule
        // file already is.
        var projectFile = Path.Combine(_projectRoot, "Sample.omenproject.cs");
        File.WriteAllText(projectFile, """
        using Omen.Core.Rules;

        public class SampleProject : OmenProjectRules
        {
            public SampleProject()
            {
                SourceRoots.Add("Code");
            }
        }
        """);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var coreFile = Path.Combine(_projectRoot, "Code", "Core", "Core.module.cs");
        var coreText = File.ReadAllText(coreFile);

        var (before, _) = await compiler.BuildCompilationAsync(_projectRoot, coreFile, coreText);
        before.SyntaxTrees.Select(t => t.FilePath).Should().NotContain(f => f.EndsWith("Stray.module.cs"));

        // Widen SourceRoots to include the previously-excluded Stray module, on the same
        // RuleCompiler instance whose caches already saw the old, narrower discovery.
        File.WriteAllText(projectFile, """
        using Omen.Core.Rules;

        public class SampleProject : OmenProjectRules
        {
            public SampleProject()
            {
                SourceRoots.Add("Code");
                SourceRoots.Add("Stray");
            }
        }
        """);

        // Past DiscoverRuleFilesCached's 5-second lifetime - long enough that the cached
        // discovery (and the base compilation built from it) must no longer be reused as-is.
        await Task.Delay(TimeSpan.FromSeconds(5.5));

        var (after, _) = await compiler.BuildCompilationAsync(_projectRoot, coreFile, coreText);
        after.SyntaxTrees.Select(t => t.FilePath).Should().Contain(f => f.EndsWith("Stray.module.cs"));
    }

    [Fact]
    public async Task CompileRulesAsync_TwoProjectFiles_Throws()
    {
        File.WriteAllText(Path.Combine(_projectRoot, "One.omenproject.cs"), """
        using Omen.Core.Rules;
        public class OneProject : OmenProjectRules { public OneProject() { SourceRoots.Add("Code"); } }
        """);
        File.WriteAllText(Path.Combine(_projectRoot, "Two.omenproject.cs"), """
        using Omen.Core.Rules;
        public class TwoProject : OmenProjectRules { public TwoProject() { SourceRoots.Add("Code"); } }
        """);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var act = async () => await compiler.CompileRulesAsync(_projectRoot);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*at most one*");
    }
}

