# Omen Build Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `omen build` talks to one warm, long-lived `omen server` per engine root and configuration, so builds after the first skip rule compilation and instantiation, and concurrent builds share one prioritized pool instead of queueing on a directory lock.

**Architecture:** `BuildOrchestrator` gains an optional `BuildServices` seam: supplied warm state (compiled rules, module closures, action graphs, include scanner, digest store, file stamps), a supplied executor factory, and "the caller already holds the intermediate lock". Without it, behaviour is today's. The server supplies all three: a `BuildWarmState` kept correct by a `FileSystemWatcher`, and a `SharedActionScheduler` that hands each request an `IExecutor` over one shared slot pool. A named-pipe protocol carries requests, events, progress and results between `omen build` (client) and `omen server`.

**Tech Stack:** C# / .NET 8, System.CommandLine, Spectre.Console, xUnit + FluentAssertions, `System.IO.Pipes`, `System.Text.Json`.

**Spec:** `docs/superpowers/specs/2026-09-27-omen-build-server-design.md` (12822e6).

**Spec deltas decided while planning:**
- Server code lives in `src/Omen.Executors/Server/` (namespace `Omen.Executors.Server`), tested in `tests/Omen.Executors.Tests`, instead of a new `Omen.Server` project: it depends on nothing the Executors project lacks, and a project + test project pair buys nothing here.
- Source add/delete/rename throws away **every** closure and graph, not only the affected module's. Mapping a path to the module that globs it is extra code for a rare event; the next build pays one closure/graph rebuild (rules stay warm).
- The action-graph cache (spec §3.1 row 3) is Task 4, after rules and closures are warm and measured; if Task 4's measurement shows graph building under 1 s warm on `NightFoxTestRunner`, stop after its measurement step and ledger that the cache is not needed.

## Global Constraints

