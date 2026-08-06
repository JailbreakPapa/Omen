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
    public async Task CleanAsync_AfterCacheHitBuild_DoesNotLockRuleCacheDll()
    {
        // Regression test for RuleCompiler's cache-hit path locking the cached rule DLL
        // (see RuleCompiler.LoadAssembly). The CLI never hit this because every command is
        // its own process, but the GUI builds and cleans within one long-lived process: a
        // second build hits the RuleCache, and a Clean in that same session used to fail to
        // delete Intermediate/RuleCache/<hash>.dll because the file was still memory-mapped.
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

        // First build compiles the rules fresh; second build hits RuleCompiler's RuleCache.
        (await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null))!
            .Success.Should().BeTrue();
        (await orchestrator.BuildAsync(CreateRequest(targetFile), events: null, buildProgress: null))!
            .Success.Should().BeTrue();

        var cleanResult = await new CleanOrchestrator().CleanAsync(
            new CleanOrchestratorRequest { ProjectRoot = _projectRoot },
            events: null);

        cleanResult.DirectoriesFailed.Should().Be(0);
        Directory.Exists(Path.Combine(_projectRoot, "Intermediate")).Should().BeFalse();
    }
}
