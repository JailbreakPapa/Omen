# Omen GUI: Build Options Panel + UI Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a CMake-cache-style build-options system (declare → discover → edit → persist → affect the next build) plus a UI polish pass (collapsible three-pane layout, a custom "Signal" theme, toolbar icons, a status dot) to the Omen GUI.

**Architecture:** A new `Omen.Core.Options` namespace holds the declaration API (`BuildOptions.Declare`), the declaration type, and the JSON cache persistence (`OptionCacheStore`) — pure logic, no Executors/GUI dependency, mirroring how `ActionDigestStore` already lives in `Omen.Core.Graph`. A new `OptionsOrchestrator` in `Omen.Executors/Orchestration/` reuses `BuildOrchestrator`'s resolve-and-instantiate sequence to discover declared options without building anything. `BuildOrchestrator`/`ProjectGenerationOrchestrator` each gain one addition: load the cache before instantiating rules. The GUI gets a new collapsible Options pane bound to an `OptionsPanelViewModel`, plus a `Signal.axaml` style layer and an `Icons.axaml` resource dictionary applied to the existing window.

**Tech Stack:** C# / .NET 8, existing `Omen.Core`/`Omen.Executors`/`Omen.GUI` projects, `System.Text.Json` (already used by `ActionDigestStore`/`GuiSettings`), Avalonia's built-in `GridSplitter`/`PathIcon`/`StreamGeometry` (no new NuGet dependencies).

## Global Constraints

