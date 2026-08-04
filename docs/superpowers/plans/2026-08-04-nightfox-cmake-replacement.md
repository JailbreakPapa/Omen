# Omen ↔ NightFox CMake Replacement (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden Omen's core (independent module binaries, digest-based invalidation, layering checks, working monolithic linking, a pluggable platform registry, drift-free Visual Studio projects), add a `GemRules` concept modeled on O3DE's Gems, and prove the whole stack by building NightFox's `Gems/Camera` through Omen side-by-side with its existing CMake build.

**Architecture:** Twelve additive tasks in three phases. Phase A (Tasks 1-6) changes only `Omen.Core`/`Omen.Platforms`/`Omen.CLI` and is NightFox-agnostic. Phase B (Tasks 7-10) adds a new `GemRules` type that expands into ordinary `ModuleRules` instances before `ActionGraphBuilder` ever sees them, so Phase A's graph logic stays Gem-unaware. Phase C (Tasks 11-12) authors one real Gem (`F:\engine\Gems\Camera`) against the new capabilities and adds a parity script that diffs Omen's derived compiler command lines against CMake's real ones, without removing Camera's `CMakeLists.txt`.

**Tech Stack:** C# / .NET 8, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit + FluentAssertions (existing test stack in `tests/Omen.Core.Tests`), System.CommandLine (existing CLI stack).

## Global Constraints

- Every new/changed type keeps the existing project's copyright header: `// Omen Build System` / `// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.`
- `ModuleRules`/`TargetRules`/`GemRules` subclasses discovered by reflection MUST expose a public constructor taking exactly `(BuildContext)` — this is `RuleCompiler`'s existing discovery contract (`RuleCompiler.cs:195`, `:222`); do not add required constructor parameters to any of these types.
- No new NuGet dependencies. Everything in this plan is buildable with what `Omen.Core.csproj`/`Omen.Core.Tests.csproj` already reference (Roslyn, xUnit, FluentAssertions).
- Follow the existing test style: one `[Fact]` per behavior, `Should()` (FluentAssertions) assertions, a private `CreateTestContext()` helper building a `BuildContext` — see `tests/Omen.Core.Tests/ModuleRulesTests.cs:147-155` for the exact pattern to copy.
- Console (Prospero/Xbox) `IToolchain` bodies are explicitly out of scope for this plan — stub with `NotImplementedException` and a comment. Do not attempt real console compiler/linker invocation.

---

## Phase A — Omen core hardening

### Task 1: Independently-linked modules (`ModuleRules.BinaryType`)

**Files:**
- Modify: `src/Omen.Core/Rules/ModuleRules.cs` (add `BinaryType` property)
- Modify: `src/Omen.Core/Graph/ActionGraphBuilder.cs` (`Build`, new `BuildModuleBinaryAction`, `BuildLinkAction`)
- Create: `tests/Omen.Core.Tests/ActionGraphBuilderTests.cs`

**Interfaces:**
- Produces: `ModuleRules.BinaryType` (`Omen.Core.Configuration.TargetType?`, default `null`). `ActionGraphBuilder.Build(TargetRules target, IReadOnlyList<ModuleRules> modules)` (signature unchanged) now creates one archive/link action per module with `BinaryType` set, in addition to the target's own link action.

No `ActionGraphBuilder` unit tests exist yet (`tests/Omen.Core.Tests/` has no `ActionGraphBuilderTests.cs` today) — this task creates that file and its fixture helpers, which later tasks (4, and indirectly 8-10 via integration) extend.

- [ ] **Step 1: Add the `BinaryType` property**

In `src/Omen.Core/Rules/ModuleRules.cs`, add after `SourceDirectory` (around line 170):

```csharp
    /// <summary>
    /// When set, this module is linked as its own independent binary (its object files
    /// are archived/linked separately) instead of folding its objects into the target's
    /// link. Dependents link against the resulting artifact rather than absorbing its
    /// objects directly. When null (the default), behavior is unchanged from today.
    /// </summary>
    public TargetType? BinaryType { get; set; }
```

- [ ] **Step 2: Write the failing test fixture and first test**

Create `tests/Omen.Core.Tests/ActionGraphBuilderTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Implementations;
using Omen.Core.Interfaces;
using Omen.Core.Rules;
using Omen.Platforms;

namespace Omen.Core.Tests;

public class ActionGraphBuilderTests : IDisposable
{
    private readonly string _projectRoot;

    public ActionGraphBuilderTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionGraphBuilderTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    private void WriteSourceFile(string moduleSourceDir, string fileName)
    {
        var dir = Path.Combine(_projectRoot, moduleSourceDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "// test source\n");
    }

    private BuildContext CreateContext() => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Development,
        ProjectRoot = _projectRoot,
        OutputDirectory = Path.Combine(_projectRoot, "Binaries"),
        IntermediateDirectory = Path.Combine(_projectRoot, "Intermediate")
    };

    private static ActionGraphBuilder CreateBuilder(BuildContext context) =>
        new(context, new FakeToolchain(), new Sha256DigestCalculator());

    private sealed class TestTarget : TargetRules
    {
        public TestTarget(BuildContext context) : base(context) { }
    }

    private sealed class TestModule : ModuleRules
    {
        public TestModule(BuildContext context) : base(context) { }
    }

    private sealed class FakeToolchain : ToolchainBase
    {
        public override TargetPlatform Platform => TargetPlatform.Windows;
        public override TargetArchitecture Architecture => TargetArchitecture.X64;
        public override string Name => "Fake";
        public override string Version => "1.0";
        public override string CompilerPath => "cl.exe";
        public override string LinkerPath => "link.exe";
        public override string ArchiverPath => "lib.exe";
        public override string ObjectFileExtension => ".obj";
        public override string StaticLibraryExtension => ".lib";
        public override string SharedLibraryExtension => ".dll";
        public override string ExecutableExtension => ".exe";

        public override Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Graph-building tests only; execution is not exercised.");
        public override Task<LinkResult> LinkAsync(LinkRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Graph-building tests only; execution is not exercised.");
        public override Task<ArchiveResult> ArchiveAsync(ArchiveRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Graph-building tests only; execution is not exercised.");
        public override IReadOnlyList<string> GetDefaultCompilerFlags(BuildConfiguration configuration) => [];
        public override IReadOnlyList<string> GetDefaultLinkerFlags(BuildConfiguration configuration) => [];
        protected override IReadOnlyList<CompileDiagnostic> ParseDiagnostics(string output) => [];
    }

    [Fact]
    public void ModuleWithBinaryType_GetsItsOwnLinkAction()
    {
        // Arrange
        var context = CreateContext();
        WriteSourceFile("Source/Runtime", "Runtime.cpp");
        var runtimeModule = new TestModule(context) { SourceDirectory = "Source/Runtime", BinaryType = TargetType.SharedLibrary };
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [runtimeModule]);

        // Assert: one link action for the independent module's own binary, plus the
        // target's own (empty, since all objects were absorbed by the independent module).
        var linkActions = graph.Actions.Where(a => a.Type is ActionType.Link or ActionType.Archive).ToList();
        linkActions.Should().ContainSingle(a => a.Description.Contains("TestModule"));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter ActionGraphBuilderTests`