- Repo: `F:\Omen-githubpublic`. Another session has uncommitted edits in `src/Omen.Core/Graph/ActionGraphBuilder.cs`, `src/Omen.Core/Interfaces/IToolchain.cs`, `src/Omen.Core/Rules/ModuleRules.cs`, `src/Omen.Core/Rules/PluginFlavorModuleRules.cs`, `src/Omen.Core/Rules/PluginRules.cs`, `src/Omen.Platforms/ToolchainBase.cs`, `src/Omen.Platforms/Windows/MsvcToolchain.cs`, `tests/Omen.Core.Tests/RuleCompilerPluginTests.cs`. Never stage those hunks. If a task must edit one of those files, stage only your insertion with `F:\engine\.superpowers\sdd\2026-09-26-houdini-world-step1\stage_insertions.py`'s approach (index blob = `HEAD` + your insertion). Every other file: `git add <paths> && git commit -m "..." -- <paths>`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Test commands: `dotnet test tests/Omen.Core.Tests`, `dotnet test tests/Omen.Executors.Tests`, `dotnet test tests/Omen.GUI.Tests`. Full suites must stay green (baseline: 476 / 62 / 36).
- `--no-server` behaviour must stay byte-for-byte today's: same events, same exit codes, same BuildEvents files.
- The global `omen` tool must never see a rule-file API it lacks. This plan changes no rule-facing API (`ModuleRules`, `TargetRules`, `PluginRules`, `BuildContext` public surface); keep it that way.
- Server pipe name: `omen-<root>-<config>`, where `<root>` is the project root with `:` `\` `/` replaced by `_`, e.g. `omen-F__engine-Development`. Idle exit default 30 min. Pool default `Environment.ProcessorCount - 2` (minimum 1). Background minimum slot applies at pool size ≥ 4.
- Style: match the surrounding Omen code (file-scoped namespaces, `sealed` classes, XML doc comments on public members explaining *why*, no comments restating *what*).

## Review Focus

1. **A rule file edited while a build is running** — the running build must finish on the rules it started with; the next request must see the edit. Pinned by `BuildWarmStateTests.ChangesQueuedDuringABuild_ApplyAtTheNextRequest` (Task 2).
2. **`omen clean` or a hand-deleted `Intermediate/`** — warm state that points at deleted files must not produce a "nothing to build". Pinned by `BuildWarmStateTests.IntermediateDeleted_DropsEverything` (Task 3).
3. **Two requests for the same target at once** (Visual Studio double-click) — every action runs once, both succeed. Pinned by `SharedActionSchedulerTests.SameGraphTwice_EachActionRunsOnce` (Task 5).
4. **Client killed with Ctrl+C mid-build while another client shares its actions** — the other client's build still completes. Pinned by `SharedActionSchedulerTests.Cancel_KeepsSharedWork` (Task 5).
5. **An old server still running after `update-omen-tool.ps1`** — the new client must not be served by old code. Pinned by `ServerClientTests.VersionMismatch_OldServerDrainsAndExits` (Task 7).

---

### Task 1: `BuildServices` seam in `BuildOrchestrator`

**Files:**
- Create: `src/Omen.Executors/Orchestration/BuildServices.cs`
- Modify: `src/Omen.Executors/Orchestration/BuildOrchestrator.cs` (constructor, lock acquisition at ~260, executor creation at ~579)
- Test: `tests/Omen.Executors.Tests/BuildServicesTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class BuildServices
  {
      public BuildWarmState? Warm { get; init; }                       // Task 2+; null = cold
      public Func<int, Func<BuildAction, bool>, IExecutor>? ExecutorFactory { get; init; } // (jobs, isUpToDate) -> executor; null = ParallelExecutor
      public bool CallerHoldsIntermediateLock { get; init; }          // server holds build.lock itself
  }
  public BuildOrchestrator(BuildServices? services = null)
  ```
  (`BuildWarmState` is declared in Task 2; in this task add an empty `public sealed class BuildWarmState { }` in `src/Omen.Executors/Server/BuildWarmState.cs` so the property compiles; Task 2 fills it.)

- [ ] **Step 1: Write the failing tests**

`tests/Omen.Executors.Tests/BuildServicesTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Interfaces;
using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public class BuildServicesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(BuildServicesTests), Guid.NewGuid().ToString("N"));

    public BuildServicesTests() => TestProjects.WriteOneFileProject(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private BuildOrchestratorRequest Request() => new()
    {
        TargetFile = Path.Combine(_root, "App.target.cs"),
        Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64, Configuration = BuildConfiguration.Development,
    };

    [Fact]
    public async Task ExecutorFactory_RunsTheGraph()
    {
        var used = 0;
        var services = new BuildServices
        {
            ExecutorFactory = (jobs, isUpToDate) => { used++; return new FakeExecutor(); },
        };

        var result = await new BuildOrchestrator(services).BuildAsync(Request(), null, null);

        result.Should().NotBeNull();
        used.Should().Be(1);
    }

    [Fact]
    public async Task CallerHoldsIntermediateLock_DoesNotWaitOnIt()
    {
        var lockDir = Path.Combine(_root, "Intermediate", "Windows_Development", ".buildtool");
        Directory.CreateDirectory(lockDir);
        using var held = new FileStream(Path.Combine(lockDir, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var services = new BuildServices { CallerHoldsIntermediateLock = true, ExecutorFactory = (_, _) => new FakeExecutor() };

        var build = new BuildOrchestrator(services).BuildAsync(Request(), null, null);

        (await Task.WhenAny(build, Task.Delay(TimeSpan.FromSeconds(60)))).Should().BeSameAs(build);
    }
}
```

`tests/Omen.Executors.Tests/TestProjects.cs` (shared fixture helpers used by Tasks 1-8):

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Executors.Tests;

/// <summary>Tiny on-disk projects for orchestrator/server tests. Nothing here invokes a compiler.</summary>
internal static class TestProjects
{
    public static void WriteOneFileProject(string root, string moduleName = "CoreModule", params string[] sources)
    {
        var sourceDir = Path.Combine(root, "Source", "Core");
        Directory.CreateDirectory(sourceDir);
        foreach (var source in sources.Length == 0 ? ["Core.cpp"] : sources)
            File.WriteAllText(Path.Combine(sourceDir, source), $"int {Path.GetFileNameWithoutExtension(source)}() {{ return 0; }}\n");
        File.WriteAllText(Path.Combine(sourceDir, "Core.module.cs"), $$"""
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class {{moduleName}} : ModuleRules
            {
                public {{moduleName}}(BuildContext context) : base(context) { SourceDirectory = "Source/Core"; }
            }
            """);
        File.WriteAllText(Path.Combine(root, "App.target.cs"), $$"""
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppTarget : TargetRules
            {
                public AppTarget(BuildContext context) : base(context) { Type = TargetType.StaticLibrary; LaunchModuleName = "{{moduleName}}"; UseUnityBuild = false; }
            }
            """);
    }
}

/// <summary>Completes every pending action by writing its outputs; records what it ran.</summary>
internal sealed class FakeExecutor : IExecutor
{
    public List<string> Ran { get; } = [];
    public string Name => "Fake";
    public int MaxParallelism => 1;

    public Task<BuildResult> ExecuteAsync(ActionGraph graph, IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
    {
        foreach (var action in graph.GetTopologicalOrder().Where(a => a.Status == ActionStatus.Pending))
        {
            foreach (var output in action.Outputs)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output.Path)!);
                File.WriteAllText(output.Path, action.Id);
            }
            action.Status = ActionStatus.Completed;
            Ran.Add(action.Description);
        }
        return Task.FromResult(new BuildResult
        {
            Success = true, TotalDuration = TimeSpan.Zero, TotalActions = graph.Actions.Count,
            SuccessfulActions = Ran.Count, FailedActions = 0,
            SkippedActions = graph.Actions.Count(a => a.Status == ActionStatus.Skipped), CachedActions = 0,
        });
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Executors.Tests --filter BuildServicesTests`
Expected: compile error — `BuildServices` / the `BuildOrchestrator(BuildServices)` constructor do not exist.

- [ ] **Step 3: Implement**

`src/Omen.Executors/Orchestration/BuildServices.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Interfaces;
using Omen.Executors.Server;

namespace Omen.Executors.Orchestration;

/// <summary>
/// What a long-lived host (the build server) lends a <see cref="BuildOrchestrator"/>: state kept warm across
/// builds, the executor that runs the graph, and whether it already holds the intermediate directory. Null
/// members mean "build cold, alone, with a ParallelExecutor" - the in-process <c>omen build --no-server</c> path.
/// </summary>
public sealed class BuildServices
{
    public BuildWarmState? Warm { get; init; }

    /// <summary>(jobs, isUpToDate) -> the executor for one combination's graph.</summary>
    public Func<int, Func<BuildAction, bool>, IExecutor>? ExecutorFactory { get; init; }

    /// <summary>The caller holds <c>Intermediate/&lt;config&gt;/.buildtool/build.lock</c> for its own lifetime.</summary>
    public bool CallerHoldsIntermediateLock { get; init; }
}
```

`src/Omen.Executors/Server/BuildWarmState.cs` (placeholder filled in Task 2):

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Executors.Server;

/// <summary>State a build server keeps warm between builds. Filled in by the warm-state task.</summary>
public sealed class BuildWarmState
{
}
```

In `BuildOrchestrator`:

```csharp
    private readonly BuildServices _services;

    public BuildOrchestrator(BuildServices? services = null) => _services = services ?? new BuildServices();
```

Wrap the lock block (lines ~260-272) so it only runs when `!_services.CallerHoldsIntermediateLock`:

```csharp
            FileStream? buildLock = null;
            if (!_services.CallerHoldsIntermediateLock)
            {
                buildLock = TryAcquireBuildLock(context.IntermediateDirectory);
                if (buildLock == null)
                {
                    // (existing "Another Omen build holds ..." report and wait loop, unchanged)
                }
            }
            using var _ = buildLock;
```

Replace the executor construction in `BuildOneCombinationAsync`:

```csharp
        Func<BuildAction, bool> isUpToDate = action => graph.IsUpToDate(action, digestCalculator, digestStore);
        var executor = _services.ExecutorFactory?.Invoke(jobs ?? Environment.ProcessorCount, isUpToDate)
            ?? new ParallelExecutor(jobs, actionCache, isUpToDate);
```

(`BuildOneCombinationAsync` is `private async` on the instance already, so `_services` is in scope.)

- [ ] **Step 4: Run the tests and the suites**

Run: `dotnet test tests/Omen.Executors.Tests` then `dotnet test tests/Omen.Core.Tests` and `dotnet test tests/Omen.GUI.Tests`
Expected: new tests PASS; suites 476 / 62+2 / 36, no failures.

- [ ] **Step 5: Commit**

```bash
git add src/Omen.Executors/Orchestration/BuildServices.cs src/Omen.Executors/Server/BuildWarmState.cs src/Omen.Executors/Orchestration/BuildOrchestrator.cs tests/Omen.Executors.Tests/BuildServicesTests.cs tests/Omen.Executors.Tests/TestProjects.cs
git commit -m "BuildOrchestrator: optional BuildServices (warm state, executor factory, caller-held lock)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" -- src/Omen.Executors/Orchestration/BuildServices.cs src/Omen.Executors/Server/BuildWarmState.cs src/Omen.Executors/Orchestration/BuildOrchestrator.cs tests/Omen.Executors.Tests/BuildServicesTests.cs tests/Omen.Executors.Tests/TestProjects.cs
```

---

### Task 2: Warm compiled rules and module closures

**Files:**
- Modify: `src/Omen.Executors/Server/BuildWarmState.cs`
- Modify: `src/Omen.Executors/Orchestration/BuildOrchestrator.cs` (rule compilation at ~111-124; instantiation/closure at ~324-359)
- Test: `tests/Omen.Executors.Tests/BuildWarmStateTests.cs`

**Interfaces:**
- Consumes: `BuildServices.Warm` (Task 1).
- Produces:
  ```csharp
  public enum FileChangeKind { Changed, Created, Deleted, Renamed }
  public readonly record struct FileChange(string Path, FileChangeKind Kind);

  public sealed class BuildWarmState
  {
      public int RuleCompilations { get; }            // how many times rules were compiled (tests, --status)
      public int ClosureComputations { get; }
      public void Enqueue(FileChange change);        // thread-safe; watcher thread calls this
      public void EnqueueOverflow();                 // watcher lost events: drop everything at next apply
      public void ApplyPendingChanges();             // start of each request only
      public void DropAll();
      // used by BuildOrchestrator:
      internal Task<CompiledRules> GetCompiledRulesAsync(string workingDir, Func<Task<CompiledRules>> compile);
      internal ClosureEntry? GetClosure(string key);
      internal void SetClosure(string key, ClosureEntry entry);
  }
  internal sealed record ClosureEntry(TargetRules TargetRules, IReadOnlyList<ModuleRules> Modules, ModuleClosure Closure);
  ```

- [ ] **Step 1: Write the failing tests**

`tests/Omen.Executors.Tests/BuildWarmStateTests.cs`:

```csharp
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

    [Fact]
    public async Task SecondBuild_CompilesRulesAndClosureOnce()
    {
        await Build();
        _warm.ApplyPendingChanges();
        await Build();

        _warm.RuleCompilations.Should().Be(1);
        _warm.ClosureComputations.Should().Be(1);
    }

    [Theory]
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

    [Theory]
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

    [Fact]
    public async Task SourceContentChange_KeepsRulesAndClosure()
    {
        await Build();
        _warm.Enqueue(new FileChange(Path.Combine(_root, "Source", "Core", "Core.cpp"), FileChangeKind.Changed));
        _warm.ApplyPendingChanges();
        await Build();

        _warm.RuleCompilations.Should().Be(1);
        _warm.ClosureComputations.Should().Be(1);
    }

    [Fact]
    public async Task Overflow_DropsEverything()
    {
        await Build();
        _warm.EnqueueOverflow();
        _warm.ApplyPendingChanges();
        await Build();

        _warm.RuleCompilations.Should().Be(2);
    }

    [Fact]
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Executors.Tests --filter BuildWarmStateTests`
Expected: compile errors — `FileChange`, `Enqueue`, `RuleCompilations` … not defined.

- [ ] **Step 3: Implement `BuildWarmState`**

Replace `src/Omen.Executors/Server/BuildWarmState.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using Omen.Core.Rules;

namespace Omen.Executors.Server;

public enum FileChangeKind { Changed, Created, Deleted, Renamed }

public readonly record struct FileChange(string Path, FileChangeKind Kind);

internal sealed record ClosureEntry(TargetRules TargetRules, IReadOnlyList<ModuleRules> Modules, ModuleClosure Closure);

/// <summary>
/// What a build server keeps between builds, and the rules for throwing it away. Changes arrive on the watcher
/// thread through <see cref="Enqueue"/> but only take effect in <see cref="ApplyPendingChanges"/>, which the
/// server calls at the start of each request - a build never sees its inputs change under it.
/// </summary>
public sealed class BuildWarmState
{
    private static readonly string[] RuleFileSuffixes = [".target.cs", ".module.cs", ".plugin.cs", ".omenproject.cs"];
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cpp", ".c", ".cc", ".cxx", ".h", ".hpp", ".hxx", ".inl" };

    private readonly ConcurrentQueue<FileChange> _pending = new();
    private volatile bool _overflowed;
    private readonly Dictionary<string, CompiledRules> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClosureEntry> _closures = new(StringComparer.Ordinal);
    private int _ruleCompilations;
    private int _closureComputations;

    public int RuleCompilations => _ruleCompilations;
    public int ClosureComputations => _closureComputations;

    public void Enqueue(FileChange change) => _pending.Enqueue(change);

    public void EnqueueOverflow() => _overflowed = true;

    public void ApplyPendingChanges()
    {
        if (_overflowed)
        {
            _overflowed = false;
            _pending.Clear();
            DropAll();
            return;
        }
        while (_pending.TryDequeue(out var change))
        {
            if (IsRuleFile(change.Path))
            {
                DropAll();
            }
            else if (change.Kind != FileChangeKind.Changed && SourceExtensions.Contains(Path.GetExtension(change.Path)))
            {
                lock (_closures) _closures.Clear();
            }
        }
    }

    public void DropAll()
    {
        lock (_rules) _rules.Clear();
        lock (_closures) _closures.Clear();
    }

    internal async Task<CompiledRules> GetCompiledRulesAsync(string workingDir, Func<Task<CompiledRules>> compile)
    {
        lock (_rules)
        {
            if (_rules.TryGetValue(workingDir, out var warm))
                return warm;
        }
        var compiled = await compile();
        Interlocked.Increment(ref _ruleCompilations);
        lock (_rules) _rules[workingDir] = compiled;
        return compiled;
    }

    internal ClosureEntry? GetClosure(string key)
    {
        lock (_closures) return _closures.TryGetValue(key, out var entry) ? entry : null;
    }

    internal void SetClosure(string key, ClosureEntry entry)
    {
        Interlocked.Increment(ref _closureComputations);
        lock (_closures) _closures[key] = entry;
    }

    private static bool IsRuleFile(string path) =>
        RuleFileSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase)) ||
        Path.GetFileName(path).Equals("plugin.json", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Use it in `BuildOrchestrator`**

Rule compilation (replace `compiledRules = await ruleCompiler.CompileRulesAsync(workingDir, ct);`):

```csharp
            compiledRules = _services.Warm is { } warm
                ? await warm.GetCompiledRulesAsync(workingDir, () => ruleCompiler.CompileRulesAsync(workingDir, ct))
                : await ruleCompiler.CompileRulesAsync(workingDir, ct);
```

Instantiation and closure: wrap the block from `var targetRules = compiledRules.CreateTargetRules(...)` through `Phase("Rule instantiation and closure");` so a warm entry keyed by `$"{workingDir}|{targetType.FullName}|{resolved}|{string.Join(",", enabledPlugins ?? [])}|{variant}"` is reused. The pieces the rest of the method reads are `targetRules`, `includedModules` (pre-closure list feeding `ModuleClosure.Compute`) and `closure`:

```csharp
            var closureKey = $"{workingDir}|{targetType.FullName}|{resolved}|{string.Join(",", enabledPlugins ?? [])}|{variant}";
            TargetRules? targetRules;
            ModuleClosure closure;
            if (_services.Warm?.GetClosure(closureKey) is { } warmClosure)
            {
                targetRules = warmClosure.TargetRules;
                closure = warmClosure.Closure;
            }
            else
            {
                // (the existing code from CreateTargetRules through ModuleClosure.Compute, unchanged, ending in
                //  `closure = ModuleClosure.Compute(...)`, with its early `return null` paths intact)
                _services.Warm?.SetClosure(closureKey, new ClosureEntry(targetRules, includedModules, closure));
            }
            Phase("Rule instantiation and closure");
```

The LTO/link-type overrides applied to `targetRules` today happen inside the else-branch before caching (they depend only on the request and project, which are part of the key through `resolved` and the rules). Add `request.EnableLinkTimeOptimization` to the key: `…|{request.EnableLinkTimeOptimization}`.

- [ ] **Step 5: Run the tests and suites**

Run: `dotnet test tests/Omen.Executors.Tests`, `dotnet test tests/Omen.Core.Tests`, `dotnet test tests/Omen.GUI.Tests`
Expected: `BuildWarmStateTests` 10 PASS (theories expand); suites green.

- [ ] **Step 6: Commit**

```bash
git add src/Omen.Executors/Server/BuildWarmState.cs src/Omen.Executors/Orchestration/BuildOrchestrator.cs tests/Omen.Executors.Tests/BuildWarmStateTests.cs
git commit -m "Build server warm state: compiled rules and module closures, thrown away by file changes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" -- src/Omen.Executors/Server/BuildWarmState.cs src/Omen.Executors/Orchestration/BuildOrchestrator.cs tests/Omen.Executors.Tests/BuildWarmStateTests.cs
```

---

### Task 3: Warm include scanner, digest store and file stamps; equivalence

**Files:**
- Modify: `src/Omen.Executors/Server/BuildWarmState.cs`
- Modify: `src/Omen.Core/Graph/ActionGraph.cs` (stamp lookup)
- Modify: `src/Omen.Executors/Orchestration/BuildOrchestrator.cs` (`BuildOneCombinationAsync`: include scanner ~544-553, digest store ~558)
- Test: `tests/Omen.Executors.Tests/BuildWarmStateTests.cs`, `tests/Omen.Executors.Tests/WarmColdEquivalenceTests.cs`

**Interfaces:**
- Consumes: Task 2's `BuildWarmState`.
- Produces:
  ```csharp
  // Omen.Core.Graph
  public interface IFileStamps { DateTime? Get(string path); }
  public sealed class ActionGraph { public IFileStamps? Stamps { get; set; } }   // used when set, instead of per-pass cache

  // Omen.Executors.Server.BuildWarmState
  public string? WatchedRoot { get; set; }                      // only paths under it are stamp-cached
  internal IncludeScanner GetIncludeScanner(string cachePath);  // loaded once per intermediate dir
  internal ActionDigestStore GetDigestStore(string path);       // one shared instance per intermediate dir
  internal IFileStamps Stamps { get; }                          // evicts on Changed/Created/Deleted/Renamed
  ```

- [ ] **Step 1: Write the failing tests**

Append to `BuildWarmStateTests`:

```csharp
    [Fact]
    public async Task IntermediateDeleted_DropsEverything()
    {
        await Build();
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
    public void Stamps_InsideTheWatchedRoot_AreCachedUntilAChangeArrives()
    {
        _warm.WatchedRoot = _root;
        var header = Path.Combine(_root, "Source", "Core", "Core.cpp");
        var first = _warm.Stamps.Get(header);
        File.SetLastWriteTimeUtc(header, DateTime.UtcNow.AddHours(1));
        _warm.Stamps.Get(header).Should().Be(first);

        _warm.Enqueue(new FileChange(header, FileChangeKind.Changed));
        _warm.ApplyPendingChanges();
        _warm.Stamps.Get(header).Should().NotBe(first);
    }
```

`tests/Omen.Executors.Tests/WarmColdEquivalenceTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Executors.Orchestration;
using Omen.Executors.Server;

namespace Omen.Executors.Tests;

/// <summary>
/// The server's correctness rule: after any edit, a warm build decides exactly what a cold build of the same tree
/// decides. Two copies of one project; each edit is applied to both; one builds cold, one warm.
/// </summary>
public class WarmColdEquivalenceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(WarmColdEquivalenceTests), Guid.NewGuid().ToString("N"));
    private string Cold => Path.Combine(_base, "cold");
    private string Warm => Path.Combine(_base, "warm");
    private readonly BuildWarmState _warm = new();

    public WarmColdEquivalenceTests()
    {
        TestProjects.WriteOneFileProject(Cold, sources: ["A.cpp", "B.cpp"]);
        TestProjects.WriteOneFileProject(Warm, sources: ["A.cpp", "B.cpp"]);
        _warm.WatchedRoot = Warm;
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private static async Task<List<string>> Run(string root, BuildServices services)
    {
        var executor = new FakeExecutor();
        var withExecutor = new BuildServices { Warm = services.Warm, ExecutorFactory = (_, _) => executor };
        await new BuildOrchestrator(withExecutor).BuildAsync(new BuildOrchestratorRequest
        {
            TargetFile = Path.Combine(root, "App.target.cs"),
            Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64, Configuration = BuildConfiguration.Development,
        }, null, null);
        return executor.Ran.OrderBy(s => s).ToList();
    }

    private async Task Edit(Action<string> edit, params FileChange[] warmChanges)
    {
        edit(Cold);
        edit(Warm);
        foreach (var change in warmChanges)
            _warm.Enqueue(change with { Path = change.Path.Replace("{root}", Warm) });
        _warm.ApplyPendingChanges();
        var cold = await Run(Cold, new BuildServices());
        var warm = await Run(Warm, new BuildServices { Warm = _warm });
        warm.Should().Equal(cold);
    }

    [Fact]
    public async Task EveryEditDecidesTheSameWarmAndCold()
    {
        await Edit(_ => { });                                                     // first build
        await Edit(_ => { });                                                     // no-op
        await Edit(r => File.SetLastWriteTimeUtc(Path.Combine(r, "Source/Core/A.cpp"), DateTime.UtcNow.AddMinutes(1)),
            new FileChange("{root}/Source/Core/A.cpp", FileChangeKind.Changed));
        await Edit(r => File.WriteAllText(Path.Combine(r, "Source/Core/C.cpp"), "int C() { return 0; }\n"),
            new FileChange("{root}/Source/Core/C.cpp", FileChangeKind.Created));
        await Edit(r => File.Move(Path.Combine(r, "Source/Core/B.cpp"), Path.Combine(r, "Source/Core/D.cpp")),
            new FileChange("{root}/Source/Core/B.cpp", FileChangeKind.Renamed));
        await Edit(r => File.AppendAllText(Path.Combine(r, "App.target.cs"), "\n// edited\n"),
            new FileChange("{root}/App.target.cs", FileChangeKind.Changed));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Executors.Tests --filter "BuildWarmStateTests|WarmColdEquivalenceTests"`
Expected: compile errors for `WatchedRoot`, `Stamps`.

- [ ] **Step 3: Implement stamps in `ActionGraph`**

`src/Omen.Core/Graph/IFileStamps.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Graph;

/// <summary>A file's last write time (UTC), or null when it does not exist.</summary>
public interface IFileStamps
{
    DateTime? Get(string path);
}
```

In `ActionGraph`, add `public IFileStamps? Stamps { get; set; }` and make `Stamp` consult it first:

```csharp
    private DateTime? Stamp(string path)
    {
        if (Stamps != null)
            return Stamps.Get(path);
        // (existing body unchanged)
    }
```

- [ ] **Step 4: Implement the warm pieces**

Add to `BuildWarmState`:

```csharp
    private readonly Dictionary<string, IncludeScanner> _includeScanners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActionDigestStore> _digestStores = new(StringComparer.OrdinalIgnoreCase);
    private readonly WarmStamps _stamps;

    public BuildWarmState() => _stamps = new WarmStamps(this);

    /// <summary>Only paths under this root are stamp-cached; everything else is stat'ed every time.</summary>
    public string? WatchedRoot { get; set; }

    internal IFileStamps Stamps => _stamps;

    internal IncludeScanner GetIncludeScanner(string cachePath)
    {
        lock (_includeScanners)
        {
            if (!_includeScanners.TryGetValue(cachePath, out var scanner))
                _includeScanners[cachePath] = scanner = IncludeScanner.LoadFrom(cachePath);
            return scanner;
        }
    }

    internal ActionDigestStore GetDigestStore(string path)
    {
        lock (_digestStores)
        {
            if (!_digestStores.TryGetValue(path, out var store))
                _digestStores[path] = store = new ActionDigestStore(path);
            return store;
        }
    }

    private sealed class WarmStamps(BuildWarmState owner) : IFileStamps
    {
        private readonly ConcurrentDictionary<string, DateTime?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public DateTime? Get(string path)
        {
            if (owner.WatchedRoot is not { } root || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Read(path);
            return _cache.GetOrAdd(path, Read);
        }

        public void Evict(string path)
        {
            _cache.TryRemove(path, out _);
            // A deleted/renamed directory takes every cached path under it.
            foreach (var key in _cache.Keys.Where(k => k.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                _cache.TryRemove(key, out _);
        }

        public void Clear() => _cache.Clear();

        private static DateTime? Read(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
    }
```

In `ApplyPendingChanges`, for every change call `_stamps.Evict(Path.GetFullPath(change.Path))`; for a `Changed` change also evict that file's include-scan entry: `foreach (var scanner in _includeScanners.Values) scanner.Forget(change.Path);`. `DropAll()` also clears `_includeScanners`, `_digestStores` (after `Save()` on each) and `_stamps.Clear()`. Add one branch before the source-extension branch: a `Deleted`/`Renamed` whose path ends in `Intermediate` or contains `\Intermediate\` → `DropAll()` (Review Focus 2).

`IncludeScanner.Forget(string path)`: add to `src/Omen.Core/Graph/IncludeScanner.cs` (grep its cache dictionary name first) — remove the entry for that path from its per-file cache. If `IncludeScanner.cs` is in the other session's dirty list at execution time, apply the insertion-only staging rule.

Normalize every enqueued path with `Path.GetFullPath` in `Enqueue` so `{root}/Source/…` (forward slashes) matches `FileInfo` paths.

- [ ] **Step 5: Use them in `BuildOneCombinationAsync`**

```csharp
        var graphBuilder = new ActionGraphBuilder(context, toolchain, digestCalculator)
        {
            IncludeScanner = _services.Warm?.GetIncludeScanner(includeCachePath) ?? IncludeScanner.LoadFrom(includeCachePath)
        };
```

after `graphBuilder.Build(...)`: `graph.Stamps = _services.Warm?.Stamps;`

```csharp
        var digestPath = Path.Combine(context.IntermediateDirectory, ".buildtool", "digests.json");
        var digestStore = _services.Warm?.GetDigestStore(digestPath) ?? new ActionDigestStore(digestPath);
```

Stamps written by this build's own actions: after the executor finishes, evict every completed action's outputs so the next request re-reads them — in the existing `foreach (var action in graph.Actions.Where(a => a.Status is ActionStatus.Completed or ActionStatus.Skipped))` loop add `if (_services.Warm is { } w) foreach (var o in action.Outputs) w.Enqueue(new FileChange(o.Path, FileChangeKind.Changed));` (the watcher reports these too; enqueueing directly removes a race with the watcher's latency).

- [ ] **Step 6: Run the tests and suites**

Run: all three suites.
Expected: new tests PASS; `WarmColdEquivalenceTests.EveryEditDecidesTheSameWarmAndCold` PASS; suites green.

- [ ] **Step 7: Commit**

```bash
git add src/Omen.Core/Graph/IFileStamps.cs src/Omen.Core/Graph/ActionGraph.cs src/Omen.Core/Graph/IncludeScanner.cs src/Omen.Executors/Server/BuildWarmState.cs src/Omen.Executors/Orchestration/BuildOrchestrator.cs tests/Omen.Executors.Tests/BuildWarmStateTests.cs tests/Omen.Executors.Tests/WarmColdEquivalenceTests.cs
git commit -m "Build server warm state: include scanner, digest store and file stamps; warm/cold equivalence

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" -- src/Omen.Core/Graph/IFileStamps.cs src/Omen.Core/Graph/ActionGraph.cs src/Omen.Core/Graph/IncludeScanner.cs src/Omen.Executors/Server/BuildWarmState.cs src/Omen.Executors/Orchestration/BuildOrchestrator.cs tests/Omen.Executors.Tests/BuildWarmStateTests.cs tests/Omen.Executors.Tests/WarmColdEquivalenceTests.cs
```

---

### Task 4: Measure, then (only if needed) cache action graphs

**Files:**
- Modify (conditional): `src/Omen.Core/Graph/ActionGraph.cs` (`ResetForRebuild`), `BuildWarmState.cs`, `BuildOrchestrator.cs`
- Test (conditional): `tests/Omen.Executors.Tests/BuildWarmStateTests.cs`

- [ ] **Step 1: Measure warm graph time on the engine**

Build the CLI (`dotnet build src/Omen.CLI -v q`). Temporary harness, not committed: a test in `tests/Omen.Executors.Tests` marked `[Fact(Skip = "manual")]` that runs `BuildOrchestrator` twice against `F:\engine\Code\Tools\AzTestRunner\NightFoxTestRunner.target.cs` with one `BuildWarmState` and a `FakeExecutor` that runs nothing (returns Completed without writing), capturing the "  Action graph: N.Ns" event of the second run. Remove `Skip`, run it once with `--filter`, record the number in the ledger, restore `Skip` and delete the harness before committing anything.

Rule: warm "Action graph" < 1.0 s → ledger `Task 4: Ruling: graph cache not needed (warm graph N s)` and skip to Task 5. Otherwise continue.

- [ ] **Step 2: Failing test (only if continuing)**

```csharp
    [Fact]
    public async Task SecondBuild_ReusesTheActionGraph()
    {
        await Build();
        _warm.ApplyPendingChanges();
        await Build();
        _warm.GraphBuilds.Should().Be(1);
    }
```

- [ ] **Step 3: Implement (only if continuing)**

`ActionGraph.ResetForRebuild()`: every action `Status = Pending`, `RebuildReason = RootCause = null`, `ActionDigest = null`. `BuildWarmState`: `GraphBuilds` counter, `GetGraph(key)`/`SetGraph(key, graph)` with the closure key; `ApplyPendingChanges` clears graphs wherever it clears closures. `BuildOneCombinationAsync`: reuse `graph` from the warm cache (calling `ResetForRebuild`) instead of `graphBuilder.Build`; the include scanner save and annotation-cache save stay inside the not-cached branch.

A graph must not be shared by two concurrent requests: key the cache per request slot by taking it out (`TryTake`) and putting it back after the build; a second concurrent request for the same key builds its own.

- [ ] **Step 4: Run the suites; commit (only if continuing)**

```bash
git add src/Omen.Core/Graph/ActionGraph.cs src/Omen.Executors/Server/BuildWarmState.cs src/Omen.Executors/Orchestration/BuildOrchestrator.cs tests/Omen.Executors.Tests/BuildWarmStateTests.cs
git commit -m "Build server warm state: reuse action graphs between builds

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" -- src/Omen.Core/Graph/ActionGraph.cs src/Omen.Executors/Server/BuildWarmState.cs src/Omen.Executors/Orchestration/BuildOrchestrator.cs tests/Omen.Executors.Tests/BuildWarmStateTests.cs
```

---

### Task 5: `SharedActionScheduler`

**Files:**
- Create: `src/Omen.Executors/Server/SharedActionScheduler.cs`
- Modify: `src/Omen.Executors/ParallelExecutor.cs` (make the process runner reusable)
- Test: `tests/Omen.Executors.Tests/SharedActionSchedulerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum RequestClass { Interactive, Background }
  public sealed record ScheduleRequest(string Id, RequestClass Class, int Cap);

  public sealed class SharedActionScheduler
  {
      public SharedActionScheduler(int slots, Func<BuildAction, CancellationToken, Task<ActionResult>> run);
      public int Slots { get; }
      // An IExecutor that runs one request's graph on the shared pool; isUpToDate as ParallelExecutor's.
      public IExecutor For(ScheduleRequest request, Func<BuildAction, bool> isUpToDate);
      public IReadOnlyList<(string RequestId, int Running, int Queued)> Snapshot();   // --status
  }

  // ParallelExecutor
  public static Task<ActionResult> RunProcessAsync(BuildAction action, IActionCache? cache, CancellationToken ct);
  ```

- [ ] **Step 1: Write the failing tests**

`tests/Omen.Executors.Tests/SharedActionSchedulerTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using Omen.Core.Graph;
using Omen.Core.Interfaces;
using Omen.Executors.Server;

namespace Omen.Executors.Tests;

public class SharedActionSchedulerTests
{
    /// <summary>A controllable action runner: each action blocks until released, and every start is recorded.</summary>
    private sealed class Runner
    {
        public ConcurrentQueue<string> Started { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource<bool>> Gates { get; } = new();
        public int Running;
        public int MaxRunning;
        public HashSet<string> Failing { get; } = [];

        public async Task<ActionResult> Run(BuildAction action, CancellationToken ct)
        {
            Started.Enqueue(action.Description);
            var now = Interlocked.Increment(ref Running);
            InterlockedMax(ref MaxRunning, now);
            try
            {
                var gate = Gates.GetOrAdd(action.Description, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                await gate.Task.WaitAsync(ct);
                return new ActionResult { Action = action, Success = !Failing.Contains(action.Description), Duration = TimeSpan.Zero };
            }
            finally
            {
                Interlocked.Decrement(ref Running);
            }
        }

        public void Release(string description) =>
            Gates.GetOrAdd(description, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(true);

        public void ReleaseAll()
        {
            foreach (var gate in Gates.Values) gate.TrySetResult(true);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }

    private static BuildAction Action(string name, string? output = null, string commandLine = "cl") => new()
    {
        Id = Guid.NewGuid().ToString("N"), Type = ActionType.Compile, Description = name, CommandLine = commandLine,
        WorkingDirectory = ".", Outputs = [new FileItem { Path = output ?? $"C:/out/{name}.obj" }],
    };

    private static ActionGraph Graph(params BuildAction[] actions)
    {
        var graph = new ActionGraph();
        foreach (var action in actions) graph.AddAction(action);
        return graph;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        condition().Should().BeTrue();
    }

    [Fact]
    public async Task SameGraphTwice_EachActionRunsOnce()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var a = scheduler.For(new ScheduleRequest("A", RequestClass.Interactive, 4), _ => false)
            .ExecuteAsync(Graph(Action("x"), Action("y")));
        var b = scheduler.For(new ScheduleRequest("B", RequestClass.Interactive, 4), _ => false)
            .ExecuteAsync(Graph(Action("x"), Action("y")));
        await Eventually(() => runner.Started.Count == 2);
        runner.ReleaseAll();

        (await a).Success.Should().BeTrue();
        (await b).Success.Should().BeTrue();
        runner.Started.Should().BeEquivalentTo(["x", "y"]);
    }

    [Fact]
    public async Task SameOutputDifferentCommand_RunsBothOneAfterTheOther()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var a = scheduler.For(new ScheduleRequest("A", RequestClass.Interactive, 4), _ => false)
            .ExecuteAsync(Graph(Action("first", "C:/out/same.obj", "cl /O1")));
        await Eventually(() => runner.Started.Count == 1);
        var b = scheduler.For(new ScheduleRequest("B", RequestClass.Interactive, 4), _ => false)
            .ExecuteAsync(Graph(Action("second", "C:/out/same.obj", "cl /O2")));
        await Task.Delay(100);
        runner.Started.Should().Equal("first");

        runner.Release("first");
        await Eventually(() => runner.Started.Count == 2);
        runner.Release("second");
        (await a).Success.Should().BeTrue();
        (await b).Success.Should().BeTrue();
    }

    [Fact]
    public async Task Interactive_GoesBeforeBackground()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(1, runner.Run);
        var blocker = scheduler.For(new ScheduleRequest("0", RequestClass.Interactive, 1), _ => false).ExecuteAsync(Graph(Action("blocker")));
        await Eventually(() => runner.Started.Count == 1);
        var background = scheduler.For(new ScheduleRequest("bg", RequestClass.Background, 1), _ => false).ExecuteAsync(Graph(Action("bg")));
        await Task.Delay(50);
        var interactive = scheduler.For(new ScheduleRequest("ui", RequestClass.Interactive, 1), _ => false).ExecuteAsync(Graph(Action("ui")));
        await Task.Delay(50);

        runner.Release("blocker");
        await Eventually(() => runner.Started.Count == 2);
        runner.Started.Should().Equal("blocker", "ui");
        runner.ReleaseAll();
        await Task.WhenAll(blocker, background, interactive);
    }

    [Fact]
    public async Task Background_KeepsOneSlotAtFourOrMore()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var ui = scheduler.For(new ScheduleRequest("ui", RequestClass.Interactive, 8), _ => false)
            .ExecuteAsync(Graph(Enumerable.Range(0, 8).Select(i => Action($"ui{i}")).ToArray()));
        await Eventually(() => runner.Started.Count == 4);               // ui took the whole pool before bg arrived
        var bg = scheduler.For(new ScheduleRequest("bg", RequestClass.Background, 8), _ => false)
            .ExecuteAsync(Graph(Action("bg0")));
        await Task.Delay(50);

        runner.Release(runner.Started.First());                          // one slot frees; 4 ui actions still queued
        await Eventually(() => runner.Started.Count == 5);
        runner.Started.Last().Should().Be("bg0", "no preemption, but the first free slot goes to the waiting background request");
        runner.ReleaseAll();
        await Eventually(() => runner.Started.Count == 9);
        runner.ReleaseAll();
        await Task.WhenAll(ui, bg);
    }

    [Fact]
    public async Task RequestCap_LimitsThatRequestOnly()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(8, runner.Run);
        var capped = scheduler.For(new ScheduleRequest("c", RequestClass.Interactive, 2), _ => false)
            .ExecuteAsync(Graph(Enumerable.Range(0, 6).Select(i => Action($"c{i}")).ToArray()));
        await Eventually(() => runner.Started.Count == 2);
        await Task.Delay(50);

        runner.Started.Count.Should().Be(2);
        runner.ReleaseAll();
        await Eventually(() => runner.Started.Count == 6);
        runner.ReleaseAll();
        await capped;
    }

    [Fact]
    public async Task SharedAction_TakesTheHigherClass()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(1, runner.Run);
        var blocker = scheduler.For(new ScheduleRequest("0", RequestClass.Interactive, 1), _ => false).ExecuteAsync(Graph(Action("blocker")));
        await Eventually(() => runner.Started.Count == 1);
        var bg = scheduler.For(new ScheduleRequest("bg", RequestClass.Background, 1), _ => false).ExecuteAsync(Graph(Action("shared")));
        var other = scheduler.For(new ScheduleRequest("bg2", RequestClass.Background, 1), _ => false).ExecuteAsync(Graph(Action("other")));
        var ui = scheduler.For(new ScheduleRequest("ui", RequestClass.Interactive, 1), _ => false).ExecuteAsync(Graph(Action("shared")));
        await Task.Delay(50);

        runner.Release("blocker");
        await Eventually(() => runner.Started.Count == 2);
        runner.Started.Should().Equal("blocker", "shared");
        runner.ReleaseAll();
        await Task.WhenAll(blocker, bg, other, ui);
    }

    [Fact]
    public async Task Cancel_KeepsSharedWork()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        using var cancelA = new CancellationTokenSource();
        var a = scheduler.For(new ScheduleRequest("A", RequestClass.Interactive, 4), _ => false)
            .ExecuteAsync(Graph(Action("shared"), Action("onlyA")), ct: cancelA.Token);
        var b = scheduler.For(new ScheduleRequest("B", RequestClass.Interactive, 4), _ => false)
            .ExecuteAsync(Graph(Action("shared")));