- Every new/changed `.cs` file keeps the copyright header: `// Omen Build System` / `// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.`
- No new NuGet dependencies. `Directory.Packages.props` is not touched by this plan.
- The option cache is one file per project (`Intermediate/omen-cache.json`), shared across every platform/configuration — not per-config.
- Declaring the same option name twice in one discovery/build pass is not an error: the first declaration's default and type win; later calls just return the already-recorded effective value.
- A malformed/unreadable `omen-cache.json` degrades to an empty cache (log nothing fatal, don't throw) — matching `ActionDigestStore`'s and `GuiSettings`'s existing malformed-JSON handling.
- View-layer (XAML/theme) changes are verified by running the app against a real project, per this GUI's established testing convention — not unit-tested. Core/Executors logic changes get real unit tests following the existing temp-directory-and-real-rule-compilation pattern (see `tests/Omen.Executors.Tests/BuildOrchestratorTests.cs` for the house style).

---

## Task 1: `BuildOptions`, `BuildOptionDeclaration`, `OptionCacheStore`

**Files:**
- Create: `src/Omen.Core/Options/BuildOptions.cs`
- Create: `src/Omen.Core/Options/OptionCacheStore.cs`
- Modify: `src/Omen.Core/Configuration/BuildContext.cs`
- Test: `tests/Omen.Core.Tests/BuildOptionsTests.cs`
- Test: `tests/Omen.Core.Tests/OptionCacheStoreTests.cs`

**Interfaces:**
- Produces: `Omen.Core.Options.BuildOptionType` (enum: `Bool`, `String`, `Int`, `Path`), `Omen.Core.Options.BuildOptionDeclaration { string Name, string Description, BuildOptionType Type, string DefaultValue, string EffectiveValue }`, `Omen.Core.Options.BuildOptions.Declare(BuildContext, string name, string description, bool defaultValue) -> bool`, `.Declare(BuildContext, string, string, string defaultValue) -> string`, `.Declare(BuildContext, string, string, int defaultValue) -> int`, `.DeclarePath(BuildContext, string, string, string defaultValue) -> string`. `Omen.Core.Options.OptionCacheStore(string path)` with `.Load() -> IReadOnlyDictionary<string, string>` and `.Save(IReadOnlyDictionary<string, string>)`. `BuildContext.DeclaredOptions : List<BuildOptionDeclaration>` (mutable, `init` empty list) and `BuildContext.CachedOptionValues : IReadOnlyDictionary<string, string>` (`init`, empty dictionary default).

- [ ] **Step 1: Write the failing tests for `BuildContext`'s new properties and `BuildOptions`**

Create `tests/Omen.Core.Tests/BuildOptionsTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Options;

namespace Omen.Core.Tests;

public class BuildOptionsTests
{
    private static BuildContext CreateContext(IReadOnlyDictionary<string, string>? cached = null) => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = "/test/project",
        OutputDirectory = "/test/project/bin",
        IntermediateDirectory = "/test/project/obj",
        CachedOptionValues = cached ?? new Dictionary<string, string>()
    };

    [Fact]
    public void Declare_Bool_NoCachedValue_ReturnsDefaultAndRecordsDeclaration()
    {
        var context = CreateContext();

        var result = BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable feature X", false);

        result.Should().BeFalse();
        context.DeclaredOptions.Should().ContainSingle(o =>
            o.Name == "ENABLE_FEATURE_X" &&
            o.Description == "Enable feature X" &&
            o.Type == BuildOptionType.Bool &&
            o.DefaultValue == "false" &&
            o.EffectiveValue == "false");
    }

    [Fact]
    public void Declare_Bool_WithCachedValue_ReturnsCachedOverride()
    {
        var context = CreateContext(new Dictionary<string, string> { ["ENABLE_FEATURE_X"] = "true" });

        var result = BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable feature X", false);

        result.Should().BeTrue();
        context.DeclaredOptions.Single().EffectiveValue.Should().Be("true");
    }

    [Fact]
    public void Declare_String_WithCachedValue_ReturnsCachedOverride()
    {
        var context = CreateContext(new Dictionary<string, string> { ["INSTALL_PREFIX"] = "/opt/custom" });

        var result = BuildOptions.Declare(context, "INSTALL_PREFIX", "Install prefix", "/usr/local");

        result.Should().Be("/opt/custom");
    }

    [Fact]
    public void Declare_Int_WithCachedValue_ParsesAndReturnsCachedOverride()
    {
        var context = CreateContext(new Dictionary<string, string> { ["MAX_WORKERS"] = "16" });

        var result = BuildOptions.Declare(context, "MAX_WORKERS", "Max worker threads", 4);

        result.Should().Be(16);
    }

    [Fact]
    public void Declare_Int_WithUnparsableCachedValue_FallsBackToDefault()
    {
        var context = CreateContext(new Dictionary<string, string> { ["MAX_WORKERS"] = "not-a-number" });

        var result = BuildOptions.Declare(context, "MAX_WORKERS", "Max worker threads", 4);

        result.Should().Be(4);
    }

    [Fact]
    public void DeclarePath_RecordsPathType()
    {
        var context = CreateContext();

        var result = BuildOptions.DeclarePath(context, "SDK_ROOT", "Path to the SDK", "/default/sdk");

        result.Should().Be("/default/sdk");
        context.DeclaredOptions.Single().Type.Should().Be(BuildOptionType.Path);
    }

    [Fact]
    public void Declare_SameNameTwice_FirstDeclarationWins()
    {
        var context = CreateContext();

        var first = BuildOptions.Declare(context, "SHARED_OPTION", "First description", true);
        var second = BuildOptions.Declare(context, "SHARED_OPTION", "Second description", false);

        first.Should().BeTrue();
        second.Should().BeTrue();
        context.DeclaredOptions.Should().ContainSingle();
        context.DeclaredOptions.Single().Description.Should().Be("First description");
    }
}
```

Create `tests/Omen.Core.Tests/OptionCacheStoreTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Options;

namespace Omen.Core.Tests;

public class OptionCacheStoreTests : IDisposable
{
    private readonly string _path;

    public OptionCacheStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(OptionCacheStoreTests), Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_FileDoesNotExist_ReturnsEmptyDictionary()
    {
        var store = new OptionCacheStore(_path);
        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new OptionCacheStore(_path);
        var values = new Dictionary<string, string> { ["ENABLE_FEATURE_X"] = "true", ["MAX_WORKERS"] = "8" };

        store.Save(values);
        var loaded = store.Load();

        loaded.Should().BeEquivalentTo(values);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsEmptyDictionaryInsteadOfThrowing()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(_path, "{ not valid json");

        var store = new OptionCacheStore(_path);

        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void Save_CreatesParentDirectoryIfMissing()
    {
        var store = new OptionCacheStore(_path);
        Directory.Exists(Path.GetDirectoryName(_path)).Should().BeFalse();

        store.Save(new Dictionary<string, string> { ["X"] = "1" });

        File.Exists(_path).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Omen.Core.Tests --filter BuildOptionsTests`
Run: `dotnet test tests/Omen.Core.Tests --filter OptionCacheStoreTests`
Expected: FAIL with compile errors — `BuildContext.CachedOptionValues`/`DeclaredOptions`, `BuildOptions`, `OptionCacheStore` don't exist yet.

- [ ] **Step 3: Add the new `BuildContext` properties**

In `src/Omen.Core/Configuration/BuildContext.cs`, add `using Omen.Core.Options;` at the top, and add after the existing `CoordinatorAddress` property (before `GetContextId()`):

```csharp
    /// <summary>
    /// Build options declared during rule instantiation (via BuildOptions.Declare), collected
    /// as a side effect of constructing TargetRules/ModuleRules/GemRules against this context -
    /// mirrors how CMake's option() calls register into its cache during Configure.
    /// </summary>
    public List<BuildOptionDeclaration> DeclaredOptions { get; init; } = [];

    /// <summary>
    /// Persisted option overrides (from a prior Configure), consulted by BuildOptions.Declare
    /// when a rules file declares an option - a name present here overrides that option's
    /// compiled-in default for this build.
    /// </summary>
    public IReadOnlyDictionary<string, string> CachedOptionValues { get; init; } = new Dictionary<string, string>();
```

- [ ] **Step 4: Implement `BuildOptions`**

Create `src/Omen.Core/Options/BuildOptions.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Options;

public enum BuildOptionType
{
    Bool,
    String,
    Int,
    Path
}

/// <summary>
/// A declared, user-configurable build option, as recorded onto BuildContext.DeclaredOptions
/// when a rules file calls BuildOptions.Declare - the GUI's Options panel and OptionsOrchestrator
/// both read this list, never a separate registry, so there is one source of truth for "what
/// options does this project have."
/// </summary>
public sealed class BuildOptionDeclaration
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required BuildOptionType Type { get; init; }
    public required string DefaultValue { get; init; }
    public required string EffectiveValue { get; init; }
}

/// <summary>
/// Declares a user-configurable build option from a rules file, in the spirit of CMake's
/// option()/set(... CACHE). A static entry point (rather than a method on ModuleRules/
/// TargetRules/GemRules) since those are three separate, unrelated base-class hierarchies -
/// this works identically from any of them, or from a plain rules file with no base class
/// dependency on this concept at all.
/// </summary>
public static class BuildOptions
{
    public static bool Declare(BuildContext context, string name, string description, bool defaultValue)
    {
        var effective = ResolveAndRecord(context, name, description, BuildOptionType.Bool, defaultValue ? "true" : "false");
        return effective.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static string Declare(BuildContext context, string name, string description, string defaultValue) =>
        ResolveAndRecord(context, name, description, BuildOptionType.String, defaultValue);

    public static int Declare(BuildContext context, string name, string description, int defaultValue)
    {
        var effective = ResolveAndRecord(context, name, description, BuildOptionType.Int, defaultValue.ToString());
        return int.TryParse(effective, out var parsed) ? parsed : defaultValue;
    }

    public static string DeclarePath(BuildContext context, string name, string description, string defaultValue) =>
        ResolveAndRecord(context, name, description, BuildOptionType.Path, defaultValue);

    private static string ResolveAndRecord(BuildContext context, string name, string description, BuildOptionType type, string defaultValue)
    {
        var existing = context.DeclaredOptions.FirstOrDefault(o => o.Name == name);
        if (existing != null)
            return existing.EffectiveValue;

        var effectiveValue = context.CachedOptionValues.TryGetValue(name, out var cached) ? cached : defaultValue;

        context.DeclaredOptions.Add(new BuildOptionDeclaration
        {
            Name = name,
            Description = description,
            Type = type,
            DefaultValue = defaultValue,
            EffectiveValue = effectiveValue
        });

        return effectiveValue;
    }
}
```

- [ ] **Step 5: Implement `OptionCacheStore`**

Create `src/Omen.Core/Options/OptionCacheStore.cs` (mirrors `src/Omen.Core/Graph/ActionDigestStore.cs`'s shape):

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;

namespace Omen.Core.Options;

/// <summary>
/// Persists edited build-option values across Configure runs - the Omen equivalent of
/// CMakeCache.txt, minus CMake's type-suffix-in-the-key convention (BuildOptionDeclaration
/// already carries the type, so the cache file itself only needs name -> string value).
/// </summary>
public sealed class OptionCacheStore(string path)
{
    public IReadOnlyDictionary<string, string> Load()
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public void Save(IReadOnlyDictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(values));
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Omen.Core.Tests --filter BuildOptionsTests`
Run: `dotnet test tests/Omen.Core.Tests --filter OptionCacheStoreTests`
Expected: both PASS.

- [ ] **Step 7: Run the full test suite and commit**

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS (this task only adds to `BuildContext`, doesn't change existing behavior — every other test should be unaffected).

```bash
git add src/Omen.Core/Options/BuildOptions.cs src/Omen.Core/Options/OptionCacheStore.cs src/Omen.Core/Configuration/BuildContext.cs tests/Omen.Core.Tests/BuildOptionsTests.cs tests/Omen.Core.Tests/OptionCacheStoreTests.cs
git commit -m "feat: add BuildOptions declaration API and OptionCacheStore"
```

---

## Task 2: `OptionsOrchestrator`

**Files:**
- Create: `src/Omen.Executors/Orchestration/OptionsOrchestrator.cs`
- Test: `tests/Omen.Executors.Tests/OptionsOrchestratorTests.cs`

**Interfaces:**
- Consumes: `BuildOptions`, `BuildOptionDeclaration`, `OptionCacheStore` (Task 1); `OrchestratorEvent`/`OrchestratorEventLevel` (existing).
- Produces: `OptionsOrchestratorRequest { string TargetFile }`, `OptionsOrchestrator.DiscoverAsync(OptionsOrchestratorRequest, IProgress<OrchestratorEvent>?, CancellationToken ct = default) -> Task<IReadOnlyList<BuildOptionDeclaration>?>` (null on rule-compilation failure), `OptionsOrchestrator.SaveOptions(string targetFile, IReadOnlyDictionary<string, string> values)`.

This reuses `BuildOrchestrator.BuildAsync`'s resolve-and-instantiate sequence (`src/Omen.Executors/Orchestration/BuildOrchestrator.cs`) up through `CreateTargetRules`/`CreateModuleRules`, then stops — no toolchain, no graph, no execution.

- [ ] **Step 1: Write the failing tests**

Create `tests/Omen.Executors.Tests/OptionsOrchestratorTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Omen.Executors.Tests --filter OptionsOrchestratorTests`
Expected: FAIL with a compile error — `OptionsOrchestrator`/`OptionsOrchestratorRequest` don't exist.

- [ ] **Step 3: Implement `OptionsOrchestrator`**

Create `src/Omen.Executors/Orchestration/OptionsOrchestrator.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Options;
using Omen.Core.Rules;

namespace Omen.Executors.Orchestration;

public sealed class OptionsOrchestratorRequest
{
    public required string TargetFile { get; init; }
}

/// <summary>
/// Discovers a project's declared build options without building anything - the Omen
/// equivalent of a CMake Configure pass. Reuses BuildOrchestrator's resolve-and-instantiate
/// sequence (target file -> rule compilation -> BuildContext -> CreateTargetRules/
/// CreateModuleRules) and stops there: instantiating rules is what runs BuildOptions.Declare
/// calls and populates BuildContext.DeclaredOptions, which is the entire discovery mechanism.
/// </summary>
public sealed class OptionsOrchestrator
{
    public async Task<IReadOnlyList<BuildOptionDeclaration>?> DiscoverAsync(
        OptionsOrchestratorRequest request,
        IProgress<OrchestratorEvent>? events,
        CancellationToken ct = default)
    {
        var workingDir = ResolveWorkingDir(request.TargetFile);
        var cacheStore = new OptionCacheStore(CachePath(workingDir));

        var context = new BuildContext
        {
            Platform = TargetPlatform.Windows,
            Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Development,
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate"),
            OutputDirectory = Path.Combine(workingDir, "Binaries"),
            CachedOptionValues = cacheStore.Load()
        };

        var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));

        CompiledRules compiledRules;
        try
        {
            events?.Report(new OrchestratorEvent("Compiling build rules...", OrchestratorEventLevel.Info));
            compiledRules = await ruleCompiler.CompileRulesAsync(workingDir, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            events?.Report(new OrchestratorEvent($"Error compiling rules: {ex.Message}", OrchestratorEventLevel.Error));
            return null;
        }

        compiledRules.CreateTargetRules(context);
        compiledRules.CreateModuleRules(context);

        events?.Report(new OrchestratorEvent($"Found {context.DeclaredOptions.Count} option(s)", OrchestratorEventLevel.Info));

        return context.DeclaredOptions;
    }

    public void SaveOptions(string targetFile, IReadOnlyDictionary<string, string> values)
    {
        var workingDir = ResolveWorkingDir(targetFile);
        new OptionCacheStore(CachePath(workingDir)).Save(values);
    }

    private static string ResolveWorkingDir(string targetFile) =>
        Path.GetDirectoryName(targetFile) ?? Path.GetPathRoot(targetFile) ?? targetFile;

    private static string CachePath(string workingDir) =>
        Path.Combine(workingDir, "Intermediate", "omen-cache.json");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Omen.Executors.Tests --filter OptionsOrchestratorTests`
Expected: PASS (4/4)

- [ ] **Step 5: Run the full test suite and commit**

Run: `dotnet test tests/Omen.Core.Tests && dotnet test tests/Omen.Executors.Tests`
Expected: both PASS.

```bash
git add src/Omen.Executors/Orchestration/OptionsOrchestrator.cs tests/Omen.Executors.Tests/OptionsOrchestratorTests.cs
git commit -m "feat: add OptionsOrchestrator for build-option discovery"
```

---

## Task 3: Wire the option cache into `BuildOrchestrator` and `ProjectGenerationOrchestrator`

**Files:**
- Modify: `src/Omen.Executors/Orchestration/BuildOrchestrator.cs`
- Modify: `src/Omen.Executors/Orchestration/ProjectGenerationOrchestrator.cs`
- Test: `tests/Omen.Executors.Tests/BuildOrchestratorTests.cs`

**Interfaces:**
- Consumes: `OptionCacheStore` (Task 1).
- Produces: no new public API — `BuildOrchestrator.BuildAsync` and `ProjectGenerationOrchestrator.GenerateAsync` now both load `Intermediate/omen-cache.json` into the `BuildContext` they construct.

This is what makes a Configure'd option value actually reach a real build — without this, `BuildOptions.Declare` calls during a real Build would always see an empty `CachedOptionValues` and only ever return defaults.

- [ ] **Step 1: Write the failing test**

Add to `tests/Omen.Executors.Tests/BuildOrchestratorTests.cs` (read the current file first — it already has several tests and a `CreateRequest` helper from earlier tasks; add this as a new `[Fact]` alongside them, don't restructure the file):

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Executors.Tests --filter BuildAsync_WithCachedOptionOverride_MakesItAvailableToRuleFiles`
Expected: FAIL — `recorded-value.txt` is written with `"False"` (the default), since `BuildOrchestrator` doesn't load the cache yet.

- [ ] **Step 3: Wire the cache into `BuildOrchestrator`**

In `src/Omen.Executors/Orchestration/BuildOrchestrator.cs`, add `using Omen.Core.Options;` to the top, and change the `context` construction (currently lines 46-55) to:

```csharp
        var optionCacheStore = new OptionCacheStore(Path.Combine(workingDir, "Intermediate", "omen-cache.json"));

        var context = new BuildContext
        {
            Platform = request.Platform,
            Architecture = request.Architecture,
            Configuration = request.Configuration,
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate", $"{request.Platform}_{request.Configuration}"),
            OutputDirectory = Path.Combine(workingDir, "Binaries", $"{request.Platform}_{request.Configuration}"),
            ParallelJobs = request.Jobs ?? Environment.ProcessorCount,
            CachedOptionValues = optionCacheStore.Load()
        };
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Omen.Executors.Tests --filter BuildAsync_WithCachedOptionOverride_MakesItAvailableToRuleFiles`
Expected: PASS

- [ ] **Step 5: Wire the cache into `ProjectGenerationOrchestrator`**

Read `src/Omen.Executors/Orchestration/ProjectGenerationOrchestrator.cs`'s current `GenerateAsync` method to confirm its `BuildContext` construction still matches the shape below (it hasn't been touched since it was written, but confirm before editing). Add `using Omen.Core.Options;` to the top, and change:

```csharp
        var context = new BuildContext
        {
            Platform = TargetPlatform.Windows,
            Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Development,
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate"),
            OutputDirectory = Path.Combine(workingDir, "Binaries")
        };
```

to:

```csharp
        var optionCacheStore = new OptionCacheStore(Path.Combine(workingDir, "Intermediate", "omen-cache.json"));

        var context = new BuildContext
        {
            Platform = TargetPlatform.Windows,
            Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Development,
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate"),
            OutputDirectory = Path.Combine(workingDir, "Binaries"),
            CachedOptionValues = optionCacheStore.Load()
        };
```

If the actual current code differs from this (e.g. different variable names), apply the same change in substance — load the cache, pass it as `CachedOptionValues` — rather than the literal diff if it doesn't match cleanly.

- [ ] **Step 6: Run the full test suite and commit**

Run: `dotnet test tests/Omen.Core.Tests && dotnet test tests/Omen.Executors.Tests`
Expected: both PASS.

```bash
git add src/Omen.Executors/Orchestration/BuildOrchestrator.cs src/Omen.Executors/Orchestration/ProjectGenerationOrchestrator.cs tests/Omen.Executors.Tests/BuildOrchestratorTests.cs
git commit -m "feat: load omen-cache.json into BuildContext for Build and Generate"
```

---

## Task 4: `OptionsPanelViewModel` and `BuildOptionViewModel`

**Files:**
- Create: `src/Omen.GUI/ViewModels/BuildOptionViewModel.cs`
- Create: `src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs`
- Modify: `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`

**Interfaces:**
- Consumes: `OptionsOrchestrator`, `OptionsOrchestratorRequest`, `BuildOptionDeclaration`, `BuildOptionType` (Tasks 1-2); `OrchestratorEvent` (existing).
- Produces: `BuildOptionViewModel { string Name, string Description, BuildOptionType Type, string Value, bool IsBool, bool IsString, bool IsInt, bool IsPath, bool? IsChecked, decimal? NumericValue }`, `OptionsPanelViewModel { ObservableCollection<BuildOptionViewModel> Options, bool IsExpanded, string StatusText, Task ConfigureAsync(string targetFile, IProgress<OrchestratorEvent>?) }`, `MainWindowViewModel.OptionsPanel : OptionsPanelViewModel`, `MainWindowViewModel.ConfigureOptionsAsync() -> Task`.

No dedicated unit tests for this task — `BuildOptionViewModel`'s type-conversion properties and `OptionsPanelViewModel`'s orchestration are thin, and this GUI's established convention (per the design spec) verifies the ViewModel/View layer by running the app, matching how `OutputLine`/`ProjectTreeNode` had no dedicated test files either. Task 8 verifies this end-to-end.

- [ ] **Step 1: Create `BuildOptionViewModel`**

Create `src/Omen.GUI/ViewModels/BuildOptionViewModel.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Core.Options;

namespace Omen.GUI.ViewModels;

/// <summary>
/// An editable wrapper around a discovered BuildOptionDeclaration. The underlying storage is
/// always a string (Value) - IsChecked/NumericValue are typed views onto it for the widgets
/// that need a bool?/decimal? rather than a string (CheckBox, NumericUpDown).
/// </summary>
public sealed partial class BuildOptionViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required BuildOptionType Type { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked))]
    [NotifyPropertyChangedFor(nameof(NumericValue))]
    private string _value = "";

    public bool IsBool => Type == BuildOptionType.Bool;
    public bool IsString => Type == BuildOptionType.String;
    public bool IsInt => Type == BuildOptionType.Int;
    public bool IsPath => Type == BuildOptionType.Path;

    public bool? IsChecked
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value == true ? "true" : "false";
    }

    public decimal? NumericValue
    {
        get => decimal.TryParse(Value, out var parsed) ? parsed : 0m;
        set => Value = ((long)(value ?? 0)).ToString();
    }
}
```

- [ ] **Step 2: Create `OptionsPanelViewModel`**

Create `src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Executors.Orchestration;

namespace Omen.GUI.ViewModels;

public sealed partial class OptionsPanelViewModel : ViewModelBase
{
    public ObservableCollection<BuildOptionViewModel> Options { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>
    /// Persists any current edits (if options were already discovered once) and re-runs
    /// discovery, mirroring cmake-gui's Configure button. The first-ever call for a project
    /// has nothing to save yet, so it only discovers.
    /// </summary>
    public async Task ConfigureAsync(string targetFile, IProgress<OrchestratorEvent>? events)
    {
        var orchestrator = new OptionsOrchestrator();

        if (Options.Count > 0)
        {
            var edited = Options.ToDictionary(o => o.Name, o => o.Value);
            orchestrator.SaveOptions(targetFile, edited);
        }

        var declarations = await orchestrator.DiscoverAsync(new OptionsOrchestratorRequest { TargetFile = targetFile }, events);
        if (declarations == null)
        {
            StatusText = "Configure failed";
            return;
        }

        Options.Clear();
        foreach (var declaration in declarations)
        {
            Options.Add(new BuildOptionViewModel
            {
                Name = declaration.Name,
                Description = declaration.Description,
                Type = declaration.Type,
                Value = declaration.EffectiveValue
            });
        }

        StatusText = Options.Count == 1 ? "1 option" : $"{Options.Count} options";
    }
}
```

- [ ] **Step 3: Wire into `MainWindowViewModel`**

In `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`, add `using Omen.Executors.Orchestration;` if not already present (it already is, from earlier tasks), and add after the `ProjectTreeRoots` property:

```csharp
    public OptionsPanelViewModel OptionsPanel { get; } = new();
```

Add after `CloseProject()`:

```csharp
    public async Task ConfigureOptionsAsync()
    {
        if (ProjectPath == null) return;

        var targetFile = Directory.GetFiles(ProjectPath, "*.target.cs", SearchOption.AllDirectories).FirstOrDefault();
        if (targetFile == null)
        {
            OptionsPanel.StatusText = "No .target.cs file found in this project.";
            return;
        }

        var eventsProgress = new Progress<OrchestratorEvent>(e => AppendLine(e.Message, e.Level));

        try
        {
            await OptionsPanel.ConfigureAsync(targetFile, eventsProgress);
        }
        catch (Exception ex)
        {
            OptionsPanel.StatusText = "Configure failed";
            AppendLine($"Unexpected error configuring options: {ex.Message}", OrchestratorEventLevel.Error);
        }
    }
```

In `LoadProject`, add a best-effort auto-configure after the existing body (after `_settings.Save();`):

```csharp
        _ = ConfigureOptionsAsync();
```

This is intentionally fire-and-forget: `LoadProject` is called synchronously from both the constructor and the Open Project click handler, and option discovery is a nice-to-have on open, not something that should block or fail project loading if it errors — `ConfigureOptionsAsync` already catches its own exceptions and reports them as an output line rather than throwing.

- [ ] **Step 4: Build and run a quick sanity check**

Run: `dotnet build Omen.sln`
Expected: 0 errors.

Run: `dotnet test tests/Omen.Core.Tests && dotnet test tests/Omen.Executors.Tests`
Expected: both PASS (this task doesn't touch either test project's production code).

- [ ] **Step 5: Commit**

```bash
git add src/Omen.GUI/ViewModels/BuildOptionViewModel.cs src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs src/Omen.GUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat: add OptionsPanelViewModel and wire auto-configure on project open"
```

---

## Task 5: Collapsible three-pane layout with the Options panel

**Files:**
- Modify: `src/Omen.GUI/Views/MainWindow.axaml`
- Modify: `src/Omen.GUI/Views/MainWindow.axaml.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.OptionsPanel` (Task 4), `BuildOptionViewModel` (Task 4).
- Produces: `MainWindow`'s Options pane UI, `OnToggleOptionsClick`, `OnConfigureOptionsClick`, `OnBrowsePathClick` handlers.

- [ ] **Step 1: Replace the Tree/Output `Grid` with a three-pane, splitter-separated layout**

In `src/Omen.GUI/Views/MainWindow.axaml`, replace the final `<Grid ColumnDefinitions="250,*">...</Grid>` block (the Tree/Output grid) with:

```xml
        <Grid ColumnDefinitions="250,4,*,4,Auto">
            <Border Grid.Column="0" Classes="pane" BorderThickness="0,0,1,0" BorderBrush="Gray">
                <DockPanel>
                    <TextBlock DockPanel.Dock="Top" Text="Project" Classes="paneHeader" />
                    <TreeView ItemsSource="{Binding ProjectTreeRoots}">
                        <TreeView.ItemTemplate>
                            <TreeDataTemplate ItemsSource="{Binding Children}">
                                <TextBlock Text="{Binding Name}" />
                            </TreeDataTemplate>
                        </TreeView.ItemTemplate>
                    </TreeView>
                </DockPanel>
            </Border>

            <GridSplitter Grid.Column="1" Width="4" Background="Transparent" ResizeDirection="Columns" />

            <DockPanel Grid.Column="2">
                <TextBlock DockPanel.Dock="Top" Text="Build Output" Classes="paneHeader" />
                <ListBox x:Name="OutputListBox" ItemsSource="{Binding OutputLines}" FontFamily="Cascadia Code,Consolas,monospace" FontSize="12" ScrollViewer.HorizontalScrollBarVisibility="Auto">
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <TextBlock Text="{Binding Text}" Foreground="{Binding Level, Converter={x:Static conv:OutputLevelToBrushConverter.Instance}}" TextWrapping="NoWrap" />
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
            </DockPanel>

            <GridSplitter Grid.Column="3" Width="4" Background="Transparent" ResizeDirection="Columns" IsVisible="{Binding OptionsPanel.IsExpanded}" />

            <Grid Grid.Column="4">
                <Button Content="⟨ Options ⟩" Click="OnToggleOptionsClick" IsVisible="{Binding !OptionsPanel.IsExpanded}" VerticalAlignment="Center">
                    <Button.RenderTransform>
                        <RotateTransform Angle="90" />
                    </Button.RenderTransform>
                </Button>

                <Border Width="280" Classes="pane" BorderThickness="1,0,0,0" BorderBrush="Gray" IsVisible="{Binding OptionsPanel.IsExpanded}">
                    <DockPanel>
                        <Grid DockPanel.Dock="Top" ColumnDefinitions="*,Auto">
                            <TextBlock Grid.Column="0" Text="Options" Classes="paneHeader" />
                            <Button Grid.Column="1" Content="⟨" Click="OnToggleOptionsClick" Margin="0,0,8,0" />
                        </Grid>

                        <StackPanel DockPanel.Dock="Bottom" Margin="8">
                            <TextBlock Text="{Binding OptionsPanel.StatusText}" Margin="0,0,0,6" FontSize="11" Opacity="0.8" />
                            <Button Content="Configure" Click="OnConfigureOptionsClick" HorizontalAlignment="Stretch" Classes="primary" />
                        </StackPanel>

                        <ScrollViewer>
                            <ItemsControl ItemsSource="{Binding OptionsPanel.Options}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <StackPanel Margin="8" Spacing="4">
                                            <TextBlock Text="{Binding Name}" FontWeight="Bold" />
                                            <TextBlock Text="{Binding Description}" FontSize="11" Opacity="0.7" TextWrapping="Wrap" />
                                            <CheckBox Content="Enabled" IsChecked="{Binding IsChecked}" IsVisible="{Binding IsBool}" />
                                            <TextBox Text="{Binding Value}" IsVisible="{Binding IsString}" />
                                            <NumericUpDown Value="{Binding NumericValue}" IsVisible="{Binding IsInt}" />
                                            <Grid ColumnDefinitions="*,Auto" IsVisible="{Binding IsPath}">
                                                <TextBox Grid.Column="0" Text="{Binding Value}" />
                                                <Button Grid.Column="1" Content="..." Click="OnBrowsePathClick" Tag="{Binding}" Margin="4,0,0,0" />
                                            </Grid>
                                        </StackPanel>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </ScrollViewer>
                    </DockPanel>
                </Border>
            </Grid>
        </Grid>
```

- [ ] **Step 2: Add the three new click handlers**

In `src/Omen.GUI/Views/MainWindow.axaml.cs`, add `using Avalonia.Platform.Storage;` if not already present (it should be, from Task 7 of the prior GUI plan), and add `using Omen.GUI.ViewModels;` if not present. Add these methods:

```csharp
    private void OnToggleOptionsClick(object? sender, RoutedEventArgs e) =>
        ViewModel.OptionsPanel.IsExpanded = !ViewModel.OptionsPanel.IsExpanded;

    private async void OnConfigureOptionsClick(object? sender, RoutedEventArgs e) =>
        await ViewModel.ConfigureOptionsAsync();

    private async void OnBrowsePathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BuildOptionViewModel option }) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Select value for {option.Name}",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.TryGetLocalPath() is { } path)
        {
            option.Value = path;
        }
    }
```

- [ ] **Step 3: Verify it launches**

Run: `dotnet run --project src/Omen.GUI`
Expected: window opens with three panes (Tree | Output | Options), separated by draggable splitters. The Options pane shows "No .target.cs file found in this project." if no project is open, or (once a project is opened) whatever options that project declares — none yet, since no rules file in `examples/GemSample` or `examples/ExampleGame` declares one until Task 8. Clicking the "⟨" button collapses the Options pane to a narrow vertical "⟨ Options ⟩" strip; clicking that expands it back. Close the window cleanly.

- [ ] **Step 4: Commit**

```bash
git add src/Omen.GUI/Views/MainWindow.axaml src/Omen.GUI/Views/MainWindow.axaml.cs
git commit -m "feat: collapsible three-pane layout with the Options panel"
```

---

## Task 6: "Signal" theme — colors, corner radius, section headers, status dot

**Files:**
- Create: `src/Omen.GUI/Styles/Signal.axaml`
- Modify: `src/Omen.GUI/App.axaml`
- Modify: `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.axaml`

**Interfaces:**
- Produces: `MainWindowViewModel.BuildState` (enum: `Idle`, `Building`, `Success`, `Failed`), `MainWindowViewModel.StatusDotBrush : IBrush` (computed).

`Avalonia.Fonts.Inter` is already applied app-wide via `Program.cs`'s existing `.WithInterFont()` call (from the prior GUI plan) — that already makes Inter the default `FontFamily` everywhere, so this task does not need to reference it again; it only adds color/shape/weight styling on top.

- [ ] **Step 1: Create the theme resource dictionary**

Create `src/Omen.GUI/Styles/Signal.axaml`:

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Style Selector="Window">
        <Setter Property="Background" Value="#10141A" />
    </Style>

    <Style Selector="Border.pane">
        <Setter Property="Background" Value="#181E27" />
        <Setter Property="CornerRadius" Value="8" />
    </Style>

    <Style Selector="TextBlock.paneHeader">
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Padding" Value="8,6" />
        <Setter Property="Foreground" Value="#A8C2D9" />
    </Style>

    <Style Selector="Button">
        <Setter Property="CornerRadius" Value="6" />
    </Style>

    <Style Selector="Button.primary">
        <Setter Property="Background" Value="#3FA9F5" />
        <Setter Property="Foreground" Value="#10141A" />
        <Setter Property="FontWeight" Value="SemiBold" />
    </Style>

    <Style Selector="Button.primary:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="#6EC1FF" />
    </Style>

    <Style Selector="Button.primary:disabled /template/ ContentPresenter">
        <Setter Property="Background" Value="#2A3542" />
    </Style>

</Styles>
```

- [ ] **Step 2: Merge it into `App.axaml`**

In `src/Omen.GUI/App.axaml`, change:

```xml
    <Application.Styles>
        <FluentTheme />
    </Application.Styles>
```

to:

```xml
    <Application.Styles>
        <FluentTheme />
        <StyleInclude Source="avares://Omen.GUI/Styles/Signal.axaml" />
    </Application.Styles>
```

- [ ] **Step 3: Add `BuildState` and `StatusDotBrush` to `MainWindowViewModel`**

In `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`, add `using Avalonia.Media;` to the top, and add near the top of the class (after the `_statusText` property):

```csharp
    public enum BuildState { Idle, Building, Success, Failed }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDotBrush))]
    private BuildState _currentBuildState = BuildState.Idle;

    public IBrush StatusDotBrush => CurrentBuildState switch
    {
        BuildState.Building => Brushes.DodgerBlue,
        BuildState.Success => Brushes.LimeGreen,
        BuildState.Failed => Brushes.OrangeRed,
        _ => Brushes.Gray
    };
