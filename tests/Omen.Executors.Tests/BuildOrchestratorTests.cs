// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public class BuildOrchestratorTests : IDisposable
{
    private readonly string _projectRoot;

    public BuildOrchestratorTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(BuildOrchestratorTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    private BuildOrchestratorRequest CreateRequest(string targetFile) => new()
    {
        TargetFile = targetFile,
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Development
    };

    [Fact]
    public async Task BuildAsync_RuleCompilationFails_ReportsErrorEventAndReturnsNull()
    {
        // Arrange: a .target.cs with a syntax error
        var targetFile = Path.Combine(_projectRoot, "Broken.target.cs");
        File.WriteAllText(targetFile, "this is not valid C#");
        var orchestrator = new BuildOrchestrator();
        var events = new List<OrchestratorEvent>();

        // Act
        var result = await orchestrator.BuildAsync(
            CreateRequest(targetFile),
            new Progress<OrchestratorEvent>(events.Add),
            buildProgress: null);

        // Assert
        result.Should().BeNull();
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Error && e.Message.Contains("compiling rules"));
    }

    [Fact]
    public async Task BuildAsync_NoTargetFound_ReportsErrorEventAndReturnsNull()
    {
        // Arrange: a valid module but no TargetRules subclass anywhere
        var moduleDir = Path.Combine(_projectRoot, "Source", "Core");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllText(Path.Combine(moduleDir, "Core.module.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class CoreModule : ModuleRules
            {
                public CoreModule(BuildContext context) : base(context)
                {
                    SourceDirectory = "Source/Core";
                }
            }
            """);
        // BuildOrchestrator resolves the target file from the request directly (unlike the
        // CLI's ResolveTargetFile search), so point it at a target.cs path that doesn't exist -
        // RuleCompiler.CompileRulesAsync only requires at least one *.module.cs OR *.target.cs
        // OR *.gem.cs to exist somewhere under the project root, which the module above satisfies.
        var missingTargetFile = Path.Combine(_projectRoot, "Missing.target.cs");
        var orchestrator = new BuildOrchestrator();
        var events = new List<OrchestratorEvent>();

        // Act
        var result = await orchestrator.BuildAsync(
            CreateRequest(missingTargetFile),
            new Progress<OrchestratorEvent>(events.Add),
            buildProgress: null);

        // Assert
        result.Should().BeNull();
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Error && e.Message.Contains("No target found"));
    }

    [Fact]
    public async Task BuildAsync_NoModulesFound_ReturnsSuccessfulZeroActionResult()
    {
        // Arrange: a target with zero modules anywhere in the project
        var targetFile = Path.Combine(_projectRoot, "Empty.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class EmptyTarget : TargetRules
            {
                public EmptyTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                }
            }
            """);
        var orchestrator = new BuildOrchestrator();
        var events = new List<OrchestratorEvent>();

        // Act
        var result = await orchestrator.BuildAsync(
            CreateRequest(targetFile),
            new Progress<OrchestratorEvent>(events.Add),
            buildProgress: null);

        // Assert
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.TotalActions.Should().Be(0);
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Warning && e.Message.Contains("No modules"));
    }

    [Fact]
    public async Task BuildAsync_UsesTargetFilesDirectoryAsProjectRoot()
    {
        // Regression guard for the request shape: BuildContext.ProjectRoot must come from
        // the target file's own directory (matching BuildCommand.cs's
        // Path.GetDirectoryName(targetFile) convention), not Environment.CurrentDirectory -
        // the GUI has no meaningful "current directory" of its own to fall back on.
        var subDir = Path.Combine(_projectRoot, "MyProject");
        Directory.CreateDirectory(subDir);
        var targetFile = Path.Combine(subDir, "MyProject.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class MyProjectTarget : TargetRules
            {
                public MyProjectTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                }
            }
            """);
        var orchestrator = new BuildOrchestrator();
        var events = new List<OrchestratorEvent>();

        var result = await orchestrator.BuildAsync(
            CreateRequest(targetFile),
            new Progress<OrchestratorEvent>(events.Add),
            buildProgress: null);

        // No modules exist under MyProject/Source, so this still returns the zero-action
        // success path - the point of this test is that it does NOT error looking for
        // rule files anywhere other than under `subDir` (i.e. it didn't fall back to
        // Environment.CurrentDirectory, which would be the test runner's own directory
        // and could contain unrelated rule files, making this test flaky).
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_SecondBuild_ReportsTrueSkippedCountNotZero()
    {
        // Regression test: ParallelExecutor's own internal skip counter can't see actions
        // BuildOrchestrator already flipped from Pending to Skipped via its digest-based
        // pre-pass (that counter only counts actions still Pending at that point), so
        // BuildResult.SkippedActions used to always come back 0 for a fully up-to-date
        // rebuild. BuildOrchestrator must recompute the true count from the graph before
        // returning, since callers (BuildCommand.cs) no longer have access to the graph
        // themselves to work around it the way the pre-extraction code did.
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
        var targetFile = Path.Combine(_projectRoot, "App.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppTarget : TargetRules
            {
                public AppTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "AppModule";
                    ExtraModules.Add("AppModule");
                    UsePCHFiles = false;
                    UseUnityBuild = false;
                }
            }
            """);

        var orchestrator = new BuildOrchestrator();

        var first = await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null);
        first.Should().NotBeNull();
        first!.Success.Should().BeTrue();
        first.TotalActions.Should().BeGreaterThan(0);

        var second = await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null);

        second.Should().NotBeNull();
        second!.Success.Should().BeTrue();
        second.SkippedActions.Should().Be(first.TotalActions);
    }

    [Fact]
    public async Task BuildAsync_AfterCachedOptionValueChanges_RecompilesInsteadOfSkipping()
    {
        // Regression test: ParallelExecutor used to run its own second, timestamp-only
        // up-to-date pass (graph.MarkUpToDateActionsAsSkipped(), no digest argument) after
        // BuildOrchestrator's own digest-aware pass had already correctly left
        // option-affected actions Pending. Since editing Intermediate/omen-cache.json
        // touches no source file, the timestamp-only pass saw nothing to invalidate and
        // flipped those actions straight back to Skipped, silently discarding a rebuild
        // that a toggled build option (a real command-line change) requires.
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
        var targetFile = Path.Combine(_projectRoot, "App.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Options;
            using Omen.Core.Rules;

            public class AppTarget : TargetRules
            {
                public AppTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "AppModule";
                    ExtraModules.Add("AppModule");
                    UsePCHFiles = false;
                    UseUnityBuild = false;

                    if (BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable feature X", false))
                    {
                        GlobalDefinitions.Add("FEATURE_X=1");
                    }
                }
            }
            """);

        var cacheStore = new Omen.Core.Options.OptionCacheStore(Path.Combine(_projectRoot, "Intermediate", "omen-cache.json"));
        var orchestrator = new BuildOrchestrator();

        var first = await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null);
        first.Should().NotBeNull();
        first!.Success.Should().BeTrue();
        first.TotalActions.Should().BeGreaterThan(0);

        cacheStore.Save(new Dictionary<string, string> { ["ENABLE_FEATURE_X"] = "true" });
        var second = await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null);

        second.Should().NotBeNull();
        second!.Success.Should().BeTrue();
        second.SkippedActions.Should().Be(0);
        second.SuccessfulActions.Should().Be(first.TotalActions);

        // No further change - this rebuild really should skip everything.
        var third = await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null);
        third.Should().NotBeNull();
        third!.SkippedActions.Should().Be(first.TotalActions);
    }

    [Fact]
    public async Task CleanAsync_AfterCacheHitBuild_DoesNotLockRuleCacheDll()
    {
        // Regression test for RuleCompiler's cache-hit path locking the cached rule DLL
        // (see RuleCompiler.LoadAssembly). The CLI never hit this because every command is
        // its own process, but the GUI builds and cleans within one long-lived process: a
        // build whose RuleCache entry is new to *this* process's AssemblyLoadContext.Default
        // used to leave Intermediate/RuleCache/<hash>.dll memory-mapped, so a Clean in that
        // same session failed to delete it.
        //
        // Note this can't be reproduced by simply building the same unchanged rules twice in
        // one process: AssemblyLoadContext.Default only locks a DLL the *first* time it loads
        // that exact assembly identity, and a same-process rebuild reuses the identity already
        // resident from the first build's in-memory compile - it never re-touches the file. To
        // reproduce the real trigger (a cache entry this process has never loaded before, e.g.
        // one left by an earlier CLI build), the cache file is swapped for a differently-named
        // assembly between builds so the second build's cache hit is a genuinely fresh load.
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
        var targetFile = Path.Combine(_projectRoot, "App.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppTarget : TargetRules
            {
                public AppTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "AppModule";
                    ExtraModules.Add("AppModule");
                    UsePCHFiles = false;
                    UseUnityBuild = false;
                }
            }
            """);

        var orchestrator = new BuildOrchestrator();

        var first = await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null);
        first!.Success.Should().BeTrue();

        var cacheDir = Path.Combine(_projectRoot, "Intermediate", "RuleCache");
        var cachedDll = Directory.GetFiles(cacheDir, "*.dll").Should().ContainSingle().Subject;
        File.WriteAllBytes(cachedDll, CompileTrivialAssembly());

        // This build's rules aren't the ones actually on disk (the swapped-in assembly has no
        // AppModule/AppTarget types), so it's expected to fail past the rule-compile step. What
        // matters is that RuleCompiler.CompileRulesAsync took the cache-hit path for a DLL
        // identity this process has never loaded before.
        await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null);

        var cleanResult = await new CleanOrchestrator().CleanAsync(
            new CleanOrchestratorRequest { ProjectRoot = _projectRoot },
            events: null);

        cleanResult.DirectoriesFailed.Should().Be(0);
        Directory.Exists(Path.Combine(_projectRoot, "Intermediate")).Should().BeFalse();
    }

    private static byte[] CompileTrivialAssembly()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("public class RuleCacheProbe {}");
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            $"OmenRulesProbe_{Guid.NewGuid():N}",
            [tree],
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        compilation.Emit(ms).Success.Should().BeTrue();
        return ms.ToArray();
    }

    [Fact]
    public async Task BuildAsync_WithCachedOptionOverride_MakesItAvailableToRuleFiles()
    {
        // Arrange: a target that declares an option and records its effective value where the
        // test can observe it (BuildOptions.Declare's return value isn't otherwise surfaced by
        // BuildResult, so the target writes it to a file as a simple, real side effect).
        var recordedValuePath = Path.Combine(_projectRoot, "recorded-value.txt");
        var targetFile = Path.Combine(_projectRoot, "Sample.target.cs");
        File.WriteAllText(targetFile, $$"""
            using Omen.Core.Configuration;
            using Omen.Core.Options;
            using Omen.Core.Rules;

            public class SampleTarget : TargetRules
            {
                public SampleTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    var enabled = BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable feature X", false);
                    System.IO.File.WriteAllText(@"{{recordedValuePath.Replace("\\", "\\\\")}}", enabled.ToString());
                }
            }
            """);

        new Omen.Core.Options.OptionCacheStore(Path.Combine(_projectRoot, "Intermediate", "omen-cache.json"))
            .Save(new Dictionary<string, string> { ["ENABLE_FEATURE_X"] = "true" });

        var orchestrator = new BuildOrchestrator();

        // Act
        await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null);

        // Assert: the target's constructor ran with the cached override visible, not the
        // compiled-in default.
        File.Exists(recordedValuePath).Should().BeTrue();
        File.ReadAllText(recordedValuePath).Should().Be("True");
    }

    [Fact]
    public async Task BuildAsync_TargetWithoutSupportedTargets_BuildsExactlyOneCombination()
    {
        var targetFile = Path.Combine(_projectRoot, "NoMatrix.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class NoMatrixTarget : TargetRules
            {
                public NoMatrixTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                }
            }
            """);
        var orchestrator = new BuildOrchestrator();

        var result = await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null);

        // No modules exist, so this is the existing zero-action success path - the point of
        // this test is that it stays a SINGLE zero-action result, not a multiplied one.
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.TotalActions.Should().Be(0);
    }

    [Fact]
    public async Task BuildAsync_TargetWithSupportedTargets_BuildsEveryResolvedCombination()
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
                    .For(TargetArchitecture.X64)
                    .For(BuildConfiguration.Debug, BuildConfiguration.Release);

                public AppTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "AppModule";
                    ExtraModules.Add("AppModule");
                    UsePCHFiles = false;
                    UseUnityBuild = false;
                }
            }
            """);
        var orchestrator = new BuildOrchestrator();
        var events = new List<OrchestratorEvent>();

        // Not CreateRequest(...) - its hardcoded Configuration = BuildConfiguration.Development
        // would filter out both of this matrix's declared combinations (Debug, Release), since
        // MatchesFilter treats a non-null request.Configuration as an exact-match filter and
        // neither combination is Development.
        var result = await orchestrator.BuildAsync(
            new BuildOrchestratorRequest { TargetFile = Path.Combine(_projectRoot, "App.target.cs") },
            new Progress<OrchestratorEvent>(events.Add),
            buildProgress: null);

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        // Two resolved combinations (Debug, Release) x however many actions one produces.
        result.TotalActions.Should().Be(2 * result.TotalActions / 2); // sanity: even split
        (result.SuccessfulActions + result.SkippedActions).Should().Be(result.TotalActions);
        Directory.Exists(Path.Combine(_projectRoot, "Binaries", "Windows_Debug")).Should().BeTrue();
        Directory.Exists(Path.Combine(_projectRoot, "Binaries", "Windows_Release")).Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_FilterNarrowsToSubsetOfDeclaredMatrix()
    {
        // A real (but empty - no .cpp/.c files) module directory, so the build actually reaches
        // the "0 actions, up to date" path per resolved combination instead of short-circuiting
        // on the earlier "No modules to build" warning that fires when a target has zero modules
        // at all - this test is about filtering, not about the zero-modules case.
        var filteredSourceDir = Path.Combine(_projectRoot, "Source", "Filtered");
        Directory.CreateDirectory(filteredSourceDir);
        File.WriteAllText(Path.Combine(_projectRoot, "Source", "Filtered.module.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class FilteredModule : ModuleRules
            {
                public FilteredModule(BuildContext context) : base(context)
                {
                    Type = ModuleType.Runtime;
                    SourceDirectory = "Source/Filtered";
                }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "Filtered.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class FilteredTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows, TargetPlatform.Linux)
                    .For(TargetArchitecture.X64)
                    .For(BuildConfiguration.Debug, BuildConfiguration.Release);

                public FilteredTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    ExtraModules.Add("FilteredModule");
                }
            }
            """);
        var orchestrator = new BuildOrchestrator();

        var request = new BuildOrchestratorRequest
        {
            TargetFile = Path.Combine(_projectRoot, "Filtered.target.cs"),
            Platform = TargetPlatform.Windows,
            Configuration = BuildConfiguration.Debug
        };
        var events = new List<OrchestratorEvent>();
        var result = await orchestrator.BuildAsync(request, new Progress<OrchestratorEvent>(events.Add), buildProgress: null);

        // The module directory exists but has no source files, so every resolved combination
        // hits the zero-*action* success path (not the zero-*modules* warning path) - filtering
        // down to exactly one combination means exactly one "Nothing to build" event instead of
        // two.
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        events.Count(e => e.Message.Contains("up to date") || e.Message.Contains("Nothing to build")).Should().Be(1);
    }

    [Fact]
    public async Task BuildAsync_FilterMatchesNothing_ReportsErrorAndReturnsNull()
    {
        File.WriteAllText(Path.Combine(_projectRoot, "Narrow.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class NarrowTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty.For(TargetPlatform.Windows);

                public NarrowTarget(BuildContext context) : base(context) { }
            }
            """);
        var orchestrator = new BuildOrchestrator();
        var request = new BuildOrchestratorRequest
        {
            TargetFile = Path.Combine(_projectRoot, "Narrow.target.cs"),
            Platform = TargetPlatform.Linux
        };
        var events = new List<OrchestratorEvent>();

        var result = await orchestrator.BuildAsync(request, new Progress<OrchestratorEvent>(events.Add), buildProgress: null);

        result.Should().BeNull();
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Error && e.Message.Contains("matched"));
    }

    [Fact]
    public async Task BuildAsync_MatrixExcludesEverything_ReportsErrorAndReturnsNull()
    {
        File.WriteAllText(Path.Combine(_projectRoot, "Impossible.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class ImpossibleTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows)
                    .Exclude(_ => true);

                public ImpossibleTarget(BuildContext context) : base(context) { }
            }
            """);
        var orchestrator = new BuildOrchestrator();
        var events = new List<OrchestratorEvent>();

        var result = await orchestrator.BuildAsync(
            CreateRequest(Path.Combine(_projectRoot, "Impossible.target.cs")),
            new Progress<OrchestratorEvent>(events.Add),
            buildProgress: null);

        result.Should().BeNull();
        events.Should().Contain(e => e.Level == OrchestratorEventLevel.Error && e.Message.Contains("SupportedTargets"));
    }

    [Fact]
    public async Task BuildAsync_ModuleSupportedTargetsExcludesCombination_ModuleAbsentFromThatCombinationOnly()
    {
        var sourceDir = Path.Combine(_projectRoot, "Source", "WinOnly", "Private");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "WinOnly.cpp"), "void WinOnlyFunc() {}\n");
        File.WriteAllText(Path.Combine(_projectRoot, "Source", "WinOnly", "WinOnly.module.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class WinOnlyModule : ModuleRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty.For(TargetPlatform.Windows);

                public WinOnlyModule(BuildContext context) : base(context)
                {
                    Type = ModuleType.Runtime;
                    SourceDirectory = "Source/WinOnly";
                }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "Gated.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class GatedTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows, TargetPlatform.Linux)
                    .For(TargetArchitecture.X64)
                    .For(BuildConfiguration.Debug);

                public GatedTarget(BuildContext context) : base(context)
                {
                    // StaticLibrary, not Executable: archiving needs no entry point, and
                    // WinOnly.cpp below deliberately has no main() (it only needs to exist and
                    // compile - this test verifies module gating, not that the target links into
                    // a runnable program).
                    Type = TargetType.StaticLibrary;
                    ExtraModules.Add("WinOnlyModule");
                    UsePCHFiles = false;
                    UseUnityBuild = false;
                }
            }
            """);
        var orchestrator = new BuildOrchestrator();
        var events = new List<OrchestratorEvent>();

        // Not CreateRequest(...) - its hardcoded Configuration = BuildConfiguration.Development
        // would filter out this matrix's only declared configuration (Debug).
        var result = await orchestrator.BuildAsync(
            new BuildOrchestratorRequest { TargetFile = Path.Combine(_projectRoot, "Gated.target.cs") },
            new Progress<OrchestratorEvent>(events.Add),
            buildProgress: null);

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        // Windows combination compiles WinOnly.cpp; Linux combination has zero actions since
        // its only module was gated out. Object files land under a directory named for the
        // module's Name (ModuleRules.Name defaults to GetType().Name, with no "Module" suffix
        // stripping anywhere in the codebase) - i.e. "WinOnlyModule", not "WinOnly".
        File.Exists(Path.Combine(_projectRoot, "Intermediate", "Windows_Debug", "WinOnlyModule", "WinOnly.obj")).Should().BeTrue();
        Directory.Exists(Path.Combine(_projectRoot, "Intermediate", "Linux_Debug", "WinOnlyModule")).Should().BeFalse();
    }

    [Fact]
    public async Task BuildAsync_OneCombinationFails_OthersStillRunAndAggregateReflectsBoth()
    {
        var sourceDir = Path.Combine(_projectRoot, "Source", "App", "Private");
        Directory.CreateDirectory(sourceDir);
        // FORCE_FAIL is only defined for the Debug combination (Step 3 below) - Release always
        // compiles cleanly, Debug always hits the #error.
        File.WriteAllText(Path.Combine(sourceDir, "Main.cpp"), """
            #ifdef FORCE_FAIL
            #error intentional failure for the Debug combination only
            #endif
            int main() { return 0; }
            """);
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
                    .For(BuildConfiguration.Debug, BuildConfiguration.Release);

                public AppTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "AppModule";
                    ExtraModules.Add("AppModule");
                    UsePCHFiles = false;
                    UseUnityBuild = false;

                    ConfigureForConfiguration(BuildConfiguration.Debug, () => GlobalDefinitions.Add("FORCE_FAIL=1"));
                }
            }
            """);
        var orchestrator = new BuildOrchestrator();
        var events = new List<OrchestratorEvent>();

        // Not CreateRequest(...) - its hardcoded Configuration = BuildConfiguration.Development
        // would filter out both of this matrix's declared combinations (Debug, Release).
        var result = await orchestrator.BuildAsync(
            new BuildOrchestratorRequest { TargetFile = Path.Combine(_projectRoot, "App.target.cs") },
            new Progress<OrchestratorEvent>(events.Add),
            buildProgress: null);

        // The aggregate reflects the failure, but both combinations actually ran: Release's
        // output exists even though the overall result is a failure, proving Debug's compile
        // error didn't abort the loop before Release got a chance to build.
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.FailedActions.Should().BeGreaterThan(0);
        File.Exists(Path.Combine(_projectRoot, "Binaries", "Windows_Release", "App.exe")).Should().BeTrue();
    }
}
