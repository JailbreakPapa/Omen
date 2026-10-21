// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Executors.Orchestration;
using Omen.Executors.Server;

namespace Omen.Executors.Tests;

public class BuildWarmStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(BuildWarmStateTests), Guid.NewGuid().ToString("N"));
    private readonly BuildWarmState _warm = new();

    public BuildWarmStateTests() => TestProjects.WriteOneFileProject(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Task<Omen.Core.Interfaces.BuildResult?> Build(FakeExecutor? executor = null) =>
        new BuildOrchestrator(new BuildServices { Warm = _warm, ExecutorFactory = (_, _) => executor ?? new FakeExecutor() })
            .BuildAsync(new BuildOrchestratorRequest
            {
                TargetFile = Path.Combine(_root, "App.target.cs"),
                Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64, Configuration = BuildConfiguration.Development,
            }, null, null);

    [Fact(Timeout = 60_000)]
    public async Task SecondBuild_CompilesRulesAndClosureOnce()
    {
        await Build();
        _warm.ApplyPendingChanges();
        await Build();

        _warm.RuleCompilations.Should().Be(1);
        _warm.ClosureComputations.Should().Be(1);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("App.target.cs")]
    [InlineData("Source/Core/Core.module.cs")]
    [InlineData("Source/Core/plugin.json")]
    public async Task RuleFileChange_RecompilesRules(string relative)
    {
        await Build();
        _warm.Enqueue(new FileChange(Path.Combine(_root, relative), FileChangeKind.Changed));
        _warm.ApplyPendingChanges();
        await Build();

        _warm.RuleCompilations.Should().Be(2);
        _warm.ClosureComputations.Should().Be(2);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(FileChangeKind.Created)]
    [InlineData(FileChangeKind.Deleted)]
    [InlineData(FileChangeKind.Renamed)]
    public async Task SourceAddedRemovedOrRenamed_RecomputesClosureOnly(FileChangeKind kind)
    {
        await Build();
        _warm.Enqueue(new FileChange(Path.Combine(_root, "Source", "Core", "New.cpp"), kind));
        _warm.ApplyPendingChanges();
        await Build();

        _warm.RuleCompilations.Should().Be(1);
        _warm.ClosureComputations.Should().Be(2);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(FileChangeKind.Created)]
    [InlineData(FileChangeKind.Deleted)]
    [InlineData(FileChangeKind.Renamed)]
    public async Task GeneratedSourceUnderIntermediate_KeepsTheClosure(FileChangeKind kind)
    {
        // Every build writes unity and moc .cpp files under Intermediate/; they never change what a module's
        // rules pick up, and treating them as added sources recomputed the closure on every warm build.
        await Build();
        _warm.Enqueue(new FileChange(Path.Combine(_root, "Intermediate", "Win64_Development", "Unity", "Core.Unity0.cpp"), kind));
        _warm.ApplyPendingChanges();
        await Build();

        _warm.ClosureComputations.Should().Be(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task SourceContentChange_KeepsRulesAndClosure()
    {
        await Build();
        _warm.Enqueue(new FileChange(Path.Combine(_root, "Source", "Core", "Core.cpp"), FileChangeKind.Changed));
        _warm.ApplyPendingChanges();
        await Build();

        _warm.RuleCompilations.Should().Be(1);
        _warm.ClosureComputations.Should().Be(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentBuilds_InstantiateRulesOneAtATime()
    {
        // A module constructor may write into the intermediate directory (AddAutoGen writes its input list and runs
        // AzAutoGen's dry run); four Visual Studio projects building at once collided on that file.
        File.WriteAllText(Path.Combine(_root, "Source", "Core", "Core.module.cs"), """
            using System.IO;
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class CoreModule : ModuleRules
            {
                public CoreModule(BuildContext context) : base(context)
                {
                    SourceDirectory = "Source/Core";
                    Directory.CreateDirectory(context.IntermediateDirectory);
                    using var generated = new FileStream(Path.Combine(context.IntermediateDirectory, "generated.txt"), FileMode.Create, FileAccess.Write, FileShare.None);
                    System.Threading.Thread.Sleep(500);
                }
            }
            """);

        // As the build server runs them: it holds the intermediate directory itself, so builds do not take turns on it.
        Task<Omen.Core.Interfaces.BuildResult?> ServerBuild() =>
            new BuildOrchestrator(new BuildServices { Warm = _warm, CallerHoldsIntermediateLock = true, ExecutorFactory = (_, _) => new FakeExecutor() })
                .BuildAsync(new BuildOrchestratorRequest
                {
                    TargetFile = Path.Combine(_root, "App.target.cs"),
                    Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64, Configuration = BuildConfiguration.Development,
                }, null, null);

        var results = await Task.WhenAll(ServerBuild(), ServerBuild(), ServerBuild());

        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        results.Should().AllSatisfy(r => r!.Success.Should().BeTrue());
    }

    [Fact(Timeout = 60_000)]
    public async Task ChangedPersistedEnvironment_IsTakenAndRecomputesTheClosure()
    {
        // Rules read environment variables (HOUDINI_ENGINE_HFS); a server kept the ones of the client that started it.
        var name = "OMEN_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "old");
        try
        {
            await Build();
            _warm.ApplyEnvironment(new Dictionary<string, string> { [name] = "old" }).Should().BeFalse();
            _warm.ApplyEnvironment(new Dictionary<string, string> { [name] = "new" }).Should().BeTrue();
            await Build();

            Environment.GetEnvironmentVariable(name).Should().Be("new");
            _warm.ClosureComputations.Should().Be(2);
            _warm.RuleCompilations.Should().Be(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void PersistedEnvironment_OnThisMachine_ChangesNothingInAFreshProcess()
    {
        // This test host was started with the registry's variables; were any raw (unexpanded %VAR%), applying them
        // would rewrite TEMP and friends for every tool the server launches.
        var temp = Environment.GetEnvironmentVariable("TEMP");
        _warm.ApplyEnvironment(BuildWarmState.PersistedEnvironment());
        Environment.GetEnvironmentVariable("TEMP").Should().Be(temp);
    }

    [Fact]
    public void PersistedEnvironment_LeavesPathAlone()
    {
        // The launching shell's PATH (a VS developer prompt, dotnet tools) is not the registry's.
        var path = Environment.GetEnvironmentVariable("PATH");
        _warm.ApplyEnvironment(new Dictionary<string, string> { ["Path"] = @"C:\nowhere" }).Should().BeFalse();
        Environment.GetEnvironmentVariable("PATH").Should().Be(path);
    }

    [Fact(Timeout = 60_000)]
    public async Task SecondBuild_ReusesTheActionGraph()
    {
        await Build();
        _warm.ApplyPendingChanges();
        var second = new FakeExecutor();
        await Build(second);

        _warm.GraphBuilds.Should().Be(1);
        second.Ran.Should().BeEmpty("the reused graph is reset, and its actions are up to date");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(FileChangeKind.Changed)]
    [InlineData(FileChangeKind.Created)]
    public async Task SourceEditOrAdd_RebuildsTheActionGraph(FileChangeKind kind)
    {
        // An edited source can gain an #include, so its action's inputs must be rescanned.
        await Build();
        _warm.Enqueue(new FileChange(Path.Combine(_root, "Source", "Core", kind == FileChangeKind.Changed ? "Core.cpp" : "New.cpp"), kind));
        _warm.ApplyPendingChanges();
        await Build();

        _warm.GraphBuilds.Should().Be(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task SourceEditAppliedWhileTheGraphIsInUse_IsNotCached()
    {
        // A concurrent request applies the watcher's changes while this build still holds the graph.
        await Build();
        var source = Path.Combine(_root, "Source", "Core", "Core.cpp");
        await Build(new FakeExecutor
        {
            OnExecute = _ =>
            {
                _warm.Enqueue(new FileChange(source, FileChangeKind.Changed));
                _warm.ApplyPendingChanges();
            },
        });
        await Build();

        _warm.GraphBuilds.Should().Be(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task OutputWritten_KeepsTheActionGraph()
    {
        // Every build writes objects and libraries that are other actions' inputs; they never change the graph.
        await Build();
        foreach (var obj in Directory.EnumerateFiles(_root, "*.obj", SearchOption.AllDirectories))
            _warm.Enqueue(new FileChange(obj, FileChangeKind.Changed));
        _warm.ApplyPendingChanges();
        await Build();

        _warm.GraphBuilds.Should().Be(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task Overflow_DropsEverything()
    {
        await Build();
        _warm.EnqueueOverflow();
        _warm.ApplyPendingChanges();
        await Build();

        _warm.RuleCompilations.Should().Be(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task IntermediateDeleted_DropsEverything()
    {
        await Build();
        Directory.Delete(Path.Combine(_root, "Intermediate"), recursive: true);
        _warm.Enqueue(new FileChange(Path.Combine(_root, "Intermediate"), FileChangeKind.Deleted));
        _warm.ApplyPendingChanges();
        var executor = new FakeExecutor();
        await Build(executor);

        executor.Ran.Should().NotBeEmpty("the objects went with Intermediate/, so the compile must run again");
        _warm.RuleCompilations.Should().Be(2);
    }

    [Fact]
    public void Stamps_OutsideTheWatchedRoot_AreNeverCached()
    {
        _warm.WatchedRoot = _root;
        var outside = Path.Combine(Path.GetTempPath(), "OmenTests", Guid.NewGuid().ToString("N") + ".h");
        File.WriteAllText(outside, "a");
        var first = _warm.Stamps.Get(outside);
        File.SetLastWriteTimeUtc(outside, DateTime.UtcNow.AddHours(1));

        _warm.Stamps.Get(outside).Should().NotBe(first);
        File.Delete(outside);
    }

    [Fact]
    public void Stamps_ThroughAJunctionUnderTheRoot_AreNeverCached()
    {
        // FileSystemWatcher does not report changes behind a junction, so a path through one is outside the watch
        // even though its text starts with the root (3rdParty SDKs are junctioned in from other drives).
        _warm.WatchedRoot = _root;
        var elsewhere = Path.Combine(Path.GetTempPath(), "OmenTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "Sdk.h"), "a");
        var junction = Path.Combine(_root, "3rdParty");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{elsewhere}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();
        var header = Path.Combine(junction, "Sdk.h");

        _warm.IsWatched(header).Should().BeFalse();
        _warm.IsWatched(Path.Combine(_root, "Source", "Core", "Core.cpp")).Should().BeTrue();
        var first = _warm.Stamps.Get(header);
        File.SetLastWriteTimeUtc(Path.Combine(elsewhere, "Sdk.h"), DateTime.UtcNow.AddHours(1));
        _warm.Stamps.Get(header).Should().NotBe(first);

        Directory.Delete(junction);
        Directory.Delete(elsewhere, recursive: true);
    }

    [Fact]
    public async Task RuleFileChange_KeepsFileStampsButRebuildsTheGraph()
    {
        // Rules decide flags and module sets, not what a header contains: after another session's rule edit, the
        // next build re-stat'ing every header took 80-126 s on the engine while other builds loaded the disk.
        _warm.WatchedRoot = _root;
        await Build();
        var source = Path.Combine(_root, "Source", "Core", "Core.cpp");
        var first = _warm.Stamps.Get(source);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1));

        _warm.Enqueue(new FileChange(Path.Combine(_root, "Source", "Core", "Core.module.cs"), FileChangeKind.Changed));
        _warm.ApplyPendingChanges();

        _warm.Stamps.Get(source).Should().Be(first);
        await Build();
        _warm.GraphBuilds.Should().Be(2);
    }

    [Fact]
    public void Stamps_InsideTheWatchedRoot_AreCachedUntilAChangeArrives()
    {
        _warm.WatchedRoot = _root;
        var source = Path.Combine(_root, "Source", "Core", "Core.cpp");
        var first = _warm.Stamps.Get(source);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1));
        _warm.Stamps.Get(source).Should().Be(first);

        _warm.Enqueue(new FileChange(source, FileChangeKind.Changed));
        _warm.ApplyPendingChanges();
        _warm.Stamps.Get(source).Should().NotBe(first);
    }

    [Fact(Timeout = 60_000)]
    public async Task EditedSourceListedWithForwardSlashes_RecompilesOnTheNextBuild()
    {
        // An explicit SourceFiles entry ("Sub/Thing.cpp") reached the stamp cache as a mixed-slash path; the watcher's
        // change arrives normalized, so the cached stamp was never evicted and the edit reported "up to date".
        _warm.WatchedRoot = _root;
        var source = Path.Combine(_root, "Source", "Core", "Sub", "Thing.cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "int Thing() { return 0; }\n");
        File.WriteAllText(Path.Combine(_root, "Source", "Core", "Core.module.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class CoreModule : ModuleRules
            {
                public CoreModule(BuildContext context) : base(context) { SourceDirectory = "Source/Core"; SourceFiles.Add("Sub/Thing.cpp"); }
            }
            """);
        await Build();
        await Build(); // up to date: this one caches the source's stamp

        File.WriteAllText(source, "int Thing() { return 1; }\n");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1));
        _warm.Enqueue(new FileChange(source, FileChangeKind.Changed));
        _warm.ApplyPendingChanges();
        var executor = new FakeExecutor();
        await Build(executor);

        executor.Ran.Should().Contain(d => d.Contains("Thing.cpp"));
    }

    [Fact(Timeout = 60_000)]
    public async Task EditedHeader_IsRescannedByTheWarmIncludeScanner()
    {
        _warm.WatchedRoot = _root;
        var header = Path.Combine(_root, "Source", "Core", "Core.h");
        File.WriteAllText(header, "#pragma once\n");
        File.WriteAllText(Path.Combine(_root, "Source", "Core", "Core.cpp"), "#include \"Core.h\"\nint Core() { return 0; }\n");
        await Build();

        var extra = Path.Combine(_root, "Source", "Core", "Extra.h");
        File.WriteAllText(extra, "#pragma once\n");
        File.WriteAllText(header, "#pragma once\n#include \"Extra.h\"\n");
        // Only the newly reached Extra.h may make Core.cpp stale, so the rescan is what is being tested.
        File.SetLastWriteTimeUtc(header, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _warm.Enqueue(new FileChange(header, FileChangeKind.Changed));
        _warm.Enqueue(new FileChange(extra, FileChangeKind.Created));
        _warm.ApplyPendingChanges();
        File.SetLastWriteTimeUtc(extra, DateTime.UtcNow.AddHours(1));
        _warm.Enqueue(new FileChange(extra, FileChangeKind.Changed));
        _warm.ApplyPendingChanges();
        var executor = new FakeExecutor();
        await Build(executor);

        executor.Ran.Should().Contain(d => d.Contains("Core.cpp"), "Extra.h is newer than Core.obj and is now reached through Core.h");
    }

    [Fact(Timeout = 60_000)]
    public async Task OutputsWrittenByOneBuild_AreUpToDateForAConcurrentBuild()
    {
        _warm.WatchedRoot = _root;
        await Build();
        var second = new FakeExecutor();
        await Build(second); // started before any watcher change was applied, like a concurrent server request

        second.Ran.Should().BeEmpty("the first build's outputs are on disk and their digests recorded");
    }

    [Fact(Timeout = 60_000)]
    public async Task ChangesQueuedDuringABuild_ApplyAtTheNextRequest()
    {
        await Build();
        _warm.Enqueue(new FileChange(Path.Combine(_root, "App.target.cs"), FileChangeKind.Changed));
        await Build(); // no ApplyPendingChanges: this build must still use the rules it had
        _warm.RuleCompilations.Should().Be(1);

        _warm.ApplyPendingChanges();
        await Build();
        _warm.RuleCompilations.Should().Be(2);
    }
}