```

`[ObservableProperty]` requires `CommunityToolkit.Mvvm.ComponentModel` (already imported in this file). Update every existing `StatusText =` assignment in `BuildAsync`, `RebuildAsync` (implicitly, via `BuildAsync`/`CleanAsync`), `CleanAsync`, and `GenerateProjectFilesAsync` to also set `CurrentBuildState` alongside it:

- `StatusText = "Building...";` (in `BuildAsync`) → also set `CurrentBuildState = BuildState.Building;` immediately before it.
- `StatusText = result?.Success == true ? "Build succeeded" : "Build failed";` → also set `CurrentBuildState = result?.Success == true ? BuildState.Success : BuildState.Failed;`.
- The `OperationCanceledException` catch's `StatusText = "Build cancelled";` → also set `CurrentBuildState = BuildState.Failed;` (cancelled reads as "not successful," matching the existing red-for-error visual language — there's no separate "cancelled" color in this pass).
- The generic-exception catch's `StatusText = "Build failed";` → also set `CurrentBuildState = BuildState.Failed;`.
- `CleanAsync`'s `StatusText = "Cleaning...";` → also set `CurrentBuildState = BuildState.Building;` (Clean uses the same "in progress" color as Build; there's no separate "cleaning" color).
- `CleanAsync`'s success `StatusText = $"Project: ...";` → also set `CurrentBuildState = BuildState.Success;`.
- `CleanAsync`'s failure `StatusText = "Clean failed";` → also set `CurrentBuildState = BuildState.Failed;`.
- `GenerateProjectFilesAsync`'s three `StatusText =` assignments (in-progress, success, failure) → the same pattern: Building/Success/Failed respectively.

- [ ] **Step 4: Add the status dot to the status bar**

In `src/Omen.GUI/Views/MainWindow.axaml`, replace:

```xml
                <TextBlock Grid.Column="0" Text="{Binding StatusText}" VerticalAlignment="Center" />