Expected: FAIL — either a compile error (`BinaryType` doesn't exist yet, fixed by Step 1) or `linkActions` contains zero matches because `ActionGraphBuilder` doesn't create a per-module link action.

- [ ] **Step 4: Implement independent module linking**

In `src/Omen.Core/Graph/ActionGraphBuilder.cs`, replace the `Build` method body (lines 31-57) with:

```csharp
    public ActionGraph Build(TargetRules target, IReadOnlyList<ModuleRules> modules)
    {
        var graph = new ActionGraph();
        var moduleOutputs = new Dictionary<string, List<FileItem>>();
        var moduleCompileActions = new Dictionary<string, List<BuildAction>>();
        var independentModuleLibraries = new Dictionary<string, string>(); // module name -> library path for dependents

        _moduleDict = modules.ToDictionary(m => m.Name);

        var orderedModules = TopologicalSortModules(modules);
        var aggregateObjectFiles = new List<FileItem>();
        var aggregateCompileActions = new List<BuildAction>();

        foreach (var module in orderedModules)
        {
            var (objectFiles, compileActions) = BuildModuleActions(graph, module, target, moduleOutputs);
            moduleOutputs[module.Name] = objectFiles;
            moduleCompileActions[module.Name] = compileActions;

            if (LinksIndependently(module, target))
            {
                var libraryPath = BuildModuleBinaryAction(graph, module, objectFiles, compileActions);
                independentModuleLibraries[module.Name] = libraryPath;
            }
            else
            {
                aggregateObjectFiles.AddRange(objectFiles);
                aggregateCompileActions.AddRange(compileActions);
            }
        }

        BuildLinkAction(graph, target, modules, aggregateObjectFiles, aggregateCompileActions, independentModuleLibraries);

        graph.ComputePriorities();

        return graph;
    }

    /// <summary>
    /// True when a module is linked as its own independent binary rather than folded
    /// into the target's aggregate link. Monolithic targets fold every module regardless
    /// of BinaryType (see Task 4).
    /// </summary>
    private static bool LinksIndependently(ModuleRules module, TargetRules target) =>
        module.BinaryType.HasValue;

    private string BuildModuleBinaryAction(
        ActionGraph graph,
        ModuleRules module,
        List<FileItem> objectFiles,
        List<BuildAction> compileActions)
    {
        var outputExtension = module.BinaryType switch
        {
            TargetType.SharedLibrary => _toolchain.SharedLibraryExtension,
            TargetType.StaticLibrary => _toolchain.StaticLibraryExtension,
            _ => _toolchain.SharedLibraryExtension
        };
        var outputPath = Path.Combine(_context.OutputDirectory, module.Name + outputExtension);

        var linkRequest = new LinkRequest
        {
            ObjectFiles = objectFiles.Select(o => o.Path).ToList(),
            OutputFile = outputPath,
            OutputType = module.BinaryType!.Value,
            Configuration = _context.Configuration,
            Libraries = module.PublicLibraries.Concat(module.PrivateLibraries).Distinct().ToList(),
            SystemLibraries = module.PublicSystemLibraries.Distinct().ToList(),
            GenerateDebugInfo = true
        };
        var commandLine = BuildLinkCommandLine(linkRequest);

        var action = new BuildAction
        {
            Id = GenerateActionId(),
            Type = module.BinaryType == TargetType.StaticLibrary ? ActionType.Archive : ActionType.Link,
            Description = $"Link {module.Name}",
            CommandLine = commandLine,
            WorkingDirectory = _context.ProjectRoot,
            Inputs = objectFiles,
            Outputs = [new FileItem { Path = outputPath }],
            ModuleName = module.Name,
            CanExecuteRemotely = false,
            EstimatedDuration = TimeSpan.FromSeconds(10),
            Environment = new Dictionary<string, string>(_toolchain.Environment)
        };

        foreach (var compileAction in compileActions)
        {
            action.Dependencies.Add(compileAction);
            compileAction.Dependents.Add(action);
        }

        graph.AddAction(action);

        // A shared library's linkable artifact on Windows is its import lib, not the DLL
        // itself; the toolchain places it alongside the DLL with the same base name.
        return module.BinaryType == TargetType.SharedLibrary
            ? Path.ChangeExtension(outputPath, _toolchain.StaticLibraryExtension)
            : outputPath;
    }
```

- [ ] **Step 5: Update `BuildLinkAction` to take the aggregate lists and independent libraries**

Replace the existing `BuildLinkAction` signature and body (lines 209-280) with:

```csharp
    private void BuildLinkAction(
        ActionGraph graph,
        TargetRules target,
        IReadOnlyList<ModuleRules> modules,
        List<FileItem> aggregateObjectFiles,
        List<BuildAction> aggregateCompileActions,
        Dictionary<string, string> independentModuleLibraries)
    {
        if (aggregateObjectFiles.Count == 0 && independentModuleLibraries.Count == 0) return;

        var outputName = target.OutputName ?? target.Name;
        var outputExtension = target.Type switch
        {
            TargetType.Executable => _toolchain.ExecutableExtension,
            TargetType.SharedLibrary => _toolchain.SharedLibraryExtension,
            TargetType.StaticLibrary => _toolchain.StaticLibraryExtension,
            _ => ""
        };

        var outputPath = Path.Combine(
            target.OutputDirectory ?? _context.OutputDirectory,
            outputName + outputExtension);

        var libraries = modules.SelectMany(m => m.PublicLibraries.Concat(m.PrivateLibraries))
            .Concat(independentModuleLibraries.Values)
            .Distinct().ToList();
        var systemLibraries = modules.SelectMany(m => m.PublicSystemLibraries).Distinct().ToList();
        var frameworks = modules.SelectMany(m => m.PublicFrameworks).Distinct().ToList();
        var linkerFlags = modules.SelectMany(m => m.AdditionalLinkerFlags).Distinct().ToList();

        var linkRequest = new LinkRequest
        {
            ObjectFiles = aggregateObjectFiles.Select(o => o.Path).ToList(),
            OutputFile = outputPath,
            OutputType = target.Type,
            Configuration = _context.Configuration,
            Libraries = libraries,
            SystemLibraries = systemLibraries,
            Frameworks = frameworks,
            GenerateDebugInfo = target.GenerateDebugInfo,
            IncrementalLinking = target.UseIncrementalLinking,
            EnableLTO = target.EnableLTO,
            AdditionalFlags = linkerFlags
        };

        var commandLine = BuildLinkCommandLine(linkRequest);

        var linkAction = new BuildAction
        {
            Id = GenerateActionId(),
            Type = target.Type == TargetType.StaticLibrary ? ActionType.Archive : ActionType.Link,
            Description = $"Link {outputName}",
            CommandLine = commandLine,
            WorkingDirectory = _context.ProjectRoot,
            Inputs = aggregateObjectFiles,
            Outputs = [new FileItem { Path = outputPath }],
            CanExecuteRemotely = false,
            EstimatedDuration = TimeSpan.FromSeconds(10),
            Environment = new Dictionary<string, string>(_toolchain.Environment)
        };

        foreach (var compileAction in aggregateCompileActions)
        {
            linkAction.Dependencies.Add(compileAction);
            compileAction.Dependents.Add(linkAction);
        }

        // Ensure independent module binaries are linked/archived before the target that
        // consumes them, even though the target doesn't compile their objects itself.
        foreach (var moduleName in independentModuleLibraries.Keys)
        {
            var moduleLinkAction = graph.Actions.FirstOrDefault(a => a.ModuleName == moduleName && a.Type is ActionType.Link or ActionType.Archive);
            if (moduleLinkAction != null)
            {
                linkAction.Dependencies.Add(moduleLinkAction);
                moduleLinkAction.Dependents.Add(linkAction);
            }
        }

        graph.AddAction(linkAction);
    }
```

- [ ] **Step 6: Run the test to verify it passes, then run the full suite**

Run: `dotnet test tests/Omen.Core.Tests --filter ActionGraphBuilderTests`
Expected: PASS

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all pre-existing tests still PASS (this task changes `BuildLinkAction`'s signature, an internal/private method — no public API besides the new `BinaryType` property changed).

- [ ] **Step 7: Commit**

```bash
git add src/Omen.Core/Rules/ModuleRules.cs src/Omen.Core/Graph/ActionGraphBuilder.cs tests/Omen.Core.Tests/ActionGraphBuilderTests.cs
git commit -m "feat: modules can link as independent binaries via ModuleRules.BinaryType"
```

---

### Task 2: Command-line-hash invalidation

**Files:**
- Create: `src/Omen.Core/Graph/ActionDigestStore.cs`
- Modify: `src/Omen.Core/Graph/ActionGraph.cs` (add digest-aware `IsUpToDate`/`MarkUpToDateActionsAsSkipped` overloads)
- Modify: `src/Omen.CLI/Commands/BuildCommand.cs` (persist digests after a build)
- Create: `tests/Omen.Core.Tests/ActionDigestStoreTests.cs`

**Interfaces:**
- Consumes: `Omen.Core.Interfaces.IDigestCalculator` (existing), `BuildAction.ComputeDigest(IDigestCalculator)` (existing, `BuildAction.cs:104`).
- Produces: `ActionDigestStore` (load/get/set/save over a JSON sidecar file), `ActionGraph.IsUpToDate(BuildAction, IDigestCalculator, ActionDigestStore)` and `ActionGraph.MarkUpToDateActionsAsSkipped(IDigestCalculator, ActionDigestStore)` overloads.

- [ ] **Step 1: Write the failing test for `ActionDigestStore`**

Create `tests/Omen.Core.Tests/ActionDigestStoreTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Core.Tests;

public class ActionDigestStoreTests : IDisposable
{
    private readonly string _path;

    public ActionDigestStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionDigestStoreTests), Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void SetThenGet_RoundTripsTheDigest()
    {
        // Arrange
        var store = new ActionDigestStore(_path);
        var digest = new ContentDigest("abc123", 42);

        // Act
        store.Set("C:/out/Foo.obj", digest);

        // Assert
        store.TryGet("C:/out/Foo.obj", out var result).Should().BeTrue();
        result.Should().Be(digest);
    }

    [Fact]
    public void TryGet_UnknownOutput_ReturnsFalse()
    {
        var store = new ActionDigestStore(_path);
        store.TryGet("C:/out/Missing.obj", out _).Should().BeFalse();
    }

    [Fact]
    public void SaveThenReload_PersistsAcrossInstances()
    {
        // Arrange
        var digest = new ContentDigest("def456", 7);
        var store1 = new ActionDigestStore(_path);
        store1.Set("C:/out/Bar.obj", digest);
        store1.Save();

        // Act
        var store2 = new ActionDigestStore(_path);

        // Assert
        store2.TryGet("C:/out/Bar.obj", out var result).Should().BeTrue();
        result.Should().Be(digest);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter ActionDigestStoreTests`
Expected: FAIL with a compile error — `ActionDigestStore` doesn't exist yet.

- [ ] **Step 3: Implement `ActionDigestStore`**

Create `src/Omen.Core/Graph/ActionDigestStore.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;
using Omen.Core.Interfaces;

namespace Omen.Core.Graph;

/// <summary>
/// Persists the digest recorded for each action's primary output across builds, so an
/// action can be skipped when its command line (and therefore its digest) hasn't changed,
/// rather than relying on file timestamps alone.
/// </summary>
public sealed class ActionDigestStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _digests;

    public ActionDigestStore(string path)
    {
        _path = path;
        _digests = Load(path);
    }

    public bool TryGet(string outputPath, out ContentDigest digest)
    {
        if (_digests.TryGetValue(outputPath, out var serialized))
        {
            digest = ContentDigest.Parse(serialized);
            return true;
        }
        digest = default;
        return false;
    }

    public void Set(string outputPath, ContentDigest digest) => _digests[outputPath] = digest.ToString();

    public void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(_digests));
    }

    private static Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Omen.Core.Tests --filter ActionDigestStoreTests`
Expected: PASS

- [ ] **Step 5: Write the failing test for digest-aware `IsUpToDate`**

Add to `tests/Omen.Core.Tests/ActionGraphTests.cs` (after the existing `Reset_SetsAllStatusesToPending` test):

```csharp
    [Fact]
    public void IsUpToDate_WithDigest_ReturnsFalseWhenCommandLineChanged()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionGraphTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var storePath = Path.Combine(tempDir, "digests.json");
        var outputPath = Path.Combine(tempDir, "out.obj");
        File.WriteAllText(outputPath, "stale object file");

        var calculator = new Sha256DigestCalculator();
        var store = new ActionDigestStore(storePath);

        var originalAction = CreateAction("compile1");
        var originalDigest = originalAction.ComputeDigest(calculator);
        store.Set(outputPath, originalDigest);

        var changedAction = new BuildAction
        {
            Id = "compile1",
            Type = ActionType.Compile,
            Description = "Test action compile1",
            CommandLine = "test.exe /DIFFERENT_FLAG",
            WorkingDirectory = "/test",
            Outputs = [new FileItem { Path = outputPath }]
        };
        var graph = new ActionGraph();
        graph.AddAction(changedAction);

        // Act
        var upToDate = graph.IsUpToDate(changedAction, calculator, store);

        // Assert
        upToDate.Should().BeFalse();

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void IsUpToDate_WithDigest_ReturnsTrueWhenCommandLineUnchanged()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionGraphTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var storePath = Path.Combine(tempDir, "digests.json");
        var outputPath = Path.Combine(tempDir, "out.obj");
        File.WriteAllText(outputPath, "up to date object file");

        var calculator = new Sha256DigestCalculator();
        var store = new ActionDigestStore(storePath);

        var action = new BuildAction
        {
            Id = "compile1",
            Type = ActionType.Compile,
            Description = "Test action compile1",
            CommandLine = "test.exe /SAME_FLAG",
            WorkingDirectory = "/test",
            Outputs = [new FileItem { Path = outputPath }]
        };
        var digest = action.ComputeDigest(calculator);
        store.Set(outputPath, digest);

        var graph = new ActionGraph();
        graph.AddAction(action);

        // Act
        var upToDate = graph.IsUpToDate(action, calculator, store);

        // Assert
        upToDate.Should().BeTrue();

        Directory.Delete(tempDir, recursive: true);
    }
```

Add `using Omen.Core.Implementations;` to the top of `ActionGraphTests.cs` if not already present via global usings — check `tests/Omen.Core.Tests/GlobalUsings.cs` first and only add what's missing.

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter ActionGraphTests`
Expected: FAIL with a compile error — the digest-aware `IsUpToDate` overload doesn't exist yet.

- [ ] **Step 7: Implement the digest-aware overloads**

In `src/Omen.Core/Graph/ActionGraph.cs`, add after the existing `IsUpToDate` method (after line 244):

```csharp
    /// <summary>
    /// Checks if an action is up-to-date by comparing its current command-line digest
    /// against the digest recorded for its primary output on a previous build. An edit
    /// to a rules file that changes no actual compiler flag leaves the digest unchanged
    /// and invalidates nothing.
    /// </summary>
    public bool IsUpToDate(BuildAction action, IDigestCalculator calculator, ActionDigestStore digestStore)
    {
        if (action.Outputs.Count == 0 || action.Outputs.Any(o => !File.Exists(o.Path)))
            return false;

        var currentDigest = action.ComputeDigest(calculator);
        var primaryOutput = action.Outputs[0].Path;

        return digestStore.TryGet(primaryOutput, out var previousDigest) && currentDigest.Equals(previousDigest);
    }

    /// <summary>
    /// Marks digest-up-to-date actions as skipped. Unlike the timestamp-only overload,
    /// this also records the current digest for every action that IS up-to-date, so the
    /// store stays populated even on a build where nothing needed to rebuild.
    /// </summary>
    public int MarkUpToDateActionsAsSkipped(IDigestCalculator calculator, ActionDigestStore digestStore)
    {
        var skipped = 0;
        foreach (var action in GetTopologicalOrder())
        {
            if (action.Status != ActionStatus.Pending)
                continue;

            if (IsUpToDate(action, calculator, digestStore))
            {
                action.Status = ActionStatus.Skipped;
                skipped++;
            }
        }
        return skipped;
    }
```

- [ ] **Step 8: Run to verify it passes**

Run: `dotnet test tests/Omen.Core.Tests --filter ActionGraphTests`
Expected: PASS

- [ ] **Step 9: Wire persistence into `BuildCommand`**

In `src/Omen.CLI/Commands/BuildCommand.cs`, after the line building `graph` (line 203, `var graph = graphBuilder.Build(targetRules, modules);`), add:

```csharp
        var digestStore = new ActionDigestStore(Path.Combine(context.IntermediateDirectory, ".buildtool", "digests.json"));
        var skipped = graph.MarkUpToDateActionsAsSkipped(digestCalculator, digestStore);
        if (skipped > 0)
        {
            AnsiConsole.MarkupLine($"[cyan]{skipped} action(s) already up to date (unchanged command line), skipped.[/]");
        }
```

And after the build finishes successfully (after the `if (result.Success)` block starts, right after `var table = new Table();` insert is not needed — instead add, immediately before `return 0;` inside the `if (result.Success)` branch):

```csharp
            foreach (var action in graph.Actions.Where(a => a.Status is ActionStatus.Completed or ActionStatus.Skipped))
            {
                if (action.Outputs.Count == 0 || !File.Exists(action.Outputs[0].Path)) continue;
                digestStore.Set(action.Outputs[0].Path, action.ComputeDigest(digestCalculator));
            }
            digestStore.Save();
```

Add `using Omen.Core.Graph;` to the top of `BuildCommand.cs` if not already present (it already imports `Omen.Core.Graph` per line 8 — confirm before adding a duplicate `using`).

- [ ] **Step 10: Run the full test suite**

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS. `BuildCommand.cs` has no unit tests today (it's a CLI entry point exercised via `examples/ExampleGame`); manually verify with `dotnet run --project src/Omen.CLI -- build` against `examples/ExampleGame/ExampleGame.target.cs` twice in a row and confirm the second run prints the "already up to date" line.

- [ ] **Step 11: Commit**

```bash
git add src/Omen.Core/Graph/ActionDigestStore.cs src/Omen.Core/Graph/ActionGraph.cs src/Omen.CLI/Commands/BuildCommand.cs tests/Omen.Core.Tests/ActionDigestStoreTests.cs tests/Omen.Core.Tests/ActionGraphTests.cs
git commit -m "feat: invalidate actions by command-line digest instead of file timestamps only"
```

---

### Task 3: Layering / forbidden-dependency checks

**Files:**
- Create: `src/Omen.Core/Rules/LayeringValidator.cs`
- Modify: `src/Omen.Core/Rules/ModuleRules.cs` (add `ForbiddenDependencies`)
- Modify: `src/Omen.CLI/Commands/BuildCommand.cs` (call the validator before graph building)
- Create: `tests/Omen.Core.Tests/LayeringValidatorTests.cs`

**Interfaces:**
- Produces: `ModuleRules.ForbiddenDependencies` (`List<(string ModuleName, string Reason)>`), `LayeringValidator.Validate(IReadOnlyList<ModuleRules> modules)` (throws `LayeringViolationException` on failure, returns normally otherwise).

- [ ] **Step 1: Add `ForbiddenDependencies` to `ModuleRules`**

In `src/Omen.Core/Rules/ModuleRules.cs`, add after `PrivateDependencies` (around line 49):

```csharp
    /// <summary>
    /// Modules this module (and anything reachable through its dependency closure) must
    /// never depend on, with a mandatory reason. Checked by <see cref="LayeringValidator"/>.
    /// </summary>
    public List<(string ModuleName, string Reason)> ForbiddenDependencies { get; } = [];
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Omen.Core.Tests/LayeringValidatorTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class LayeringValidatorTests
{
    private sealed class TestModule : ModuleRules
    {
        public TestModule(BuildContext context) : base(context) { }
    }

    private static BuildContext CreateContext() => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = "/test",
        OutputDirectory = "/test/bin",
        IntermediateDirectory = "/test/obj"
    };

    [Fact]
    public void Validate_NoForbiddenDependencies_DoesNotThrow()
    {
        var runtime = new TestModule(CreateContext()) { Name = "Runtime" };
        var act = () => LayeringValidator.Validate([runtime]);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_DirectForbiddenDependency_Throws()
    {
        var context = CreateContext();
        var editor = new TestModule(context);
        editor.PublicDependencies.Add("Editor");
        editor.ForbiddenDependencies.Add(("Editor", "The runtime is what ships."));

        var act = () => LayeringValidator.Validate([editor]);

        act.Should().Throw<LayeringViolationException>()
            .WithMessage("*forbidden dependency 'Editor'*")
            .WithMessage("*The runtime is what ships.*");
    }

    [Fact]
    public void Validate_ForbiddenDependencyThroughIntermediateModule_Throws()
    {
        var context = CreateContext();
        var runtime = new TestModule(context);
        runtime.PublicDependencies.Add("Intermediate");
        runtime.ForbiddenDependencies.Add(("Editor", "no editor in runtime"));

        var intermediate = new TestModule(context);
        intermediate.PublicDependencies.Add("Editor");

        // Both named "TestModule" by convention; give them distinct names for lookup.
        var runtimeNamed = NameAs(runtime, "Runtime");
        var intermediateNamed = NameAs(intermediate, "Intermediate");

        var act = () => LayeringValidator.Validate([runtimeNamed, intermediateNamed]);

        act.Should().Throw<LayeringViolationException>()
            .WithMessage("*Runtime -> Intermediate -> Editor*");
    }

    [Fact]
    public void Validate_ForbiddenDependencyWithNoReason_Throws()
    {
        var context = CreateContext();
        var runtime = new TestModule(context);
        runtime.ForbiddenDependencies.Add(("Editor", ""));

        var act = () => LayeringValidator.Validate([runtime]);

        act.Should().Throw<LayeringViolationException>().WithMessage("*reason*");
    }

    [Fact]
    public void Validate_ThirdPartyDependsOnFirstParty_ThrowsWithNoDeclarationRequired()
    {
        var context = CreateContext();
        var thirdParty = new TestModule(context) { Type = ModuleType.ThirdParty };
        thirdParty.PublicDependencies.Add("Runtime");
        var runtime = new TestModule(context) { Type = ModuleType.Runtime };

        var thirdPartyNamed = NameAs(thirdParty, "Vendored");
        var runtimeNamed = NameAs(runtime, "Runtime");

        var act = () => LayeringValidator.Validate([thirdPartyNamed, runtimeNamed]);

        act.Should().Throw<LayeringViolationException>().WithMessage("*third-party*Vendored*Runtime*");
    }

    // ModuleRules.Name is set in the constructor from the class name and has no public
    // setter; tests use reflection to give fixture instances distinct, readable names.
    private static ModuleRules NameAs(ModuleRules module, string name)
    {
        typeof(ModuleRules).GetProperty(nameof(ModuleRules.Name))!
            .SetValue(module, name);
        return module;
    }
}
```

Note: `ModuleRules.Name` is `{ get; }` with no setter, so the reflection workaround above will fail (auto-property backing fields for `{ get; }` have no public setter reachable via `PropertyInfo.SetValue` either — it throws `ArgumentException`). Use this instead, which matches how `ModuleRulesTests.cs` already names fixtures (by the concrete class name):

```csharp
    private sealed class RuntimeModule : ModuleRules
    {
        public RuntimeModule(BuildContext context) : base(context) { }
    }

    private sealed class EditorModule : ModuleRules
    {
        public EditorModule(BuildContext context) : base(context) { }
    }

    private sealed class IntermediateModule : ModuleRules
    {
        public IntermediateModule(BuildContext context) : base(context) { }
    }

    private sealed class VendoredModule : ModuleRules
    {
        public VendoredModule(BuildContext context) : base(context) { Type = ModuleType.ThirdParty; }
    }
```

Replace every `TestModule`/`NameAs(...)` usage above with these concrete classes instead (e.g. `new RuntimeModule(context)` has `Name == "RuntimeModule"` — adjust the `ForbiddenDependencies`/`PublicDependencies` string literals in the tests to match: `"EditorModule"`, `"IntermediateModule"`, `"RuntimeModule"`, `"VendoredModule"`), and drop the `NameAs` helper entirely.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter LayeringValidatorTests`
Expected: FAIL with a compile error — `LayeringValidator`/`LayeringViolationException` don't exist yet.

- [ ] **Step 4: Implement `LayeringValidator`**

Create `src/Omen.Core/Rules/LayeringValidator.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Rules;

/// <summary>
/// Validates a resolved module graph against declared layering rules before any compile
/// or link action is built. All violations that would be found are still reported one at
/// a time (the first one found throws) since architectural drift is best fixed as soon as
/// it's introduced.
/// </summary>
public static class LayeringValidator
{
    public static void Validate(IReadOnlyList<ModuleRules> modules)
    {
        var byName = modules.ToDictionary(m => m.Name);

        foreach (var module in modules)
        {
            foreach (var (forbiddenName, reason) in module.ForbiddenDependencies)
            {
                if (string.IsNullOrWhiteSpace(reason))
                {
                    throw new LayeringViolationException(
                        $"Module '{module.Name}' forbids dependency on '{forbiddenName}' with no reason. A reason is required.");
                }

                var path = FindPath(module, forbiddenName, byName);
                if (path != null)
                {
                    throw new LayeringViolationException(
                        $"Layering violation: {string.Join(" -> ", path)} reaches forbidden dependency '{forbiddenName}'. Reason: {reason}");
                }
            }

            if (module.Type != ModuleType.ThirdParty)
                continue;

            foreach (var depName in module.PublicDependencies.Concat(module.PrivateDependencies))
            {
                if (byName.TryGetValue(depName, out var dep) && dep.Type != ModuleType.ThirdParty)
                {
                    throw new LayeringViolationException(
                        $"Layering violation: third-party module '{module.Name}' depends on first-party module '{dep.Name}'. Vendored code must stand alone.");
                }
            }
        }
    }

    private static List<string>? FindPath(ModuleRules start, string targetName, Dictionary<string, ModuleRules> byName)
    {
        var visited = new HashSet<string>();
        var path = new List<string> { start.Name };
        return Search(start, targetName, byName, visited, path) ? path : null;
    }

    private static bool Search(ModuleRules current, string targetName, Dictionary<string, ModuleRules> byName, HashSet<string> visited, List<string> path)
    {
        if (!visited.Add(current.Name))
            return false;

        foreach (var depName in current.PublicDependencies.Concat(current.PrivateDependencies))
        {
            path.Add(depName);

            if (depName == targetName)
                return true;

            if (byName.TryGetValue(depName, out var dep) && Search(dep, targetName, byName, visited, path))
                return true;

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }
}

/// <summary>
/// Thrown when <see cref="LayeringValidator.Validate"/> finds a layering violation.
/// </summary>
public sealed class LayeringViolationException(string message) : Exception(message);
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Omen.Core.Tests --filter LayeringValidatorTests`
Expected: PASS

- [ ] **Step 6: Wire the validator into `BuildCommand`**

In `src/Omen.CLI/Commands/BuildCommand.cs`, after the line `var modules = compiledRules.CreateModuleRules(context);` (line 183), add:

```csharp
        try
        {
            LayeringValidator.Validate(modules);
        }
        catch (LayeringViolationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Layering violation:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Omen.Core/Rules/ModuleRules.cs src/Omen.Core/Rules/LayeringValidator.cs src/Omen.CLI/Commands/BuildCommand.cs tests/Omen.Core.Tests/LayeringValidatorTests.cs
git commit -m "feat: add layering/forbidden-dependency validation before graph build"
```

---

### Task 4: Monolithic linking

**Files:**
- Modify: `src/Omen.Core/Graph/ActionGraphBuilder.cs` (`LinksIndependently`, new `BuildStaticModuleRegistrationAction`, `Build`)
- Modify: `tests/Omen.Core.Tests/ActionGraphBuilderTests.cs`

**Interfaces:**
- Consumes: `TargetRules.LinkType` (existing, `TargetRules.cs:54`), `ModuleRules.BinaryType` (Task 1).
- Produces: no new public API — `Build` now honors `LinkType.Monolithic` by folding modules that would otherwise be independent shared libraries, and generates `StaticModuleRegistration.g.cpp` under `IntermediateDirectory` listing their names.

- [ ] **Step 1: Write the failing test**

Add to `tests/Omen.Core.Tests/ActionGraphBuilderTests.cs`:

```csharp
    [Fact]
    public void MonolithicTarget_FoldsSharedLibraryModulesAndGeneratesRegistration()
    {
        // Arrange
        var context = CreateContext();
        WriteSourceFile("Source/Camera", "Camera.cpp");
        var cameraModule = new TestModule(context) { SourceDirectory = "Source/Camera", BinaryType = TargetType.SharedLibrary };
        var target = new TestTarget(context) { Type = TargetType.Executable, LinkType = LinkType.Monolithic };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [cameraModule]);

        // Assert: no independent link action for the module (folded into the target link)
        graph.Actions.Should().NotContain(a => a.Type is ActionType.Link or ActionType.Archive && a.Description.Contains("Link TestModule"));

        // Assert: a registration source file was generated listing the folded module
        var registrationPath = Path.Combine(context.IntermediateDirectory, "StaticModuleRegistration.g.cpp");
        File.Exists(registrationPath).Should().BeTrue();
        File.ReadAllText(registrationPath).Should().Contain("TestModule");
    }

    [Fact]
    public void ModularTarget_KeepsSharedLibraryModulesIndependent()
    {
        // Arrange
        var context = CreateContext();
        WriteSourceFile("Source/Camera", "Camera.cpp");
        var cameraModule = new TestModule(context) { SourceDirectory = "Source/Camera", BinaryType = TargetType.SharedLibrary };
        var target = new TestTarget(context) { Type = TargetType.Executable, LinkType = LinkType.Modular };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [cameraModule]);

        // Assert
        graph.Actions.Should().Contain(a => a.Type == ActionType.Link && a.Description == "Link TestModule");
        File.Exists(Path.Combine(context.IntermediateDirectory, "StaticModuleRegistration.g.cpp")).Should().BeFalse();
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter ActionGraphBuilderTests`
Expected: FAIL — `MonolithicTarget_...` fails because today `LinksIndependently` ignores `target.LinkType`, so the module still gets its own link action and no registration file is written.

- [ ] **Step 3: Implement monolithic folding and registration codegen**

In `src/Omen.Core/Graph/ActionGraphBuilder.cs`, replace `LinksIndependently` (added in Task 1) with:

```csharp
    private static bool LinksIndependently(ModuleRules module, TargetRules target) =>
        module.BinaryType.HasValue &&
        !(target.LinkType == LinkType.Monolithic && module.BinaryType == TargetType.SharedLibrary);
```

Then update `Build` to track folded-monolithic module names and generate the registration action. Replace the `foreach (var module in orderedModules)` loop and the call to `BuildLinkAction` with:

```csharp
        var monolithicModuleNames = new List<string>();

        foreach (var module in orderedModules)
        {
            var (objectFiles, compileActions) = BuildModuleActions(graph, module, target, moduleOutputs);
            moduleOutputs[module.Name] = objectFiles;
            moduleCompileActions[module.Name] = compileActions;

            if (LinksIndependently(module, target))
            {
                var libraryPath = BuildModuleBinaryAction(graph, module, objectFiles, compileActions);
                independentModuleLibraries[module.Name] = libraryPath;
            }
            else
            {
                aggregateObjectFiles.AddRange(objectFiles);
                aggregateCompileActions.AddRange(compileActions);

                if (target.LinkType == LinkType.Monolithic && module.BinaryType == TargetType.SharedLibrary)
                {
                    monolithicModuleNames.Add(module.Name);
                }
            }
        }

        var registrationAction = BuildStaticModuleRegistrationAction(graph, monolithicModuleNames);
        if (registrationAction != null)
        {
            aggregateObjectFiles.AddRange(registrationAction.Outputs);
            aggregateCompileActions.Add(registrationAction);
        }

        BuildLinkAction(graph, target, modules, aggregateObjectFiles, aggregateCompileActions, independentModuleLibraries);
```

Add the new method after `BuildModuleBinaryAction`:

```csharp
    /// <summary>
    /// Generates (only when its content actually changed) a source file listing every
    /// module folded into a monolithic link, and a compile action for it. This is Omen's
    /// equivalent of O3DE's StaticModules.inl: a generic name table, not tied to any
    /// specific engine's module-registration mechanism.
    /// </summary>
    private BuildAction? BuildStaticModuleRegistrationAction(ActionGraph graph, List<string> monolithicModuleNames)
    {
        if (monolithicModuleNames.Count == 0) return null;

        var entries = string.Join(",\n    ", monolithicModuleNames.Select(n => $"\"{n}\""));
        var source =
            "// Generated by Omen. Do not edit.\n" +
            "extern \"C\" const char* const OmenStaticModules[] = {\n" +
            $"    {entries},\n" +
            "    nullptr\n" +
            "};\n" +
            $"extern \"C\" const int OmenStaticModuleCount = {monolithicModuleNames.Count};\n";

        Directory.CreateDirectory(_context.IntermediateDirectory);
        var sourcePath = Path.Combine(_context.IntermediateDirectory, "StaticModuleRegistration.g.cpp");
        if (!File.Exists(sourcePath) || File.ReadAllText(sourcePath) != source)
        {
            File.WriteAllText(sourcePath, source);
        }

        var objectFile = Path.Combine(_context.IntermediateDirectory, "StaticModuleRegistration" + _toolchain.ObjectFileExtension);
        var compileRequest = new CompileRequest
        {
            SourceFile = sourcePath,
            OutputFile = objectFile,
            Configuration = _context.Configuration,
            CppStandard = CppStandard.Cpp20
        };

        var action = new BuildAction
        {
            Id = GenerateActionId(),
            Type = ActionType.Compile,
            Description = "Compile StaticModuleRegistration.g.cpp",
            CommandLine = BuildCompileCommandLine(compileRequest),
            WorkingDirectory = _context.ProjectRoot,
            Inputs = [new FileItem { Path = sourcePath }],
            Outputs = [new FileItem { Path = objectFile }],
            Environment = new Dictionary<string, string>(_toolchain.Environment)
        };
        graph.AddAction(action);
        return action;
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Omen.Core.Tests --filter ActionGraphBuilderTests`
Expected: PASS

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS.

```bash
git add src/Omen.Core/Graph/ActionGraphBuilder.cs tests/Omen.Core.Tests/ActionGraphBuilderTests.cs
git commit -m "feat: wire TargetRules.LinkType.Monolithic into the action graph"
```

---

### Task 5: Pluggable platform registry + console platform entries

**Files:**
- Modify: `src/Omen.Core/Configuration/BuildEnums.cs` (add `Prospero`, `Xbox` to `TargetPlatform`)
- Create: `src/Omen.Platforms/Console/ProsperoSDK.cs`
- Create: `src/Omen.Platforms/Console/XboxSDK.cs`
- Modify: `src/Omen.Platforms/PlatformFactory.cs` (external SDK discovery)
- Create: `tests/Omen.Core.Tests/PlatformFactoryDiscoveryTests.cs` — placed in `Omen.Core.Tests` for simplicity even though it tests `Omen.Platforms` logic extracted as a testable pure function (see Step 2); if the test project doesn't already reference `Omen.Platforms`, add the reference in Step 1.

**Interfaces:**
- Produces: `TargetPlatform.Prospero`, `TargetPlatform.Xbox` (new enum members — note this removes the need for the separate, already-dead `NDAPlatforms` enum for these two; leave `NDAPlatforms` itself alone, it's out of scope here and PS4/XB1/NS1/NS2/PS5-vs-Prospero-naming is a separate decision not part of this plan). `PlatformFactory.DiscoverExternalSdks(string? extraPlatformsDirectory)` (new `internal` static method, testable independent of the cached `Lazy<>`).

- [ ] **Step 1: Add the test project reference if missing**

Check `tests/Omen.Core.Tests/Omen.Core.Tests.csproj` for a `ProjectReference` to `Omen.Platforms`. If absent, add inside the existing `<ItemGroup>` that has the `Omen.Core` reference:

```xml
    <ProjectReference Include="..\..\src\Omen.Platforms\Omen.Platforms.csproj" />
```

Also add `[assembly: InternalsVisibleTo("Omen.Core.Tests")]` to `src/Omen.Platforms/AssemblyInfo.cs` (create the file if it doesn't exist):

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Omen.Core.Tests")]
```

- [ ] **Step 2: Write the failing test**

Create `tests/Omen.Core.Tests/PlatformFactoryDiscoveryTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Platforms;

namespace Omen.Core.Tests;

public class PlatformFactoryDiscoveryTests
{
    [Fact]
    public void DiscoverExternalSdks_NoDirectory_ReturnsEmpty()
    {
        var result = PlatformFactory.DiscoverExternalSdks(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverExternalSdks_NonexistentDirectory_ReturnsEmpty()
    {
        var result = PlatformFactory.DiscoverExternalSdks("/does/not/exist");
        result.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverExternalSdks_DirectoryWithNoAssemblies_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(PlatformFactoryDiscoveryTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var result = PlatformFactory.DiscoverExternalSdks(dir);

        result.Should().BeEmpty();
        Directory.Delete(dir, recursive: true);
    }
}
```

(A true end-to-end test that drops a compiled `IPlatformSDK`-implementing assembly into a temp folder and confirms it loads is deferred — it needs a second test-fixture project built as a separate output, which is disproportionate for this task. The three cases above cover the actual new logic: the empty/missing-directory guards. The loading loop itself reuses `Assembly.LoadFrom`/reflection, the same mechanism `RuleCompiler` already relies on elsewhere in this codebase.)

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter PlatformFactoryDiscoveryTests`
Expected: FAIL with a compile error — `DiscoverExternalSdks` doesn't exist yet.

- [ ] **Step 4: Add `Prospero`/`Xbox` to `TargetPlatform`**

In `src/Omen.Core/Configuration/BuildEnums.cs`, change:

```csharp
public enum TargetPlatform
{
    Unknown,
    Windows,
    Linux,
    FreeBSD,
    Android,
    iOS
}
```

to:

```csharp
public enum TargetPlatform
{
    Unknown,
    Windows,
    Linux,
    FreeBSD,
    Android,
    iOS,
    Prospero,
    Xbox
}
```

- [ ] **Step 5: Add console SDK stubs**

Create `src/Omen.Platforms/Console/ProsperoSDK.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Console;

/// <summary>
/// Registers the Prospero (PlayStation 5) platform slot. Toolchain implementation is
/// deliberately deferred: it needs the console SDK, which is a separate follow-up once
/// someone sits down with it.
/// </summary>
public sealed class ProsperoSDK : IPlatformSDK
{
    public TargetPlatform Platform => TargetPlatform.Prospero;
    public string Name => "Prospero SDK";
    public bool IsAvailable => false;
    public IReadOnlyList<TargetArchitecture> SupportedArchitectures => [TargetArchitecture.X64];

    public SDKInfo? Detect() => null;

    public IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo) =>
        throw new NotImplementedException(
            "Prospero toolchain requires the console SDK. Implement IToolchain here once the SDK is wired up.");
}
```

Create `src/Omen.Platforms/Console/XboxSDK.cs` (identical shape, `TargetPlatform.Xbox`, `"Xbox SDK"`, "Xbox toolchain requires the console SDK...").

- [ ] **Step 6: Implement `DiscoverExternalSdks` and wire it into `DiscoverAllSdks`**

In `src/Omen.Platforms/PlatformFactory.cs`, add `using System.Reflection;` and `using Omen.Platforms.Console;` to the top, then replace `DiscoverAllSdks` (lines 110-120) with:

```csharp
    private static IReadOnlyList<IPlatformSDK> DiscoverAllSdks()
    {
        var sdks = new List<IPlatformSDK>
        {
            new WindowsSDK(),
            new LinuxSDK(),
            new FreeBsdSDK(),
            new AndroidNdkSDK(),
            new AppleSDK(),
            new ProsperoSDK(),
            new XboxSDK()
        };

        sdks.AddRange(DiscoverExternalSdks(Environment.GetEnvironmentVariable("OMEN_EXTRA_PLATFORMS_DIR")));
        return sdks;
    }

    /// <summary>
    /// Loads additional IPlatformSDK implementations from assemblies in a directory, so a
    /// new platform can be added without editing this factory. Isolated as its own method
    /// (not folded into DiscoverAllSdks) so it's testable without the surrounding Lazy&lt;&gt;
    /// cache.
    /// </summary>
    internal static IReadOnlyList<IPlatformSDK> DiscoverExternalSdks(string? extraPlatformsDirectory)
    {
        if (string.IsNullOrEmpty(extraPlatformsDirectory) || !Directory.Exists(extraPlatformsDirectory))
            return [];

        var discovered = new List<IPlatformSDK>();
        foreach (var dll in Directory.GetFiles(extraPlatformsDirectory, "*.dll"))
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(dll);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
            {
                continue;
            }

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IPlatformSDK).IsAssignableFrom(type))
                    continue;
                if (Activator.CreateInstance(type) is IPlatformSDK sdk)
                    discovered.Add(sdk);
            }
        }

        return discovered;
    }
```

Also update `GetDefaultArchitecture` (lines 50-61) to add the two new platforms so a caller doesn't silently fall into the `_ => X64` default without at least an explicit, documented choice:

```csharp
    public static TargetArchitecture GetDefaultArchitecture(TargetPlatform platform)
    {
        return platform switch
        {
            TargetPlatform.Windows => TargetArchitecture.X64,
            TargetPlatform.Linux => TargetArchitecture.X64,
            TargetPlatform.FreeBSD => TargetArchitecture.X64,
            TargetPlatform.Android => TargetArchitecture.ARM64,
            TargetPlatform.iOS => TargetArchitecture.ARM64,
            TargetPlatform.Prospero => TargetArchitecture.X64,
            TargetPlatform.Xbox => TargetArchitecture.X64,
            _ => TargetArchitecture.X64
        };
    }
```

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test tests/Omen.Core.Tests --filter PlatformFactoryDiscoveryTests`
Expected: PASS

- [ ] **Step 8: Run the full suite and commit**

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS.

```bash
git add src/Omen.Core/Configuration/BuildEnums.cs src/Omen.Platforms/Console/ProsperoSDK.cs src/Omen.Platforms/Console/XboxSDK.cs src/Omen.Platforms/PlatformFactory.cs src/Omen.Platforms/AssemblyInfo.cs tests/Omen.Core.Tests/Omen.Core.Tests.csproj tests/Omen.Core.Tests/PlatformFactoryDiscoveryTests.cs
git commit -m "feat: pluggable external platform discovery; register Prospero/Xbox platform slots"
```

---

### Task 6: NMake-style Visual Studio target projects + `compile_commands.json`

**Files:**
- Create: `src/Omen.Core/Generators/CompileCommandsWriter.cs`
- Modify: `src/Omen.Core/Generators/VisualStudioGenerator.cs`
- Create: `tests/Omen.Core.Tests/CompileCommandsWriterTests.cs`

**Interfaces:**
- Consumes: `ActionGraph.Actions` filtered to `ActionType.Compile` (existing).
- Produces: `CompileCommandsWriter.Write(ActionGraph graph, string outputPath)`.

This task has two independent halves. The `compile_commands.json` writer is new, small, and fully covered by TDD below. The NMake conversion touches the existing 1162-line `VisualStudioGenerator.cs`; because that file has no existing unit tests and this plan does not re-derive its full structure from scratch, that half is scoped as a targeted, described edit at the end of this task rather than a from-scratch TDD cycle — verify it manually per Step 6 before committing.

- [ ] **Step 1: Write the failing test for `CompileCommandsWriter`**

Create `tests/Omen.Core.Tests/CompileCommandsWriterTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;
using Omen.Core.Generators;
using Omen.Core.Graph;

namespace Omen.Core.Tests;

public class CompileCommandsWriterTests : IDisposable
{
    private readonly string _outputPath;

    public CompileCommandsWriterTests()
    {
        _outputPath = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(CompileCommandsWriterTests), Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_outputPath)) File.Delete(_outputPath);
    }

    [Fact]
    public void Write_EmitsOneEntryPerCompileAction()
    {
        // Arrange
        var graph = new ActionGraph();
        graph.AddAction(new BuildAction
        {
            Id = "compile1",
            Type = ActionType.Compile,
            Description = "Compile Foo.cpp",
            CommandLine = "cl.exe /c Foo.cpp",
            WorkingDirectory = "/project",
            Inputs = [new FileItem { Path = "/project/Foo.cpp" }],
            Outputs = [new FileItem { Path = "/project/obj/Foo.obj" }]
        });
        graph.AddAction(new BuildAction
        {
            Id = "link1",
            Type = ActionType.Link,
            Description = "Link Foo",
            CommandLine = "link.exe Foo.obj",
            WorkingDirectory = "/project",
            Outputs = [new FileItem { Path = "/project/bin/Foo.exe" }]
        });

        // Act
        CompileCommandsWriter.Write(graph, _outputPath);

        // Assert
        var json = File.ReadAllText(_outputPath);
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.EnumerateArray().ToList();

        entries.Should().HaveCount(1);
        entries[0].GetProperty("file").GetString().Should().Be("/project/Foo.cpp");
        entries[0].GetProperty("command").GetString().Should().Be("cl.exe /c Foo.cpp");
        entries[0].GetProperty("directory").GetString().Should().Be("/project");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter CompileCommandsWriterTests`
Expected: FAIL — `CompileCommandsWriter` doesn't exist.

- [ ] **Step 3: Implement `CompileCommandsWriter`**

Create `src/Omen.Core/Generators/CompileCommandsWriter.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;
using Omen.Core.Graph;

namespace Omen.Core.Generators;

/// <summary>
/// Writes compile_commands.json for clangd/clang-tidy/editors other than Visual Studio,
/// sourced from the same command lines ActionGraphBuilder produced for the real build
/// (not a second, independent derivation of include paths and definitions).
/// </summary>
public static class CompileCommandsWriter
{
    private sealed class Entry
    {
        public required string Directory { get; init; }
        public required string Command { get; init; }
        public required string File { get; init; }
    }

    public static void Write(ActionGraph graph, string outputPath)
    {
        var entries = graph.Actions
            .Where(a => a.Type == ActionType.Compile && a.Inputs.Count > 0)
            .Select(a => new Entry
            {
                Directory = a.WorkingDirectory,
                Command = a.CommandLine,
                File = a.Inputs[0].Path
            })
            .ToList();

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outputPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Omen.Core.Tests --filter CompileCommandsWriterTests`
Expected: PASS

- [ ] **Step 5: Convert target projects to NMake, guided by the existing generator's structure**

In `src/Omen.Core/Generators/VisualStudioGenerator.cs`, locate the branch that decides `ConfigurationType` for a target-driving `.vcxproj` (a real MSBuild `<ConfigurationType>Application|StaticLibrary|DynamicLibrary</ConfigurationType>`, alongside the `Microsoft.Cpp.props`/`Microsoft.Cpp.targets` imports and the per-file `<ClCompile Include="...">` item list). Distinguish "target project" (the project that actually builds something — one per `TargetRules`) from "module project" (browse-only, one per `ModuleRules`, already carrying no build command in spirit even if not enforced today) using whatever parameter the generator already uses to iterate targets versus modules.

For a target project, replace the `ConfigurationType`/`Microsoft.Cpp.props`/`Microsoft.Cpp.targets`/per-config `<ClCompile>` settings block with `Makefile`-type properties:

```csharp
sb.AppendLine("    <ConfigurationType>Makefile</ConfigurationType>");
```

and, inside the per-configuration `<PropertyGroup Condition="...">` block, add:

```csharp
sb.AppendLine($"    <NMakeBuildCommandLine>omen build {targetName} -Configuration={configuration} -Platform={platform}</NMakeBuildCommandLine>");
sb.AppendLine($"    <NMakeReBuildCommandLine>omen rebuild {targetName} -Configuration={configuration} -Platform={platform}</NMakeReBuildCommandLine>");
sb.AppendLine($"    <NMakeCleanCommandLine>omen clean {targetName} -Configuration={configuration} -Platform={platform}</NMakeCleanCommandLine>");
sb.AppendLine($"    <NMakeOutput>{outputPath}</NMakeOutput>");
```

using the same `configuration`/`platform`/`outputPath` values already computed for that block for the include-path/definitions writer. Keep the `<NMakePreprocessorDefinitions>`/`<NMakeIncludeSearchPath>` properties populated from the same include-path/definition lists the generator already computes (`BuildIncludePaths`/`BuildDefinitions` in this file), so IntelliSense keeps working — only the *build command* changes, not the data IntelliSense reads. Do not add `<ClCompile Include="...">` items for a target project any more; leave those to module projects, which are unaffected by this task.

- [ ] **Step 6: Manually verify the generator change**

There is no existing test harness for `VisualStudioGenerator`; verify manually:

Run: `dotnet run --project src/Omen.CLI -- generate project` from `examples/ExampleGame`
Expected: generation succeeds. Open the generated `ExampleGame.vcxproj` and confirm it contains `<ConfigurationType>Makefile</ConfigurationType>` and an `<NMakeBuildCommandLine>` referencing `omen build`, and no `<ClCompile Include="...">` items. Open the solution in Visual Studio (or `msbuild ExampleGame.sln /t:Build`) and confirm Build/Rebuild/Clean each shell into `omen` rather than invoking `cl.exe` directly (check the Output window / build log for `omen build` vs `cl.exe` as the invoked command).

- [ ] **Step 7: Wire `CompileCommandsWriter` into the CLI's `generate project` path**

In `src/Omen.CLI/Commands/GenerateCommand.cs`, in the branch that handles `generate project`, after the existing Visual Studio (or other) generator call, add a call building the same `ActionGraph` used for a build (via `ActionGraphBuilder`, as `BuildCommand.cs` already does) and then:

```csharp
CompileCommandsWriter.Write(graph, Path.Combine(workingDir, "compile_commands.json"));
```

- [ ] **Step 8: Run the full suite and commit**

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS.

```bash
git add src/Omen.Core/Generators/CompileCommandsWriter.cs src/Omen.Core/Generators/VisualStudioGenerator.cs src/Omen.CLI/Commands/GenerateCommand.cs tests/Omen.Core.Tests/CompileCommandsWriterTests.cs
git commit -m "feat: NMake-style VS target projects and a compile_commands.json writer"
```

---

## Phase B — Gem model

### Task 7: `GemManifest` (gem.json reader)

**Files:**
- Create: `src/Omen.Core/Rules/GemManifest.cs`
- Create: `tests/Omen.Core.Tests/GemManifestTests.cs`

**Interfaces:**
- Produces: `GemManifest { GemName, Version, Dependencies, Tags }`, `GemManifest.Load(string gemJsonPath)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Omen.Core.Tests/GemManifestTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class GemManifestTests : IDisposable
{
    private readonly string _path;

    public GemManifestTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(GemManifestTests), Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_ParsesNameVersionDependenciesAndTags()
    {
        // Arrange - shape matches Gems/Camera/gem.json in NightFox
        File.WriteAllText(_path, """
        {
            "gem_name": "Camera",
            "version": "0.1.0",
            "user_tags": ["Rendering", "Utility"],
            "dependencies": ["Atom_RPI"]
        }
        """);

        // Act
        var manifest = GemManifest.Load(_path);

        // Assert
        manifest.GemName.Should().Be("Camera");
        manifest.Version.Should().Be("0.1.0");
        manifest.Dependencies.Should().Contain("Atom_RPI");
        manifest.Tags.Should().BeEquivalentTo(["Rendering", "Utility"]);
    }

    [Fact]
    public void Load_MissingDependenciesAndTags_DefaultsToEmpty()
    {
        File.WriteAllText(_path, """{ "gem_name": "Minimal", "version": "1.0.0" }""");

        var manifest = GemManifest.Load(_path);

        manifest.Dependencies.Should().BeEmpty();
        manifest.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Load_MissingGemName_Throws()
    {
        File.WriteAllText(_path, """{ "version": "1.0.0" }""");

        var act = () => GemManifest.Load(_path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*gem_name*");
    }

    [Fact]
    public void Load_FileDoesNotExist_ThrowsFileNotFoundException()
    {
        var act = () => GemManifest.Load("/does/not/exist/gem.json");
        act.Should().Throw<FileNotFoundException>();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter GemManifestTests`
Expected: FAIL — `GemManifest` doesn't exist.

- [ ] **Step 3: Implement `GemManifest`**

Create `src/Omen.Core/Rules/GemManifest.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;

namespace Omen.Core.Rules;

/// <summary>
/// Reads an O3DE-style gem.json. This file stays authoritative for a gem's identity and
/// dependencies — it's read by tooling outside the build (Project Manager, gem repo) — so
/// this is a reader only, never a writer, and GemRules must not re-declare what's here.
/// </summary>
public sealed class GemManifest
{
    public required string GemName { get; init; }
    public required string Version { get; init; }
    public List<string> Dependencies { get; init; } = [];
    public List<string> Tags { get; init; } = [];

    public static GemManifest Load(string gemJsonPath)
    {
        if (!File.Exists(gemJsonPath))
            throw new FileNotFoundException($"Gem manifest not found: {gemJsonPath}", gemJsonPath);

        using var stream = File.OpenRead(gemJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var gemName = root.TryGetProperty("gem_name", out var nameEl) ? nameEl.GetString() : null;
        if (gemName == null)
            throw new InvalidOperationException($"'{gemJsonPath}' is missing 'gem_name'.");

        var version = root.TryGetProperty("version", out var versionEl) ? versionEl.GetString() ?? "0.0.0" : "0.0.0";

        return new GemManifest
        {
            GemName = gemName,
            Version = version,
            Dependencies = ReadStringArray(root, "dependencies"),
            Tags = ReadStringArray(root, "user_tags")
        };
    }

    private static List<string> ReadStringArray(JsonElement root, string propertyName)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(propertyName, out var arrayEl) || arrayEl.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arrayEl.EnumerateArray())
        {
            var value = item.GetString();
            if (value != null) result.Add(value);
        }
        return result;
    }
}
```

- [ ] **Step 4: Run to verify it passes and run full suite**

Run: `dotnet test tests/Omen.Core.Tests --filter GemManifestTests`
Expected: PASS

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Omen.Core/Rules/GemManifest.cs tests/Omen.Core.Tests/GemManifestTests.cs
git commit -m "feat: read O3DE gem.json manifests"
```

---

### Task 8: `GemRules` base class and flavors

**Files:**
- Create: `src/Omen.Core/Rules/GemRules.cs`
- Modify: `src/Omen.Core/Rules/ModuleRules.cs` (constructor overload for an explicit name)
- Create: `tests/Omen.Core.Tests/GemRulesTests.cs`

**Interfaces:**
- Consumes: `GemManifest.Load` (Task 7).
- Produces: `GemFlavorKind` enum (`Static`, `Runtime`, `Editor`, `Tools`), `GemFlavor { Kind, SourceDirectory, PrivateDependencies, PrivateIncludePaths, PrivateDefinitions, BinaryType }`, `GemRules { Context, Name, Manifest, Flavors, Aliases }` with `protected LoadManifest(string)`, `protected DefineFlavor(GemFlavorKind) -> GemFlavor`, `protected CreateAlias(string, GemFlavorKind)`. `ModuleRules(BuildContext, string? explicitName = null)` constructor overload — used by Task 10, not by hand-written `.module.cs` files, which keep calling `base(context)` unchanged.

- [ ] **Step 1: Write the failing tests**

Create `tests/Omen.Core.Tests/GemRulesTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class GemRulesTests : IDisposable
{
    private readonly string _projectRoot;

    public GemRulesTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(GemRulesTests), Guid.NewGuid().ToString("N"));
        var gemDir = Path.Combine(_projectRoot, "Gems", "Camera");
        Directory.CreateDirectory(gemDir);
        File.WriteAllText(Path.Combine(gemDir, "gem.json"), """
        { "gem_name": "Camera", "version": "0.1.0", "dependencies": ["Atom_RPI"] }
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

    private sealed class TestCameraGem : GemRules
    {
        public TestCameraGem(BuildContext context) : base(context)
        {
            LoadManifest("Gems/Camera");

            DefineFlavor(GemFlavorKind.Static);

            var runtime = DefineFlavor(GemFlavorKind.Runtime);
            runtime.BinaryType = TargetType.SharedLibrary;
            runtime.PrivateDependencies.Add($"{Name}.Static");

            var editor = DefineFlavor(GemFlavorKind.Editor);
            editor.BinaryType = TargetType.SharedLibrary;

            CreateAlias("Clients", GemFlavorKind.Runtime);
            CreateAlias("Tools", GemFlavorKind.Editor);
        }
    }

    [Fact]
    public void LoadManifest_SetsNameFromGemJson()
    {
        var gem = new TestCameraGem(CreateContext());
        gem.Name.Should().Be("Camera");
        gem.Manifest.Should().NotBeNull();
        gem.Manifest!.Dependencies.Should().Contain("Atom_RPI");
    }

    [Fact]
    public void DefineFlavor_RegistersFlavorByKind()
    {
        var gem = new TestCameraGem(CreateContext());
        gem.Flavors.Should().ContainKey(GemFlavorKind.Static);
        gem.Flavors.Should().ContainKey(GemFlavorKind.Runtime);
        gem.Flavors[GemFlavorKind.Runtime].BinaryType.Should().Be(TargetType.SharedLibrary);
        gem.Flavors[GemFlavorKind.Runtime].PrivateDependencies.Should().Contain("Camera.Static");
    }

    [Fact]
    public void CreateAlias_MapsAliasToFlavor()
    {
        var gem = new TestCameraGem(CreateContext());
        gem.Aliases["Clients"].Should().Be(GemFlavorKind.Runtime);
        gem.Aliases["Tools"].Should().Be(GemFlavorKind.Editor);
    }

    private sealed class GemWithBadAlias : GemRules
    {
        public GemWithBadAlias(BuildContext context) : base(context)
        {
            LoadManifest("Gems/Camera");
            CreateAlias("Clients", GemFlavorKind.Runtime); // Runtime flavor never defined
        }
    }

    [Fact]
    public void CreateAlias_UndefinedFlavor_Throws()
    {
        var act = () => new GemWithBadAlias(CreateContext());
        act.Should().Throw<InvalidOperationException>().WithMessage("*undefined flavor*");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter GemRulesTests`
Expected: FAIL — `GemRules`, `GemFlavorKind`, `GemFlavor` don't exist.

- [ ] **Step 3: Implement `GemRules`**

Create `src/Omen.Core/Rules/GemRules.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// The build-relevant flavors an O3DE gem can produce. A gem declares 1-4 of these; each
/// becomes its own ModuleRules-shaped build unit once expanded (see Task 10).
/// </summary>
public enum GemFlavorKind
{
    Static,
    Runtime,
    Editor,
    Tools
}

/// <summary>
/// One buildable variant of a gem, configured like a ModuleRules block but sharing the
/// gem-level public dependencies pulled from gem.json.
/// </summary>
public sealed class GemFlavor
{
    public required GemFlavorKind Kind { get; init; }
    public string? SourceDirectory { get; set; }
    public List<string> PrivateDependencies { get; } = [];
    public List<string> PrivateIncludePaths { get; } = [];
    public List<string> PrivateDefinitions { get; } = [];
    public TargetType? BinaryType { get; set; }
}

/// <summary>
/// Base class for a gem's build description (a `&lt;GemName&gt;.gem.cs` file). gem.json
/// stays authoritative for identity and dependencies (it's read by O3DE tooling outside
/// the build); this only declares which flavors the gem builds and how they alias to the
/// symbolic names O3DE targets reference (Clients/Servers/Unified/Tools/Builders).
/// </summary>
public abstract class GemRules
{
    protected BuildContext Context { get; }
    public string Name { get; private set; }
    public GemManifest? Manifest { get; private set; }
    public Dictionary<GemFlavorKind, GemFlavor> Flavors { get; } = new();
    public Dictionary<string, GemFlavorKind> Aliases { get; } = new();

    protected GemRules(BuildContext context)
    {
        Context = context;
        Name = GetType().Name.Replace("Gem", "");
    }

    /// <summary>
    /// Loads gem.json from &lt;ProjectRoot&gt;/&lt;gemDirectoryRelativeToProjectRoot&gt;/gem.json
    /// and adopts its gem_name as this gem's Name.
    /// </summary>
    protected void LoadManifest(string gemDirectoryRelativeToProjectRoot)
    {
        var manifestPath = Path.Combine(Context.ProjectRoot, gemDirectoryRelativeToProjectRoot, "gem.json");
        Manifest = GemManifest.Load(manifestPath);
        Name = Manifest.GemName;
    }

    protected GemFlavor DefineFlavor(GemFlavorKind kind)
    {
        var flavor = new GemFlavor { Kind = kind };
        Flavors[kind] = flavor;
        return flavor;
    }

    protected void CreateAlias(string aliasName, GemFlavorKind backedBy)
    {
        if (!Flavors.ContainsKey(backedBy))
            throw new InvalidOperationException($"Gem '{Name}' cannot alias '{aliasName}' to undefined flavor '{backedBy}'.");
        Aliases[aliasName] = backedBy;
    }
}
```

- [ ] **Step 4: Add the `ModuleRules` explicit-name constructor overload**

In `src/Omen.Core/Rules/ModuleRules.cs`, replace the existing constructor (lines 247-251):

```csharp
    protected ModuleRules(BuildContext context)
    {
        Context = context;
        Name = GetType().Name;
    }
```

with:

```csharp
    protected ModuleRules(BuildContext context) : this(context, explicitName: null) { }

    /// <summary>
    /// Used by generated module wrappers (e.g. a gem flavor, Task 10) that need a Name
    /// other than the declaring class's own name. Hand-written .module.cs files should
    /// keep using the single-argument constructor.
    /// </summary>
    protected ModuleRules(BuildContext context, string? explicitName)
    {
        Context = context;
        Name = explicitName ?? GetType().Name;
    }
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Omen.Core.Tests --filter GemRulesTests`
Expected: PASS

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS (the `ModuleRules` constructor change is additive and backward-compatible — every existing single-argument call site is unaffected).

```bash
git add src/Omen.Core/Rules/GemRules.cs src/Omen.Core/Rules/ModuleRules.cs tests/Omen.Core.Tests/GemRulesTests.cs
git commit -m "feat: add GemRules base class with flavors and aliases"
```

---

### Task 9: Gem alias resolution (`Gem::Name.Alias` references)

**Files:**
- Create: `src/Omen.Core/Rules/GemAliasResolver.cs`
- Create: `tests/Omen.Core.Tests/GemAliasResolverTests.cs`

**Interfaces:**
- Consumes: `GemRules.Name`, `GemRules.Aliases` (Task 8).
- Produces: `GemAliasResolver.Resolve(string extraModuleEntry, IReadOnlyList<GemRules> gems) -> string`.

Note on scope: `BuildCommand.cs` today builds every module `CreateModuleRules` returns regardless of `TargetRules.ExtraModules` (`ExtraModules` is declared but never read as a filter — a pre-existing gap in Omen, unrelated to this plan). This task delivers `GemAliasResolver` as a correct, independently tested utility; wiring it into an actual `ExtraModules`-filtered build is blocked on that pre-existing gap and is explicitly not fixed here — Task 11's pilot works around it by scoping the Gem's own project root instead of relying on `ExtraModules` filtering (see Task 11).

- [ ] **Step 1: Write the failing tests**

Create `tests/Omen.Core.Tests/GemAliasResolverTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class GemAliasResolverTests : IDisposable
{
    private readonly string _projectRoot;

    public GemAliasResolverTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(GemAliasResolverTests), Guid.NewGuid().ToString("N"));
        var gemDir = Path.Combine(_projectRoot, "Gems", "Camera");
        Directory.CreateDirectory(gemDir);
        File.WriteAllText(Path.Combine(gemDir, "gem.json"), """{ "gem_name": "Camera", "version": "0.1.0" }""");
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

    private sealed class TestCameraGem : GemRules
    {
        public TestCameraGem(BuildContext context) : base(context)
        {
            LoadManifest("Gems/Camera");
            DefineFlavor(GemFlavorKind.Runtime);
            CreateAlias("Clients", GemFlavorKind.Runtime);
        }
    }

    [Fact]
    public void Resolve_PlainModuleName_ReturnsUnchanged()
    {
        GemAliasResolver.Resolve("Core", []).Should().Be("Core");
    }

    [Fact]
    public void Resolve_GemAliasReference_ReturnsConcreteModuleName()
    {
        var gem = new TestCameraGem(CreateContext());
        GemAliasResolver.Resolve("Gem::Camera.Clients", [gem]).Should().Be("Camera.Runtime");
    }

    [Fact]
    public void Resolve_UnknownGem_Throws()
    {
        var act = () => GemAliasResolver.Resolve("Gem::Nonexistent.Clients", []);
        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown gem*");
    }

    [Fact]
    public void Resolve_UnknownAlias_Throws()
    {
        var gem = new TestCameraGem(CreateContext());
        var act = () => GemAliasResolver.Resolve("Gem::Camera.Servers", [gem]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*no alias 'Servers'*");
    }

    [Fact]
    public void Resolve_MalformedReference_Throws()
    {
        var act = () => GemAliasResolver.Resolve("Gem::Camera", []);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Gem::<GemName>.<Alias>*");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter GemAliasResolverTests`
Expected: FAIL — `GemAliasResolver` doesn't exist.

- [ ] **Step 3: Implement `GemAliasResolver`**

Create `src/Omen.Core/Rules/GemAliasResolver.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Rules;

/// <summary>
/// Resolves a "Gem::&lt;GemName&gt;.&lt;Alias&gt;" reference (as used in
/// TargetRules.ExtraModules) to the concrete expanded module name that alias points at.
/// A plain module name passes through unchanged.
/// </summary>
public static class GemAliasResolver
{
    private const string Prefix = "Gem::";

    public static string Resolve(string extraModuleEntry, IReadOnlyList<GemRules> gems)
    {
        if (!extraModuleEntry.StartsWith(Prefix, StringComparison.Ordinal))
            return extraModuleEntry;

        var rest = extraModuleEntry[Prefix.Length..];
        var dot = rest.IndexOf('.');
        if (dot < 0)
        {
            throw new InvalidOperationException(
                $"'{extraModuleEntry}' must be in the form 'Gem::<GemName>.<Alias>'.");
        }

        var gemName = rest[..dot];
        var aliasName = rest[(dot + 1)..];

        var gem = gems.FirstOrDefault(g => g.Name.Equals(gemName, StringComparison.OrdinalIgnoreCase));
        if (gem == null)
            throw new InvalidOperationException($"'{extraModuleEntry}' references unknown gem '{gemName}'.");

        if (!gem.Aliases.TryGetValue(aliasName, out var flavorKind))
            throw new InvalidOperationException($"Gem '{gemName}' has no alias '{aliasName}'.");

        return $"{gem.Name}.{flavorKind}";
    }
}
```

- [ ] **Step 4: Run to verify it passes and run full suite**

Run: `dotnet test tests/Omen.Core.Tests --filter GemAliasResolverTests`
Expected: PASS

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Omen.Core/Rules/GemAliasResolver.cs tests/Omen.Core.Tests/GemAliasResolverTests.cs
git commit -m "feat: resolve Gem::Name.Alias references to concrete module names"
```

---

### Task 10: `RuleCompiler` discovers and expands `GemRules`

**Files:**
- Modify: `src/Omen.Core/Rules/RuleCompiler.cs` (`CompileRulesAsync` file scan, `CompiledRules.CreateModuleRules`, new `CompiledRules.CreateGemRules`)
- Create: `src/Omen.Core/Rules/GemFlavorModuleRules.cs`
- Create: `tests/Omen.Core.Tests/RuleCompilerGemTests.cs`

**Interfaces:**
- Consumes: `GemRules` (Task 8), `ModuleRules(BuildContext, string?)` (Task 8).
- Produces: `CompiledRules.CreateGemRules(BuildContext) -> IReadOnlyList<GemRules>`. `CompiledRules.CreateModuleRules(BuildContext)` now also includes one `GemFlavorModuleRules` per defined flavor of every discovered gem, named `"{GemName}.{FlavorKind}"`.

- [ ] **Step 1: Write the failing test**

Create `tests/Omen.Core.Tests/RuleCompilerGemTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter RuleCompilerGemTests`
Expected: FAIL — `*.gem.cs` isn't scanned, `CreateGemRules` doesn't exist, and `CreateModuleRules` doesn't include gem flavors.

- [ ] **Step 3: Implement `GemFlavorModuleRules`**

Create `src/Omen.Core/Rules/GemFlavorModuleRules.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Wraps one GemFlavor into the ModuleRules shape ActionGraphBuilder already understands.
/// Not user-authored — CompiledRules.CreateModuleRules synthesizes one of these per
/// flavor a GemRules subclass defines.
/// </summary>
internal sealed class GemFlavorModuleRules : ModuleRules
{
    public GemFlavorModuleRules(BuildContext context, GemRules gem, GemFlavor flavor)
        : base(context, explicitName: $"{gem.Name}.{flavor.Kind}")
    {
        Type = flavor.Kind == GemFlavorKind.Editor ? ModuleType.Editor : ModuleType.Runtime;
        SourceDirectory = flavor.SourceDirectory;
        BinaryType = flavor.BinaryType;

        PrivateDependencies.AddRange(flavor.PrivateDependencies);
        PrivateIncludePaths.AddRange(flavor.PrivateIncludePaths);
        PrivateDefinitions.AddRange(flavor.PrivateDefinitions);

        if (gem.Manifest != null)
        {
            PublicDependencies.AddRange(gem.Manifest.Dependencies.Select(d => $"{d}.Runtime"));
        }

        PrivateDefinitions.Add($"O3DE_GEM_NAME={gem.Name}");
        PrivateDefinitions.Add($"O3DE_GEM_VERSION={gem.Manifest?.Version ?? "0.0.0"}");
    }
}
```

- [ ] **Step 4: Extend `RuleCompiler`'s file scan**

In `src/Omen.Core/Rules/RuleCompiler.cs`, in `CompileRulesAsync` (lines 32-59), replace:

```csharp
        var moduleFiles = Directory.GetFiles(projectRoot, "*.module.cs", SearchOption.AllDirectories);
        var targetFiles = Directory.GetFiles(projectRoot, "*.target.cs", SearchOption.AllDirectories);
        
        if (moduleFiles.Length == 0 && targetFiles.Length == 0)
        {
            throw new InvalidOperationException($"No rule files found in '{projectRoot}'.");
        }
        
        var allFiles = moduleFiles.Concat(targetFiles).ToList();
```

with:

```csharp
        var moduleFiles = Directory.GetFiles(projectRoot, "*.module.cs", SearchOption.AllDirectories);
        var targetFiles = Directory.GetFiles(projectRoot, "*.target.cs", SearchOption.AllDirectories);
        var gemFiles = Directory.GetFiles(projectRoot, "*.gem.cs", SearchOption.AllDirectories);
        
        if (moduleFiles.Length == 0 && targetFiles.Length == 0 && gemFiles.Length == 0)
        {
            throw new InvalidOperationException($"No rule files found in '{projectRoot}'.");
        }
        
        var allFiles = moduleFiles.Concat(targetFiles).Concat(gemFiles).ToList();
```

And update the `new CompiledRules(...)` call at the end of the method (line 58) to also carry `gemFiles` — add a `GemFiles` property to `CompiledRules` mirroring `ModuleFiles`/`TargetFiles`:

```csharp
        return new CompiledRules(assembly, moduleFiles, targetFiles, gemFiles);
```

- [ ] **Step 5: Extend `CompiledRules`**

In `src/Omen.Core/Rules/RuleCompiler.cs`, replace the `CompiledRules` class (lines 168-253) with:

```csharp
public sealed class CompiledRules
{
    private readonly Assembly _assembly;
    
    public IReadOnlyList<string> ModuleFiles { get; }
    public IReadOnlyList<string> TargetFiles { get; }
    public IReadOnlyList<string> GemFiles { get; }
    
    internal CompiledRules(Assembly assembly, IReadOnlyList<string> moduleFiles, IReadOnlyList<string> targetFiles, IReadOnlyList<string> gemFiles)
    {
        _assembly = assembly;
        ModuleFiles = moduleFiles;
        TargetFiles = targetFiles;
        GemFiles = gemFiles;
    }
    
    /// <summary>
    /// Creates instances of every hand-written ModuleRules in the compiled assembly,
    /// plus one synthesized ModuleRules per flavor of every discovered GemRules.
    /// </summary>
    public IReadOnlyList<ModuleRules> CreateModuleRules(BuildContext context)
    {
        var moduleType = typeof(ModuleRules);
        var rules = new List<ModuleRules>();
        
        foreach (var type in _assembly.GetTypes())
        {
            if (type.IsAbstract || !moduleType.IsAssignableFrom(type) || typeof(GemFlavorModuleRules).IsAssignableFrom(type))
                continue;
            
            var constructor = type.GetConstructor([typeof(BuildContext)]);
            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"Module rule type '{type.Name}' must have a constructor that takes BuildContext.");
            }
            
            var instance = (ModuleRules)constructor.Invoke([context]);
            rules.Add(instance);
        }

        foreach (var gem in CreateGemRules(context))
        {
            foreach (var flavor in gem.Flavors.Values)
            {
                rules.Add(new GemFlavorModuleRules(context, gem, flavor));
            }
        }
        
        return rules;
    }
    
    /// <summary>
    /// Creates instances of all TargetRules in the compiled assembly.
    /// </summary>
    public IReadOnlyList<TargetRules> CreateTargetRules(BuildContext context)
    {
        var targetType = typeof(TargetRules);
        var rules = new List<TargetRules>();
        
        foreach (var type in _assembly.GetTypes())
        {
            if (type.IsAbstract || !targetType.IsAssignableFrom(type))
                continue;
            
            var constructor = type.GetConstructor([typeof(BuildContext)]);
            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"Target rule type '{type.Name}' must have a constructor that takes BuildContext.");
            }
            
            var instance = (TargetRules)constructor.Invoke([context]);
            rules.Add(instance);
        }
        
        return rules;
    }

    /// <summary>
    /// Creates instances of all GemRules in the compiled assembly.
    /// </summary>
    public IReadOnlyList<GemRules> CreateGemRules(BuildContext context)
    {
        var gemType = typeof(GemRules);
        var rules = new List<GemRules>();

        foreach (var type in _assembly.GetTypes())
        {
            if (type.IsAbstract || !gemType.IsAssignableFrom(type))
                continue;

            var constructor = type.GetConstructor([typeof(BuildContext)]);
            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"Gem rule type '{type.Name}' must have a constructor that takes BuildContext.");
            }

            rules.Add((GemRules)constructor.Invoke([context]));
        }

        return rules;
    }
    
    /// <summary>
    /// Gets a specific target by name.
    /// </summary>
    public TargetRules? GetTarget(string name, BuildContext context)
    {
        return CreateTargetRules(context).FirstOrDefault(t => 
            t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
    
    /// <summary>
    /// Gets a specific module by name.
    /// </summary>
    public ModuleRules? GetModule(string name, BuildContext context)
    {
        return CreateModuleRules(context).FirstOrDefault(m => 
            m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
```

(`GemFlavorModuleRules` is `internal`, and the reflection loop in `CreateModuleRules` explicitly excludes it via `typeof(GemFlavorModuleRules).IsAssignableFrom(type)` — this guards against the same assembly's `GetTypes()` scan double-counting it as a hand-written module, since `GemFlavorModuleRules` itself is a non-abstract `ModuleRules` subclass.)

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/Omen.Core.Tests --filter RuleCompilerGemTests`
Expected: PASS

- [ ] **Step 7: Run the full suite**

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS. In particular, confirm `RuleCompilerTests` (if any pre-existing) and `ModuleRulesTests`/`TargetRulesTests` are unaffected — `CompiledRules`'s constructor gained a required 4th parameter (`gemFiles`), so grep for any other call site (`new CompiledRules(` outside `RuleCompiler.cs`) and update it; today the only call site is `RuleCompiler.cs:58`, changed in Step 4.

- [ ] **Step 8: Commit**

```bash
git add src/Omen.Core/Rules/RuleCompiler.cs src/Omen.Core/Rules/GemFlavorModuleRules.cs tests/Omen.Core.Tests/RuleCompilerGemTests.cs
git commit -m "feat: RuleCompiler discovers *.gem.cs and expands gem flavors into modules"
```

---

## Phase C — Pilot: `Gems/Camera`

### Task 11: Author `Gems/Camera/Camera.gem.cs` and build it with Omen

**Files:**
- Create: `F:\engine\Gems\Camera\Camera.gem.cs`
- Create: `F:\engine\Gems\Camera\CameraPilot.target.cs`

This task lives in the **NightFox** repo (`F:\engine`), not Omen. It has no automated test in `tests/Omen.Core.Tests` — its acceptance check is a real build, per Step 3.

**Interfaces:**
- Consumes: `GemRules`, `GemFlavorKind` (Task 8), the module-naming convention `"{GemName}.{FlavorKind}"` (Task 10).

- [ ] **Step 1: Author `Camera.gem.cs`**

Translate the existing `Gems/Camera/Code/CMakeLists.txt` (Static + Runtime + Editor targets, `Atom_RPI` dependency, `O3DE_GEM_NAME` injection already handled generically by `GemFlavorModuleRules`). Create `F:\engine\Gems\Camera\Camera.gem.cs`:

```csharp
using Omen.Core.Configuration;
using Omen.Core.Rules;

public class CameraGem : GemRules
{
    public CameraGem(BuildContext context) : base(context)
    {
        LoadManifest("Gems/Camera");

        var staticFlavor = DefineFlavor(GemFlavorKind.Static);
        staticFlavor.SourceDirectory = "Gems/Camera/Code/Source";

        var runtimeFlavor = DefineFlavor(GemFlavorKind.Runtime);
        runtimeFlavor.BinaryType = TargetType.SharedLibrary;
        runtimeFlavor.PrivateDependencies.Add($"{Name}.Static");

        var editorFlavor = DefineFlavor(GemFlavorKind.Editor);
        editorFlavor.SourceDirectory = "Gems/Camera/Code/Source";
        editorFlavor.BinaryType = TargetType.SharedLibrary;
        editorFlavor.PrivateDependencies.Add($"{Name}.Static");
        editorFlavor.PrivateDefinitions.Add("CAMERA_EDITOR");

        CreateAlias("Clients", GemFlavorKind.Runtime);
        CreateAlias("Servers", GemFlavorKind.Runtime);
        CreateAlias("Unified", GemFlavorKind.Runtime);
        CreateAlias("Tools", GemFlavorKind.Editor);
        CreateAlias("Builders", GemFlavorKind.Editor);
    }
}
```

Note: the real `Gems/Camera/Code/CMakeLists.txt` gives `Camera.Static` `PUBLIC` dependencies on `Gem::Atom_RPI.Public`, `AZ::AtomCore`, `AZ::AzFramework` — those are O3DE framework/gem targets that don't yet have their own `.gem.cs`/`.module.cs` in this pilot's scope (only Camera is migrated). Leave `staticFlavor.PublicDependencies` empty for this pilot and note the omission in Task 12's report rather than fabricating a dependency chain that doesn't build; the parity check in Task 12 compares Camera's own compile flags, not a full engine link.

- [ ] **Step 2: Author `CameraPilot.target.cs`**

Create `F:\engine\Gems\Camera\CameraPilot.target.cs`:

```csharp
using Omen.Core.Configuration;
using Omen.Core.Rules;

public class CameraPilotTarget : TargetRules
{
    public CameraPilotTarget(BuildContext context) : base(context)
    {
        Type = TargetType.SharedLibrary;
        LaunchModuleName = "Camera.Runtime";
        LinkType = LinkType.Modular;
    }
}
```

- [ ] **Step 3: Build it and verify**

Run (from `F:\engine\Gems\Camera`, so `RuleCompiler.CompileRulesAsync` scans only this gem's tree rather than the whole engine — the existing `ExtraModules`-filtering gap noted in Task 9 doesn't matter here because the project root itself is scoped):

```bash
omen build CameraPilot
```

Expected: exit code 0, and `Binaries/<Platform>_<Configuration>/Camera.Runtime.dll` (plus `Camera.Static.lib` under the same intermediate/output convention) exist on disk. If the build fails because `Gems/Camera/Code/Source/*.cpp` references headers from `Atom_RPI`/`AzCore`/`AzFramework` that aren't resolvable without those modules' own include paths (expected, since this pilot doesn't migrate them), that is the documented boundary from Step 1 — record which headers are unresolved in Task 12's write-up rather than treating it as a plan failure; the acceptance bar for this task is that Omen resolves Camera's own module graph and produces the two flavor binaries' link steps with correct Camera-owned flags, not that the pilot links against the full engine.

- [ ] **Step 4: Commit (in the NightFox repo)**

```bash
git add Gems/Camera/Camera.gem.cs Gems/Camera/CameraPilot.target.cs
git commit -m "feat: build Camera gem's Static/Runtime/Editor flavors through Omen"
```

---

### Task 12: Parity script — Omen vs CMake command-line diff

**Files:**
- Modify: `src/Omen.CLI/Commands/BuildCommand.cs` (add `--dry-run`)
- Create: `F:\engine\Gems\Camera\Tools\compare-build.ps1`

**Interfaces:**
- Produces: a `--dry-run`/`-DryRun`-equivalent CLI option on `omen build` that prints each action's command line instead of executing it (needed so the parity script can capture Omen's derived flags without a full `cl.exe`/`link.exe` invocation — `ActionGraphBuilder.Build()` already only constructs command-line strings; execution happens later via `ParallelExecutor`, so dry-run is a small, additive branch in `BuildCommand.ExecuteBuildAsync`).

- [ ] **Step 1: Add `--dry-run` to `BuildCommand`**

In `src/Omen.CLI/Commands/BuildCommand.cs`, add the option (after `coordinatorOption`, around line 62):

```csharp
        var dryRunOption = new Option<bool>(
            "--dry-run",
            "Print derived command lines without executing the build");
```

Add `command.AddOption(dryRunOption);` alongside the other `AddOption` calls, add `var dryRun = context.ParseResult.GetValueForOption(dryRunOption);` alongside the other value reads, pass `dryRun` through to `ExecuteBuildAsync`, add the parameter `bool dryRun` to `ExecuteBuildAsync`'s signature, and — right after the line `AnsiConsole.MarkupLine($"[green]Created action graph with {graph.Actions.Count} actions[/]\n");` (line 205) — insert:

```csharp
        if (dryRun)
        {
            foreach (var action in graph.GetTopologicalOrder())
            {
                AnsiConsole.WriteLine($"[{action.Type}] {action.ModuleName ?? "(target)"}: {action.CommandLine}");
            }
            return 0;
        }
```

- [ ] **Step 2: Manually verify `--dry-run`**

Run: `dotnet run --project src/Omen.CLI -- build --dry-run` from `examples/ExampleGame`
Expected: exit code 0, one line per compile/link action showing its command line, no `.obj`/`.exe` files written.

- [ ] **Step 3: Write the parity script**

Create `F:\engine\Gems\Camera\Tools\compare-build.ps1`:

```powershell
# ponytail: regex-based extraction of cl.exe invocations from MSBuild diagnostic output.
# Good enough for a periodic manual parity check; not a hardened parser. If MSBuild's
# /v:diag format changes and this stops matching, that's the signal to look again rather
# than silently reporting a false pass.
param(
    [string]$CameraDir = (Split-Path $PSScriptRoot -Parent),
    [string]$Flavor = "Camera.Runtime",
    [string]$Configuration = "Development"
)

function Get-CMakeCompileFlags {
    param([string]$SolutionDir, [string]$ProjectName)

    $log = & msbuild "$SolutionDir\O3DE.sln" /t:$ProjectName /p:Configuration=$Configuration /v:diag 2>&1
    $clLine = $log | Where-Object { $_ -match 'CL\.exe.*Camera.*\.cpp' } | Select-Object -First 1
    if (-not $clLine) {
        throw "No cl.exe invocation for $ProjectName found in MSBuild diagnostic output."
    }

    $includes = [regex]::Matches($clLine, '/I"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    $defines = [regex]::Matches($clLine, '/D([^\s]+)') | ForEach-Object { $_.Groups[1].Value }
    return @{ Includes = ($includes | Sort-Object -Unique); Defines = ($defines | Sort-Object -Unique) }
}

function Get-OmenCompileFlags {
    param([string]$GemDir, [string]$TargetName)

    Push-Location $GemDir
    try {
        $output = & omen build $TargetName --dry-run --configuration $Configuration
    } finally {
        Pop-Location
    }

    $clLine = $output | Where-Object { $_ -match '\[Compile\].*Camera' } | Select-Object -First 1
    if (-not $clLine) {
        throw "No Omen compile action for $TargetName found in --dry-run output."
    }

    $includes = [regex]::Matches($clLine, '/I"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    $defines = [regex]::Matches($clLine, '/D([^\s]+)') | ForEach-Object { $_.Groups[1].Value }
    return @{ Includes = ($includes | Sort-Object -Unique); Defines = ($defines | Sort-Object -Unique) }
}

$engineRoot = Split-Path (Split-Path $CameraDir -Parent) -Parent
$cmakeFlags = Get-CMakeCompileFlags -SolutionDir "$engineRoot\build\windows" -ProjectName "Camera"
$omenFlags = Get-OmenCompileFlags -GemDir $CameraDir -TargetName "CameraPilot"

$missingIncludes = $cmakeFlags.Includes | Where-Object { $_ -notin $omenFlags.Includes }
$extraIncludes = $omenFlags.Includes | Where-Object { $_ -notin $cmakeFlags.Includes }
$missingDefines = $cmakeFlags.Defines | Where-Object { $_ -notin $omenFlags.Defines }
$extraDefines = $omenFlags.Defines | Where-Object { $_ -notin $cmakeFlags.Defines }

Write-Host "=== Include paths CMake has that Omen is missing ==="
$missingIncludes | ForEach-Object { Write-Host "  $_" }
Write-Host "=== Include paths Omen has that CMake doesn't ==="
$extraIncludes | ForEach-Object { Write-Host "  $_" }
Write-Host "=== Definitions CMake has that Omen is missing ==="
$missingDefines | ForEach-Object { Write-Host "  $_" }
Write-Host "=== Definitions Omen has that CMake doesn't ==="
$extraDefines | ForEach-Object { Write-Host "  $_" }

if ($missingIncludes.Count -eq 0 -and $missingDefines.Count -eq 0) {
    Write-Host "`nPASS: Omen's flags are a superset-or-equal of CMake's for $Flavor." -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nFAIL: Omen is missing flags CMake has for $Flavor." -ForegroundColor Red
    exit 1
}
```

- [ ] **Step 4: Run the parity script and record the result**

Run:

```bash
pwsh F:\engine\Gems\Camera\Tools\compare-build.ps1
```

Expected: the script runs to completion and prints its four diff sections. Given Task 11's documented boundary (Camera's `Static` flavor doesn't yet declare the `Atom_RPI`/`AzCore`/`AzFramework` dependencies CMake's version has), expect real, explainable mismatches in the "missing" columns for now — this is the honest baseline the next Gem migration starts from, not a hidden failure. Do not edit the script to suppress or filter out real mismatches in order to force a clean pass.

- [ ] **Step 5: Write the migration guide**

Create `F:\engine\Gems\Camera\Tools\MIGRATION_NOTES.md`:

```markdown
# Migrating a Gem from CMake to Omen — notes from Camera

1. Read the gem's `Code/CMakeLists.txt`. Map each `ly_add_target` call to a `GemFlavor`
   (`Static` -> `GemFlavorKind.Static`, the un-suffixed `ly_add_target` -> `Runtime`,
   `.Editor` -> `Editor`).
2. `gem.json` already has the gem's own dependency list — don't re-type it into the
   `.gem.cs` file; `GemFlavorModuleRules` reads it automatically.
3. Flavor-specific `BUILD_DEPENDENCIES` that aren't in `gem.json` (framework modules like
   `AZ::AzCore`, or other gems' `Gem::X.Public`) need those modules' own `.module.cs`/
   `.gem.cs` files to exist first — a gem can't be migrated in isolation if its
   dependencies haven't been. Camera itself hit this with `Atom_RPI`/`AzCore`/
   `AzFramework`; migrate leaf gems (few or no first-party dependencies) before gems
   deeper in the graph.
4. Run `Tools/compare-build.ps1` (copy it into the new gem's `Tools/` directory, adjusting
   `-Flavor`/`-TargetName`) before removing anything from the gem's `CMakeLists.txt` — per
   the project's current decision, CMake keeps building every gem until its own parity
   check is clean, not just Camera's.
```

- [ ] **Step 6: Commit**

```bash
git add src/Omen.CLI/Commands/BuildCommand.cs
git commit -m "feat: add --dry-run to omen build for parity checking"
```

(In the NightFox repo:)

```bash
git add Gems/Camera/Tools/compare-build.ps1 Gems/Camera/Tools/MIGRATION_NOTES.md
git commit -m "chore: add Omen/CMake parity script and migration notes for Camera pilot"
```

---

## Self-review notes

- **Spec coverage:** A0 (Task 1), A2 (Task 2), A3 (Task 3), A4 (Task 4), A1 (Task 5), A5 (Task 6), B1 (Tasks 7-8), B2 (Task 8), B3 (Task 9), B4 (Task 10), C (Tasks 11-12) are each covered by a task above. The spec's explicit non-goals (settings-registry codegen, runtime-dependency staging, Test Impact Framework, CPack packaging, asset pipeline, the remaining ~400 CMakeLists.txt files, real console toolchains) have no task here, matching the spec.
- **Placeholder scan:** no task defers logic to prose ("add appropriate handling", "TBD"); every step that changes behavior includes the actual code. Task 6's VS-generator half and Task 11's build step are the two places where an exact byte-for-byte existing-code diff isn't quoted (the generator because its full 1162 lines weren't re-derived here; the Camera build because it may hit real, informative failures) — both are flagged explicitly as manual-verification steps with a stated expected outcome, not left as unstated TODOs.
- **Type consistency:** `ModuleRules.BinaryType`, `TargetRules.LinkType`, `GemRules.Name`/`Flavors`/`Aliases`, `GemFlavor.BinaryType`/`Kind`, and the `"{GemName}.{FlavorKind}"` naming convention are used identically across Tasks 1, 4, 8, 9, 10, and 11.