```

with:

```xml
                <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="6" VerticalAlignment="Center">
                    <Ellipse Width="8" Height="8" Fill="{Binding StatusDotBrush}" />
                    <TextBlock Text="{Binding StatusText}" VerticalAlignment="Center" />
                </StackPanel>
```

- [ ] **Step 5: Verify it launches and the theme is visible**

Run: `dotnet run --project src/Omen.GUI`
Expected: window background is dark slate (`#10141A`), the Tree/Output/Options panes have a slightly lighter rounded-corner background (`#181E27`), pane headers ("Project"/"Build Output"/"Options") show in a muted blue (`#A8C2D9`), the "Configure" button in the Options pane is styled blue (`Button.primary`), and a gray dot sits to the left of the status text (idle, no project's build ever run yet). Open a project and click Build: the dot turns blue while building, then green or red depending on the result. Close cleanly.

- [ ] **Step 6: Run the full test suite and commit**

Run: `dotnet build Omen.sln && dotnet test tests/Omen.Core.Tests && dotnet test tests/Omen.Executors.Tests`
Expected: build succeeds, both suites PASS.

```bash
git add src/Omen.GUI/Styles/Signal.axaml src/Omen.GUI/App.axaml src/Omen.GUI/ViewModels/MainWindowViewModel.cs src/Omen.GUI/Views/MainWindow.axaml
git commit -m "feat: Signal theme (colors, rounded panes, section headers, status dot)"
```

---

## Task 7: Toolbar icons

**Files:**
- Create: `src/Omen.GUI/Styles/Icons.axaml`
- Modify: `src/Omen.GUI/App.axaml`
- Modify: `src/Omen.GUI/Views/MainWindow.axaml`

**Interfaces:** none new — this only changes the toolbar buttons' visual content, not their bindings/handlers.

- [ ] **Step 1: Create the icon resource dictionary**

Create `src/Omen.GUI/Styles/Icons.axaml` (simple geometric glyphs — folder-open, play-triangle, refresh-arrows, trash-can, cancel-circle — no external asset files, no icon-font dependency):

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StreamGeometry x:Key="IconOpen">M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z</StreamGeometry>
    <StreamGeometry x:Key="IconBuild">M8,5.14V19.14L19,12.14L8,5.14Z</StreamGeometry>
    <StreamGeometry x:Key="IconRebuild">M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z</StreamGeometry>
    <StreamGeometry x:Key="IconClean">M19,3H14.82C14.4,1.84 13.3,1 12,1C10.7,1 9.6,1.84 9.18,3H5V5H19V3M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z</StreamGeometry>
    <StreamGeometry x:Key="IconCancel">M12,2C6.47,2 2,6.47 2,12C2,17.53 6.47,22 12,22C17.53,22 22,17.53 22,12C22,6.47 17.53,2 12,2M17,15.59L15.59,17L12,13.41L8.41,17L7,15.59L10.59,12L7,8.41L8.41,7L12,10.59L15.59,7L17,8.41L13.41,12L17,15.59Z</StreamGeometry>
</ResourceDictionary>
```

- [ ] **Step 2: Merge it into `App.axaml`**

In `src/Omen.GUI/App.axaml`, add an `Application.Resources` block (there isn't one yet) alongside the existing `Application.Styles`:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="Omen.GUI.App"
             RequestedThemeVariant="Dark">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceInclude Source="avares://Omen.GUI/Styles/Icons.axaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>

    <Application.Styles>
        <FluentTheme />
        <StyleInclude Source="avares://Omen.GUI/Styles/Signal.axaml" />
    </Application.Styles>
</Application>
```

- [ ] **Step 3: Apply icons to the toolbar buttons**

In `src/Omen.GUI/Views/MainWindow.axaml`, replace the toolbar's `StackPanel` (inside the `Border DockPanel.Dock="Top"`) buttons — `Open`, `Build`, `Rebuild`, `Clean`, `Cancel` — with icon+label versions:

```xml
                <Button Click="OnOpenProjectClick">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <PathIcon Data="{StaticResource IconOpen}" Width="14" Height="14" />
                        <TextBlock Text="Open" />
                    </StackPanel>
                </Button>
                <TextBlock Text="Platform:" VerticalAlignment="Center" />
                <ComboBox ItemsSource="{Binding AvailablePlatforms}" SelectedItem="{Binding SelectedPlatform}" MinWidth="100" />
                <TextBlock Text="Config:" VerticalAlignment="Center" />
                <ComboBox ItemsSource="{Binding Configurations}" SelectedItem="{Binding SelectedConfiguration}" MinWidth="100" />
                <Button Click="OnBuildClick" IsEnabled="{Binding !IsBuilding}" Classes="primary">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <PathIcon Data="{StaticResource IconBuild}" Width="14" Height="14" />
                        <TextBlock Text="Build" />
                    </StackPanel>
                </Button>
                <Button Click="OnRebuildClick" IsEnabled="{Binding !IsBuilding}">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <PathIcon Data="{StaticResource IconRebuild}" Width="14" Height="14" />
                        <TextBlock Text="Rebuild" />
                    </StackPanel>
                </Button>
                <Button Click="OnCleanClick" IsEnabled="{Binding !IsBuilding}">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <PathIcon Data="{StaticResource IconClean}" Width="14" Height="14" />
                        <TextBlock Text="Clean" />
                    </StackPanel>
                </Button>
                <Button Click="OnCancelClick" IsEnabled="{Binding IsBuilding}">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                        <PathIcon Data="{StaticResource IconCancel}" Width="14" Height="14" />
                        <TextBlock Text="Cancel" />
                    </StackPanel>
                </Button>
```

`PathIcon`'s `Foreground` defaults to inheriting from its container, so the `Build` button's `Classes="primary"` styling (dark icon/text on a blue background, per Task 6's `Button.primary` style) applies to its icon automatically — no extra `Foreground` setter needed.

- [ ] **Step 4: Verify it launches**

Run: `dotnet run --project src/Omen.GUI`
Expected: the toolbar's Open/Build/Rebuild/Clean/Cancel buttons each show a small icon to the left of their label. The Build button (styled `primary`) shows a dark play-triangle icon on its blue background; the others show light-colored icons on the default button background. Close cleanly.

- [ ] **Step 5: Commit**

```bash
git add src/Omen.GUI/Styles/Icons.axaml src/Omen.GUI/App.axaml src/Omen.GUI/Views/MainWindow.axaml
git commit -m "feat: add toolbar icons"
```

---

## Task 8: End-to-end verification

**Files:**
- Modify: `examples/GemSample/GemSample.target.cs` (or equivalent target file — read the current file first to confirm its exact name/location)

**Interfaces:** none new — this task adds one real declared option to a real sample project so there's something genuine to discover/edit/verify, and does a full-repo verification pass.

- [ ] **Step 1: Add a real declared option to `GemSample`**

Read `examples/GemSample`'s target file (created in an earlier session's plan) to find its exact current content, then add a `BuildOptions.Declare` call — for example, if the target class constructor currently just sets `Type`/`LaunchModuleName`/etc., add:

```csharp
using Omen.Core.Options;
```

to its usings, and inside the constructor:

```csharp
        var verboseGreeting = BuildOptions.Declare(context, "VERBOSE_GREETING", "Print an extended greeting from the Greeter gem", false);
        if (verboseGreeting)
        {
            GlobalDefinitions.Add("VERBOSE_GREETING=1");
        }
```

Adjust to fit the file's actual current structure (constructor body, existing `GlobalDefinitions`/other calls) rather than assuming an exact line number — the goal is one real, working declared option, not a specific diff shape.

- [ ] **Step 2: Full solution build and test suite**

Run: `dotnet build Omen.sln`
Expected: 0 errors.

Run: `dotnet test tests/Omen.Core.Tests && dotnet test tests/Omen.Executors.Tests`
Expected: both fully green.

- [ ] **Step 3: End-to-end verification against `GemSample`**

Run: `dotnet run --project src/Omen.GUI`

1. Open `examples/GemSample`. The Options pane should auto-populate showing `VERBOSE_GREETING` (unchecked, matching its `false` default) — this proves auto-discovery-on-open works, not just manual Configure.
2. Check the `VERBOSE_GREETING` checkbox, click Configure. The panel should show "1 option" and the checkbox should remain checked (confirms the round-trip: save → re-discover → still reflects the edit).
3. Click Build. Confirm in the output pane (or by inspecting the actual compile command in a subsequent manual `--dry-run` if useful) that the build actually ran with the option enabled — the simplest confirmation is that the build succeeds and, since `VERBOSE_GREETING=1` was added to `GlobalDefinitions`, every compiled source in the target now has that preprocessor definition. If you want stronger evidence, temporarily add a `#ifdef VERBOSE_GREETING` branch to one of `GemSample`'s source files that changes its printed output, rebuild, and confirm the printed output actually changed — then decide whether to keep that as a permanent, real demonstration in the sample or revert it to keep the sample minimal (either is acceptable; note your choice in the task report).
4. Close and reopen the GUI (or click Close Project then Open Project again). Confirm `VERBOSE_GREETING` is still checked — the cache persisted to `Intermediate/omen-cache.json` and survived.
5. Collapse and expand the Options panel via the toggle button. Confirm the edited (checked) state survives the collapse.

- [ ] **Step 4: Commit**

```bash
git add examples/GemSample/
git commit -m "feat: add a real declared build option to GemSample for end-to-end verification"
```

---

## Self-review notes

- **Spec coverage:** declaration API + persistence (Task 1), discovery orchestrator (Task 2), real-build wiring (Task 3), panel ViewModels (Task 4), collapsible layout (Task 5), Signal theme + status dot (Task 6), toolbar icons (Task 7), end-to-end proof (Task 8) — every component the design spec names has a task. The spec's non-goals (per-config caches, a generic option-widget plugin system, light theme, hand-edit-merge support) have no task, matching the spec.
- **Placeholder scan:** no step defers logic to prose. Task 3's Step 5 explicitly allows for the current `ProjectGenerationOrchestrator.cs` content to have drifted slightly and says what to do if so (apply the same change in substance) rather than silently assuming a diff that might not apply — that's a legitimate hedge against drift already observed multiple times in this codebase's history, not a vague instruction.
- **Type consistency:** `BuildOptionDeclaration`, `BuildOptionType`, `BuildOptions.Declare`/`DeclarePath`, `OptionCacheStore`, `OptionsOrchestratorRequest`, `BuildOptionViewModel`, `OptionsPanelViewModel` are used identically (same property names, same method signatures) across every task that references them, cross-checked against their Task 1-4 definitions while writing this plan.
