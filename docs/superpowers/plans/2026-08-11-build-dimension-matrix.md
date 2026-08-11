# N-Dimensional Build Configuration Matrix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Omen's hardcoded Platform/Architecture/Configuration triple with an open set of `[BuildDimension]` enums, resolved via an opt-in `SupportedTargets` matrix, so a target can declare it supports N combinations (including custom studio-defined dimensions) and one `omen build`/`generate`/GUI action can build or generate across the whole declared matrix, a CLI/GUI-narrowed subset, or (for a target that never opts in) exactly the single combination it resolves today.

**Architecture:** Three new core types (`BuildDimensionAttribute`, `ResolvedTarget`, `TargetMatrix`) live in `Omen.Core.Configuration` and know nothing about building. `BuildContext` gains a generic dimension accessor alongside its unchanged `Platform`/`Architecture`/`Configuration` properties. `TargetRules`/`GemRules`/`ModuleRules` gain an optional, context-free `static TargetMatrix SupportedTargets` convention read via reflection before any `BuildContext` exists. `BuildOrchestrator`/`ProjectGenerationOrchestrator` gain a resolve-then-loop step that runs their existing, unmodified single-`BuildContext` pipeline once per resolved combination — `ActionGraphBuilder`/`ParallelExecutor`/`ActionDigestStore` never learn the matrix exists.

**Tech Stack:** C#/.NET 8, xUnit + FluentAssertions + Moq, Avalonia 11.1.3 + CommunityToolkit.Mvvm, System.CommandLine (CLI).

## Global Constraints

- Backward compatibility is load-bearing: a `TargetRules`/`GemRules` that does not declare `SupportedTargets` MUST resolve to exactly the single combination the caller specifies (or the host-OS/`Development` default when nothing is specified) — zero expansion, identical to today's behavior. This is the acceptance bar for every task that touches the resolve path.
- `SupportedTargets` is declared only on `TargetRules`, `GemRules`, and `ModuleRules` — `ModuleRules`'s copy is a per-combination **membership gate** (is this module part of the action graph for this one already-resolved combination?), never an independent matrix to reconcile against its owning target's. See Task 3 and Task 5.
- `Exclude` predicates are evaluated after the full cartesian product of `.For<T>()` constraints (defaulting unconstrained **built-in** dimensions — `TargetPlatform`/`TargetArchitecture`/`BuildConfiguration` — to every value of that enum). Custom dimensions never auto-expand; they only appear in a `ResolvedTarget` if the target explicitly constrained them via `.For<T>()`.
- `omen build`/`generate` with no filter flags, on a target that declared a matrix, operates on the **whole matrix** by default (not a single combination) — this is the approved CLI default from the spec.
- Repo convention: every `.cs` file in `src/`/`tests/` carries the header:
  ```
  // Omen Build System
  // Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.
  ```
  (test-project files use `// Omen Build System - Unit Tests` on the first line, matching existing test files — copy the exact header from a neighboring file in the same test project rather than retyping it.)
- `Omen.Core.Tests`/`Omen.Executors.Tests` both have `GlobalUsings.cs` providing `Xunit`, `FluentAssertions`, `Moq`, `Omen.Core.Configuration`, `Omen.Core.Rules`, `Omen.Core.Graph`, `Omen.Core.Generators`, `Omen.Core.Interfaces`, `Omen.Core.Implementations` globally — do not re-add these as explicit `using`s in new test files. `Omen.Core.Options` and `Omen.Executors.Orchestration` are **not** global — add them explicitly where needed.
- A pre-existing, unrelated `TargetRules.SupportedPlatforms` (`List<TargetPlatform>`, defaults to Windows/Linux/FreeBSD/Android/iOS) already exists and is **dead** — nothing in the build pipeline reads it; it's only ever populated in `GenerateCommand.cs`'s scaffold-template string and asserted on by `TargetRulesTests.SupportedPlatforms_HasDefaultPlatforms`. It is a different, unrelated, pre-existing mechanism from the new `SupportedTargets` static property this plan adds — do not confuse the two, and do not modify `SupportedPlatforms` (out of scope; leave it and its test exactly as they are).

---

### Task 1: `[BuildDimension]`, `ResolvedTarget`, `TargetMatrix`

**Files:**
- Create: `src/Omen.Core/Configuration/BuildDimensionAttribute.cs`
- Create: `src/Omen.Core/Configuration/ResolvedTarget.cs`
- Create: `src/Omen.Core/Configuration/TargetMatrix.cs`
- Modify: `src/Omen.Core/Configuration/BuildEnums.cs`
- Test: `tests/Omen.Core.Tests/TargetMatrixTests.cs`

**Interfaces:**
- Produces: `BuildDimensionAttribute` (marker, `[AttributeUsage(AttributeTargets.Enum)]`); `ResolvedTarget` with `T Get<T>() where T : struct, Enum`, `bool TryGet<T>(out T value) where T : struct, Enum`, `IReadOnlyDictionary<Type, Enum> Values { get; }`; `TargetMatrix` with `static TargetMatrix Empty { get; }`, `TargetMatrix For<T>(params T[] values) where T : struct, Enum`, `TargetMatrix Exclude(Func<ResolvedTarget, bool> predicate)`, `IReadOnlyList<ResolvedTarget> Resolve()`.
- Consumes: nothing (foundational, no dependency on other tasks).

- [ ] **Step 1: Write the failing tests**

Create `tests/Omen.Core.Tests/TargetMatrixTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

public class TargetMatrixTests
{
    [Fact]
    public void Resolve_UnconstrainedBuiltInDimension_DefaultsToEveryEnumValue()
    {
        var matrix = TargetMatrix.Empty.For(BuildConfiguration.Debug);
        // Platform and Architecture never constrained.

        var resolved = matrix.Resolve();

        resolved.Should().HaveCount(Enum.GetValues<TargetPlatform>().Length * Enum.GetValues<TargetArchitecture>().Length);
        resolved.Should().OnlyContain(r => r.Get<BuildConfiguration>() == BuildConfiguration.Debug);
    }

    [Fact]
    public void Resolve_ConstrainedDimensions_ProducesExactCartesianProduct()
    {
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows, TargetPlatform.Linux)
            .For(TargetArchitecture.X64)
            .For(BuildConfiguration.Debug, BuildConfiguration.Release);

        var resolved = matrix.Resolve();

        resolved.Should().HaveCount(4);
        resolved.Should().Contain(r => r.Get<TargetPlatform>() == TargetPlatform.Windows && r.Get<BuildConfiguration>() == BuildConfiguration.Debug);
        resolved.Should().Contain(r => r.Get<TargetPlatform>() == TargetPlatform.Linux && r.Get<BuildConfiguration>() == BuildConfiguration.Release);
        resolved.Should().OnlyContain(r => r.Get<TargetArchitecture>() == TargetArchitecture.X64);
    }

    [Fact]
    public void Resolve_CustomDimensionNeverConstrained_DoesNotAppearInResolvedTarget()
    {
        var matrix = TargetMatrix.Empty.For(TargetPlatform.Windows);

        var resolved = matrix.Resolve();

        resolved.Should().OnlyContain(r => !r.TryGet<TestRenderApi>(out _));
    }

    [Fact]
    public void Resolve_CustomDimensionConstrained_AppearsInEveryResolvedTarget()
    {
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows)
            .For(TestRenderApi.DirectX12, TestRenderApi.Vulkan);

        var resolved = matrix.Resolve();

        resolved.Should().HaveCount(2);
        resolved.Should().Contain(r => r.Get<TestRenderApi>() == TestRenderApi.DirectX12);
        resolved.Should().Contain(r => r.Get<TestRenderApi>() == TestRenderApi.Vulkan);
    }

    [Fact]
    public void Exclude_DropsMatchingCombinationsOnly()
    {
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows, TargetPlatform.Linux)
            .For(TargetArchitecture.X64, TargetArchitecture.ARM64)
            .For(BuildConfiguration.Debug)
            .Exclude(t => t.Get<TargetPlatform>() == TargetPlatform.Windows && t.Get<TargetArchitecture>() == TargetArchitecture.ARM64);

        var resolved = matrix.Resolve();

        resolved.Should().HaveCount(3);
        resolved.Should().NotContain(r => r.Get<TargetPlatform>() == TargetPlatform.Windows && r.Get<TargetArchitecture>() == TargetArchitecture.ARM64);
    }

    [Fact]
    public void Exclude_MultiplePredicates_EachRemovesItsOwnMatches()
    {
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows, TargetPlatform.Linux)
            .For(BuildConfiguration.Debug, BuildConfiguration.Shipping)
            .Exclude(t => t.Get<TargetPlatform>() == TargetPlatform.Windows && t.Get<BuildConfiguration>() == BuildConfiguration.Shipping)
            .Exclude(t => t.Get<TargetPlatform>() == TargetPlatform.Linux && t.Get<BuildConfiguration>() == BuildConfiguration.Debug);

        var resolved = matrix.Resolve().Select(r => (r.Get<TargetPlatform>(), r.Get<TargetArchitecture>(), r.Get<BuildConfiguration>())).ToList();

        // 2 platforms x N architectures x 2 configs, minus the two excluded slices.
        var archCount = Enum.GetValues<TargetArchitecture>().Length;
        resolved.Should().HaveCount(2 * archCount);
        resolved.Where(r => r.Item1 == TargetPlatform.Windows).Should().OnlyContain(r => r.Item3 == BuildConfiguration.Debug);
        resolved.Where(r => r.Item1 == TargetPlatform.Linux).Should().OnlyContain(r => r.Item3 == BuildConfiguration.Shipping);
    }

    [Fact]
    public void Exclude_RemovesEveryCombination_ResolveReturnsEmpty()
    {
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows)
            .For(BuildConfiguration.Debug)
            .Exclude(_ => true);

        matrix.Resolve().Should().BeEmpty();
    }

    [Fact]
    public void For_ValueOfUnTaggedEnum_Throws()
    {
        var act = () => TargetMatrix.Empty.For(UntaggedEnum.A);

        act.Should().Throw<InvalidOperationException>().WithMessage("*BuildDimension*");
    }

    [Fact]
    public void For_EmptyValueArray_Throws()
    {
        var act = () => TargetMatrix.Empty.For(Array.Empty<TargetPlatform>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResolvedTarget_TryGet_UnresolvedDimension_ReturnsFalse()
    {
        var resolved = TargetMatrix.Empty.For(TargetPlatform.Windows).Resolve().Single();

        resolved.TryGet<TestRenderApi>(out var value).Should().BeFalse();
        value.Should().Be(default(TestRenderApi));
    }

    [Fact]
    public void ResolvedTarget_Get_UnresolvedDimension_Throws()
    {
        var resolved = TargetMatrix.Empty.For(TargetPlatform.Windows).Resolve().Single();

        var act = () => resolved.Get<TestRenderApi>();

        act.Should().Throw<InvalidOperationException>();
    }

    [BuildDimension]
    public enum TestRenderApi { DirectX12, Vulkan }

    public enum UntaggedEnum { A, B }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Omen.Core.Tests --filter "FullyQualifiedName~TargetMatrixTests"`
Expected: FAIL to compile — `BuildDimensionAttribute`/`ResolvedTarget`/`TargetMatrix` don't exist yet.

- [ ] **Step 3: Implement `BuildDimensionAttribute`**

Create `src/Omen.Core/Configuration/BuildDimensionAttribute.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Configuration;

/// <summary>
/// Marks an enum as a build-configuration dimension - an axis TargetMatrix.For&lt;T&gt;
/// can constrain and a ResolvedTarget/BuildContext can carry a value for. TargetPlatform,
/// TargetArchitecture, and BuildConfiguration carry this attribute as the three built-in
/// dimensions; a studio adds a custom axis (e.g. a RenderApi enum) by tagging its own enum
/// the same way - no core-repo edit required.
/// </summary>
[AttributeUsage(AttributeTargets.Enum)]
public sealed class BuildDimensionAttribute : Attribute;
```

- [ ] **Step 4: Implement `ResolvedTarget`**

Create `src/Omen.Core/Configuration/ResolvedTarget.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Configuration;

/// <summary>
/// One concrete point in a resolved TargetMatrix - one value per dimension type present
/// in this combination. Built by TargetMatrix.Resolve(); consumed by BuildOrchestrator/
/// ProjectGenerationOrchestrator to construct one BuildContext per combination.
/// </summary>
public sealed class ResolvedTarget
{
    private readonly IReadOnlyDictionary<Type, Enum> _values;

    internal ResolvedTarget(IReadOnlyDictionary<Type, Enum> values) => _values = values;

    /// <summary>Every dimension value this combination carries, keyed by dimension type.</summary>
    public IReadOnlyDictionary<Type, Enum> Values => _values;

    public T Get<T>() where T : struct, Enum
    {
        if (!TryGet<T>(out var value))
            throw new InvalidOperationException($"ResolvedTarget has no value for dimension '{typeof(T).Name}'.");
        return value;
    }

    public bool TryGet<T>(out T value) where T : struct, Enum
    {
        if (_values.TryGetValue(typeof(T), out var boxed))
        {
            value = (T)boxed;
            return true;
        }
        value = default;
        return false;
    }

    public override string ToString() =>
        string.Join(", ", _values.OrderBy(kv => kv.Key.Name).Select(kv => $"{kv.Key.Name}={kv.Value}"));
}
```

- [ ] **Step 5: Implement `TargetMatrix`**

Create `src/Omen.Core/Configuration/TargetMatrix.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Configuration;

/// <summary>
/// Builds the set of ResolvedTargets a TargetRules/GemRules/ModuleRules declares via its
/// optional static SupportedTargets property - Omen's equivalent of Sharpmake's fragment/
/// Target system. Immutable: every method returns a new TargetMatrix.
/// </summary>
public sealed class TargetMatrix
{
    private static readonly Type[] BuiltInDimensions =
        [typeof(TargetPlatform), typeof(TargetArchitecture), typeof(BuildConfiguration)];

    private readonly Dictionary<Type, Enum[]> _dimensions;
    private readonly List<Func<ResolvedTarget, bool>> _excludes;

    private TargetMatrix(Dictionary<Type, Enum[]> dimensions, List<Func<ResolvedTarget, bool>> excludes)
    {
        _dimensions = dimensions;
        _excludes = excludes;
    }

    public static TargetMatrix Empty => new([], []);

    /// <summary>Constrains dimension T to exactly these values. Calling For&lt;T&gt; a second
    /// time for the same T replaces its earlier constraint rather than adding to it.</summary>
    public TargetMatrix For<T>(params T[] values) where T : struct, Enum
    {
        if (typeof(T).GetCustomAttribute<BuildDimensionAttribute>() == null)
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' is not a [BuildDimension] enum and cannot be used in TargetMatrix.For<T>().");
        if (values.Length == 0)
            throw new ArgumentException("For<T> requires at least one value.", nameof(values));

        var dimensions = new Dictionary<Type, Enum[]>(_dimensions)
        {
            [typeof(T)] = values.Cast<Enum>().ToArray()
        };
        return new TargetMatrix(dimensions, _excludes);
    }

    /// <summary>Drops any resolved combination the predicate matches, after the full
    /// cartesian product is computed. Composes: multiple Exclude calls each remove their
    /// own matches.</summary>
    public TargetMatrix Exclude(Func<ResolvedTarget, bool> predicate)
    {
        var excludes = new List<Func<ResolvedTarget, bool>>(_excludes) { predicate };
        return new TargetMatrix(_dimensions, excludes);
    }

    /// <summary>Computes every resolved combination: the cartesian product across every
    /// constrained dimension, defaulting an unconstrained built-in dimension (Platform/
    /// Architecture/Configuration) to every value of that enum - a BuildContext always
    /// needs a value for all three. A custom dimension never auto-expands; it only appears
    /// in the result if this matrix explicitly constrained it via For&lt;T&gt;. Exclude
    /// predicates run last, in declaration order.</summary>
    public IReadOnlyList<ResolvedTarget> Resolve()
    {
        var dimensions = new Dictionary<Type, Enum[]>(_dimensions);
        foreach (var builtIn in BuiltInDimensions)
        {
            if (!dimensions.ContainsKey(builtIn))
                dimensions[builtIn] = Enum.GetValues(builtIn).Cast<Enum>().ToArray();
        }

        IEnumerable<IReadOnlyDictionary<Type, Enum>> combinations = [new Dictionary<Type, Enum>()];
        foreach (var (dimensionType, values) in dimensions)
        {
            combinations = combinations.SelectMany(prefix => values.Select(value =>
            {
                var next = new Dictionary<Type, Enum>(prefix) { [dimensionType] = value };
                return (IReadOnlyDictionary<Type, Enum>)next;
            }));
        }

        return combinations
            .Select(values => new ResolvedTarget(values))
            .Where(resolved => !_excludes.Any(exclude => exclude(resolved)))
            .ToList();
    }
}
```

Add `using System.Reflection;` to the top of `TargetMatrix.cs` for `GetCustomAttribute`.

- [ ] **Step 6: Tag the three built-in dimensions**

In `src/Omen.Core/Configuration/BuildEnums.cs`, add `[BuildDimension]` directly above each of the `TargetPlatform`, `TargetArchitecture`, and `BuildConfiguration` enum declarations (no other change to their bodies):

```csharp
[BuildDimension]
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

(and the same one-line addition above `TargetArchitecture` and `BuildConfiguration`.)

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/Omen.Core.Tests --filter "FullyQualifiedName~TargetMatrixTests"`
Expected: PASS, all 11 tests.

- [ ] **Step 8: Run the full Omen.Core.Tests suite and commit**

Run: `dotnet build Omen.sln && dotnet test tests/Omen.Core.Tests`
Expected: build succeeds, all tests (174 pre-existing + 11 new) PASS.

```bash
git add src/Omen.Core/Configuration/BuildDimensionAttribute.cs src/Omen.Core/Configuration/ResolvedTarget.cs src/Omen.Core/Configuration/TargetMatrix.cs src/Omen.Core/Configuration/BuildEnums.cs tests/Omen.Core.Tests/TargetMatrixTests.cs
git commit -m "feat: add BuildDimension attribute, ResolvedTarget, and TargetMatrix"
```

---

### Task 2: `BuildContext.GetDimension<T>()` and `AdditionalDimensions`

**Files:**
- Modify: `src/Omen.Core/Configuration/BuildContext.cs`
- Test: `tests/Omen.Core.Tests/BuildContextTests.cs`

**Interfaces:**
- Consumes: nothing new from Task 1 directly (works with any `[BuildDimension]` enum, doesn't reference `TargetMatrix`/`ResolvedTarget`).
- Produces: `BuildContext.AdditionalDimensions` (`IReadOnlyDictionary<Type, Enum>`, defaults to empty), `BuildContext.GetDimension<T>() where T : struct, Enum` — later tasks (5, 6) use this when constructing a `BuildContext` from a `ResolvedTarget` and when custom-dimension-aware code needs to read a value.

**`Platform`/`Architecture`/`Configuration` are NOT changed by this task** — they stay exactly as they are today (`required` settable properties). This is a deliberate simplification versus a "computed read-through property" design: it achieves identical external behavior (a generic per-dimension accessor) with zero risk to any of the ~13 existing `BuildContext` construction sites across the codebase, since none of them need to change at all.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Omen.Core.Tests/BuildContextTests.cs` (inside the existing `BuildContextTests` class, after the last test):

```csharp
[Fact]
public void GetDimension_BuiltInPlatform_ReturnsPlatformProperty()
{
    var context = CreateTestContext();

    context.GetDimension<TargetPlatform>().Should().Be(context.Platform);
}

[Fact]
public void GetDimension_BuiltInArchitecture_ReturnsArchitectureProperty()
{
    var context = CreateTestContext();

    context.GetDimension<TargetArchitecture>().Should().Be(context.Architecture);
}

[Fact]
public void GetDimension_BuiltInConfiguration_ReturnsConfigurationProperty()
{
    var context = CreateTestContext();

    context.GetDimension<BuildConfiguration>().Should().Be(context.Configuration);
}

[Fact]
public void GetDimension_CustomDimensionPresent_ReturnsItFromAdditionalDimensions()
{
    var context = new BuildContext
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = "/test",
        OutputDirectory = "/test/bin",
        IntermediateDirectory = "/test/obj",
        AdditionalDimensions = new Dictionary<Type, Enum> { [typeof(TestRenderApi)] = TestRenderApi.Vulkan }
    };

    context.GetDimension<TestRenderApi>().Should().Be(TestRenderApi.Vulkan);
}

[Fact]
public void GetDimension_DimensionNotPresent_Throws()
{
    var context = CreateTestContext();

    var act = () => context.GetDimension<TestRenderApi>();

    act.Should().Throw<InvalidOperationException>();
}

[Fact]
public void AdditionalDimensions_DefaultsToEmpty()
{
    var context = CreateTestContext();

    context.AdditionalDimensions.Should().NotBeNull();
    context.AdditionalDimensions.Should().BeEmpty();
}

[BuildDimension]
public enum TestRenderApi { DirectX12, Vulkan }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Omen.Core.Tests --filter "FullyQualifiedName~BuildContextTests"`
Expected: FAIL to compile — `GetDimension`/`AdditionalDimensions` don't exist yet.

- [ ] **Step 3: Implement**

In `src/Omen.Core/Configuration/BuildContext.cs`, add after the `CachedOptionValues` property (before `GetContextId()`):

```csharp
/// <summary>
/// Values for any dimension beyond the built-in Platform/Architecture/Configuration -
/// populated by BuildOrchestrator/ProjectGenerationOrchestrator when constructing a
/// BuildContext from a ResolvedTarget that carries a custom dimension. Read via
/// GetDimension&lt;T&gt;(), never directly, so built-in and custom dimensions share one API.
/// </summary>
public IReadOnlyDictionary<Type, Enum> AdditionalDimensions { get; init; } = new Dictionary<Type, Enum>();

/// <summary>
/// Reads this context's value for any [BuildDimension] enum - Platform/Architecture/
/// Configuration via their own properties, anything else via AdditionalDimensions.
/// </summary>
public T GetDimension<T>() where T : struct, Enum
{
    if (typeof(T) == typeof(TargetPlatform)) return (T)(object)Platform;
    if (typeof(T) == typeof(TargetArchitecture)) return (T)(object)Architecture;
    if (typeof(T) == typeof(BuildConfiguration)) return (T)(object)Configuration;
    if (AdditionalDimensions.TryGetValue(typeof(T), out var value)) return (T)value;
    throw new InvalidOperationException($"BuildContext has no value for dimension '{typeof(T).Name}'.");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Omen.Core.Tests --filter "FullyQualifiedName~BuildContextTests"`
Expected: PASS, all tests (pre-existing + 6 new).

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet build Omen.sln && dotnet test tests/Omen.Core.Tests`
Expected: build succeeds, all tests PASS.

```bash
git add src/Omen.Core/Configuration/BuildContext.cs tests/Omen.Core.Tests/BuildContextTests.cs
git commit -m "feat: add BuildContext.GetDimension<T>() for custom dimension values"
```

---

### Task 3: Context-free `SupportedTargets` reading on `CompiledRules`

**Files:**
- Modify: `src/Omen.Core/Rules/RuleCompiler.cs`
- Modify: `src/Omen.Core/Rules/TargetRules.cs`, `src/Omen.Core/Rules/GemRules.cs`, `src/Omen.Core/Rules/ModuleRules.cs` (doc comments only)
- Test: `tests/Omen.Core.Tests/CompiledRulesSupportedTargetsTests.cs`

**Interfaces:**
- Consumes: `TargetMatrix` (Task 1).
- Produces: `CompiledRules.FindTargetType() -> Type?` (the first non-abstract `TargetRules` subclass in the compiled assembly, or null); `CompiledRules.GetSupportedTargets(Type ruleType) -> TargetMatrix?` (reads a static `SupportedTargets` property via reflection without constructing an instance; null if the type doesn't declare one); `CompiledRules.Assembly` (`public`, matching every other member on this class — `internal` would not cross the `Omen.Core` → `Omen.Executors`/`Omen.CLI` assembly boundary these callers live on, and no `InternalsVisibleTo` is configured for either; exposes the private `_assembly` field the class already holds, needed by Task 7's CLI `--dim` name lookup, which has no other way to reach the compiled rules assembly). Task 5 (`BuildOrchestrator`), Task 6 (`ProjectGenerationOrchestrator`), Task 7 (CLI), and Task 9 (GUI matrix resolution) all call these.

- [ ] **Step 1: Write the failing tests**

Create `tests/Omen.Core.Tests/CompiledRulesSupportedTargetsTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

public class CompiledRulesSupportedTargetsTests : IDisposable
{
    private readonly string _projectRoot;

    public CompiledRulesSupportedTargetsTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(CompiledRulesSupportedTargetsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    [Fact]
    public async Task GetSupportedTargets_TargetDeclaresIt_ReturnsTheMatrix()
    {
        var targetFile = Path.Combine(_projectRoot, "WithMatrix.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class WithMatrixTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows, TargetPlatform.Linux)
                    .For(BuildConfiguration.Debug, BuildConfiguration.Release);

                public WithMatrixTarget(BuildContext context) : base(context) { }
            }
            """);
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "Intermediate", "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var targetType = compiledRules.FindTargetType();
        targetType.Should().NotBeNull();
        var matrix = compiledRules.GetSupportedTargets(targetType!);

        matrix.Should().NotBeNull();
        matrix!.Resolve().Should().HaveCount(4);
    }

    [Fact]
    public async Task GetSupportedTargets_TargetDoesNotDeclareIt_ReturnsNull()
    {
        var targetFile = Path.Combine(_projectRoot, "NoMatrix.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class NoMatrixTarget : TargetRules
            {
                public NoMatrixTarget(BuildContext context) : base(context) { }
            }
            """);
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "Intermediate", "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var targetType = compiledRules.FindTargetType();
        targetType.Should().NotBeNull();

        compiledRules.GetSupportedTargets(targetType!).Should().BeNull();
    }

    [Fact]
    public async Task FindTargetType_DoesNotRequireAnyBuildContext()
    {
        // Regression guard: FindTargetType must work purely via reflection over the
        // compiled assembly's types - no BuildContext, no instance construction. A target
        // whose constructor would throw for an unconfigured context proves this.
        var targetFile = Path.Combine(_projectRoot, "Throwing.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class ThrowingTarget : TargetRules
            {
                public ThrowingTarget(BuildContext context) : base(context)
                {
                    throw new InvalidOperationException("must never run during discovery");
                }
            }
            """);
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "Intermediate", "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var targetType = compiledRules.FindTargetType();

        targetType.Should().NotBeNull();
        targetType!.Name.Should().Be("ThrowingTarget");
    }

    [Fact]
    public async Task GetSupportedTargets_ModuleDeclaresIt_ReturnsTheMatrix()
    {
        var moduleFile = Path.Combine(_projectRoot, "WithMatrix.module.cs");
        File.WriteAllText(moduleFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class WithMatrixModule : ModuleRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty.For(TargetPlatform.Windows);

                public WithMatrixModule(BuildContext context) : base(context) { }
            }
            """);
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "Intermediate", "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var moduleType = compiledRules.CreateModuleRules(CreateContext()).Single(m => m.Name == "WithMatrixModule").GetType();
        var matrix = compiledRules.GetSupportedTargets(moduleType);

        matrix.Should().NotBeNull();
        matrix!.Resolve().Should().ContainSingle(r => r.Get<TargetPlatform>() == TargetPlatform.Windows);
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Omen.Core.Tests --filter "FullyQualifiedName~CompiledRulesSupportedTargetsTests"`
Expected: FAIL to compile — `FindTargetType`/`GetSupportedTargets` don't exist yet.

- [ ] **Step 3: Implement on `CompiledRules`**

In `src/Omen.Core/Rules/RuleCompiler.cs`, add to the `CompiledRules` class (after `CreateGemRules`, before `GetTarget`):

```csharp
/// <summary>
/// The compiled rules assembly, exposed for callers that need to reason about its types
/// without an instance - e.g. CompiledRules.FindTargetType() below, and the CLI's --dim
/// dimension-name lookup (DimensionLookup.FindDimensionType), which has no other way to
/// reach it. Public, not internal: Omen.Executors and Omen.CLI are separate assemblies
/// from Omen.Core with no InternalsVisibleTo configured between them.
/// </summary>
public Assembly Assembly => _assembly;

/// <summary>
/// Finds the first non-abstract TargetRules subclass in the compiled assembly, purely via
/// reflection over the Type - no BuildContext, no instance construction. This is what makes
/// reading SupportedTargets possible before any context exists to build one.
/// </summary>
public Type? FindTargetType()
{
    var targetType = typeof(TargetRules);
    return _assembly.GetTypes().FirstOrDefault(t => !t.IsAbstract && targetType.IsAssignableFrom(t));
}

/// <summary>
/// Reads a rule type's optional `static TargetMatrix SupportedTargets` property without
/// constructing an instance. Works for TargetRules, GemRules, and ModuleRules subclasses
/// alike (any type with a static property of that exact name and type). Returns null if the
/// type doesn't declare one - callers treat null as "no matrix, use today's single-context
/// behavior."
/// </summary>
public TargetMatrix? GetSupportedTargets(Type ruleType)
{
    var property = ruleType.GetProperty("SupportedTargets", BindingFlags.Public | BindingFlags.Static);
    if (property == null || property.PropertyType != typeof(TargetMatrix))
        return null;
    return (TargetMatrix?)property.GetValue(null);
}
```

Add `using Omen.Core.Configuration;` to the top of `RuleCompiler.cs` if not already present (it already imports `Omen.Core.Configuration` per its existing `BuildContext` usage — confirm before adding a duplicate).

- [ ] **Step 4: Document the convention on the three rule base classes**

In `src/Omen.Core/Rules/TargetRules.cs`, add a doc comment above the class declaration (do not add a base-class member — `SupportedTargets` is read via reflection, never called through the base type):

```csharp
/// <summary>
/// Base class for target definitions. Inherit from this class in your .target.cs files.
/// Inspired by Unreal Engine's TargetRules pattern.
///
/// A subclass may optionally declare `public static TargetMatrix SupportedTargets => ...`
/// to opt into building/generating across multiple Platform/Architecture/Configuration (and
/// any custom [BuildDimension]) combinations in one invocation - see TargetMatrix. Read via
/// reflection (RuleCompiler.CompiledRules.GetSupportedTargets) before any BuildContext
/// exists, since "which combinations exist" has to be known before one can be constructed. A
/// target that omits it builds/generates exactly the single combination the caller resolves,
/// unchanged from today.
/// </summary>
public abstract class TargetRules
```

Add the equivalent paragraph to `GemRules.cs`'s class doc comment (same wording, "target definitions" → "gem build description").

In `src/Omen.Core/Rules/ModuleRules.cs`, add to the class doc comment instead:

```csharp
/// <summary>
/// Base class for module definitions. Inherit from this class in your .module.cs files.
/// Inspired by Unreal Engine's ModuleRules pattern.
///
/// A subclass may optionally declare `public static TargetMatrix SupportedTargets => ...`,
/// but unlike TargetRules/GemRules this is never resolved into a matrix of its own - it's a
/// per-combination membership gate. For each ResolvedTarget the owning target resolves to,
/// BuildOrchestrator checks whether this module's SupportedTargets (if declared) contains
/// that combination; if not, the module is silently excluded from that combination's action
/// graph only (present in others where it's included). A module with no SupportedTargets
/// participates in every combination, unchanged from today.
/// </summary>
public abstract class ModuleRules
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Omen.Core.Tests --filter "FullyQualifiedName~CompiledRulesSupportedTargetsTests"`
Expected: PASS, all 4 tests.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet build Omen.sln && dotnet test tests/Omen.Core.Tests`
Expected: build succeeds, all tests PASS.

```bash
git add src/Omen.Core/Rules/RuleCompiler.cs src/Omen.Core/Rules/TargetRules.cs src/Omen.Core/Rules/GemRules.cs src/Omen.Core/Rules/ModuleRules.cs tests/Omen.Core.Tests/CompiledRulesSupportedTargetsTests.cs
git commit -m "feat: read SupportedTargets from TargetRules/GemRules/ModuleRules without a BuildContext"
```

---

### Task 4: `DimensionLookup` — string-to-dimension resolution for CLI/GUI filters

**Files:**
- Create: `src/Omen.Core/Configuration/DimensionLookup.cs`
- Test: `tests/Omen.Core.Tests/DimensionLookupTests.cs`

**Interfaces:**
- Consumes: `BuildDimensionAttribute` (Task 1).
- Produces: `DimensionLookup.FindDimensionType(Assembly rulesAssembly, string name) -> Type?` (case-insensitive match against every `[BuildDimension]`-tagged enum in `Omen.Core`'s own assembly plus the given rules assembly); `DimensionLookup.TryParseValue(Type dimensionType, string valueName, out Enum? value) -> bool`. Task 7 (CLI `--dim`) and Task 9 (GUI, if it ever needs name-based lookup) consume these.

- [ ] **Step 1: Write the failing tests**

Create `tests/Omen.Core.Tests/DimensionLookupTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Reflection;

namespace Omen.Core.Tests;

public class DimensionLookupTests
{
    [Theory]
    [InlineData("TargetPlatform")]
    [InlineData("targetplatform")]
    [InlineData("PLATFORM".Length == 8 ? "TargetPlatform" : "TargetPlatform")]
    public void FindDimensionType_BuiltInDimension_IsCaseInsensitive(string name)
    {
        var result = DimensionLookup.FindDimensionType(typeof(DimensionLookupTests).Assembly, name);

        result.Should().Be(typeof(TargetPlatform));
    }

    [Fact]
    public void FindDimensionType_CustomDimensionInGivenAssembly_IsFound()
    {
        var result = DimensionLookup.FindDimensionType(typeof(DimensionLookupTests).Assembly, nameof(TestRenderApi));

        result.Should().Be(typeof(TestRenderApi));
    }

    [Fact]
    public void FindDimensionType_UnknownName_ReturnsNull()
    {
        DimensionLookup.FindDimensionType(typeof(DimensionLookupTests).Assembly, "NoSuchDimension").Should().BeNull();
    }

    [Fact]
    public void FindDimensionType_UnTaggedEnum_IsNotFound()
    {
        DimensionLookup.FindDimensionType(typeof(DimensionLookupTests).Assembly, nameof(UntaggedEnum)).Should().BeNull();
    }

    [Fact]
    public void TryParseValue_ValidValue_ReturnsTrue()
    {
        var success = DimensionLookup.TryParseValue(typeof(TargetPlatform), "Windows", out var value);

        success.Should().BeTrue();
        value.Should().Be(TargetPlatform.Windows);
    }

    [Fact]
    public void TryParseValue_ValidValue_IsCaseInsensitive()
    {
        var success = DimensionLookup.TryParseValue(typeof(TargetPlatform), "windows", out var value);

        success.Should().BeTrue();
        value.Should().Be(TargetPlatform.Windows);
    }

    [Fact]
    public void TryParseValue_InvalidValue_ReturnsFalse()
    {
        var success = DimensionLookup.TryParseValue(typeof(TargetPlatform), "NotAPlatform", out var value);

        success.Should().BeFalse();
        value.Should().BeNull();
    }

    [BuildDimension]
    public enum TestRenderApi { DirectX12, Vulkan }

    public enum UntaggedEnum { A, B }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Omen.Core.Tests --filter "FullyQualifiedName~DimensionLookupTests"`
Expected: FAIL to compile — `DimensionLookup` doesn't exist yet.

- [ ] **Step 3: Implement**

Create `src/Omen.Core/Configuration/DimensionLookup.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Reflection;

namespace Omen.Core.Configuration;

/// <summary>
/// Resolves a dimension by name (e.g. from a CLI --dim RenderApi=DirectX12 flag) to its
/// Type and a parsed value - on-demand lookup, not a persistent registry, since the only
/// consumer that needs a string-to-Type mapping is the CLI/GUI filter-parsing layer.
/// TargetMatrix.Resolve() itself never needs this: it works directly with the Type objects
/// a rules file's own For&lt;T&gt; calls already reference.
/// </summary>
public static class DimensionLookup
{
    /// <summary>
    /// Scans Omen.Core's own assembly (the three built-in dimensions) plus the given rules
    /// assembly (any studio-defined custom dimensions) for a [BuildDimension] enum whose
    /// name matches, case-insensitively.
    /// </summary>
    public static Type? FindDimensionType(Assembly rulesAssembly, string name)
    {
        var candidates = typeof(BuildDimensionAttribute).Assembly.GetTypes()
            .Concat(rulesAssembly.GetTypes());

        return candidates.FirstOrDefault(t =>
            t.IsEnum
            && t.GetCustomAttribute<BuildDimensionAttribute>() != null
            && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Case-insensitive Enum.Parse against the given dimension type.</summary>
    public static bool TryParseValue(Type dimensionType, string valueName, out Enum? value)
    {
        if (Enum.TryParse(dimensionType, valueName, ignoreCase: true, out var parsed) && parsed is Enum enumValue)
        {
            value = enumValue;
            return true;
        }
        value = null;
        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Omen.Core.Tests --filter "FullyQualifiedName~DimensionLookupTests"`
Expected: PASS, all 8 tests.

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet build Omen.sln && dotnet test tests/Omen.Core.Tests`
Expected: build succeeds, all tests PASS.

```bash
git add src/Omen.Core/Configuration/DimensionLookup.cs tests/Omen.Core.Tests/DimensionLookupTests.cs
git commit -m "feat: add DimensionLookup for CLI/GUI dimension-name resolution"
```

---

### Task 5: `BuildOrchestrator` resolve-then-loop with module gating

**Files:**
- Modify: `src/Omen.Executors/Orchestration/BuildOrchestrator.cs`
- Test: `tests/Omen.Executors.Tests/BuildOrchestratorTests.cs`

**Interfaces:**
- Consumes: `TargetMatrix`/`ResolvedTarget` (Task 1), `BuildContext.GetDimension<T>()`/`AdditionalDimensions` (Task 2), `CompiledRules.FindTargetType()`/`GetSupportedTargets()` (Task 3).
- Produces: `BuildOrchestratorRequest.Platform`/`.Architecture`/`.Configuration` become nullable (`TargetPlatform?`/`TargetArchitecture?`/`BuildConfiguration?`); new `BuildOrchestratorRequest.DimensionFilter { get; init; }` of type `IReadOnlyDictionary<Type, IReadOnlyList<Enum>>?` (null = no custom-dimension filter). `BuildOrchestrator.BuildAsync`'s signature and return type (`Task<BuildResult?>`) are unchanged — a multi-combination build still returns one aggregated `BuildResult` (`Success` = every combination succeeded; numeric fields summed; `ActionResults`/`OutputFiles` concatenated). Task 7 (CLI) and Task 9 (GUI) both construct requests against this shape.

Read `src/Omen.Executors/Orchestration/BuildOrchestrator.cs` in full before editing — this task restructures its body into a resolve step plus a per-combination method, reusing the existing single-context logic almost verbatim rather than rewriting it.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Omen.Executors.Tests/BuildOrchestratorTests.cs` (the file already has a `WriteTargetWithOption`-style helper pattern and a `CreateRequest` helper — add these as new methods in the existing `BuildOrchestratorTests` class):

```csharp
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

    var result = await orchestrator.BuildAsync(
        CreateRequest(Path.Combine(_projectRoot, "App.target.cs")),
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

    // No source files exist, so every resolved combination hits the zero-action success
    // path - filtering down to exactly one combination means exactly one "Nothing to
    // build" event instead of two.
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
                Type = TargetType.Executable;
                ExtraModules.Add("WinOnlyModule");
                UsePCHFiles = false;
                UseUnityBuild = false;
            }
        }
        """);
    var orchestrator = new BuildOrchestrator();
    var events = new List<OrchestratorEvent>();

    var result = await orchestrator.BuildAsync(
        CreateRequest(Path.Combine(_projectRoot, "Gated.target.cs")),
        new Progress<OrchestratorEvent>(events.Add),
        buildProgress: null);

    result.Should().NotBeNull();
    result!.Success.Should().BeTrue();
    // Windows combination compiles WinOnly.cpp; Linux combination has zero actions since
    // its only module was gated out.
    File.Exists(Path.Combine(_projectRoot, "Intermediate", "Windows_Debug", "WinOnly", "WinOnly.obj")).Should().BeTrue();
    Directory.Exists(Path.Combine(_projectRoot, "Intermediate", "Linux_Debug", "WinOnly")).Should().BeFalse();
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

    var result = await orchestrator.BuildAsync(
        CreateRequest(Path.Combine(_projectRoot, "App.target.cs")),
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Omen.Executors.Tests --filter "FullyQualifiedName~BuildOrchestratorTests"`
Expected: FAIL — `BuildOrchestratorRequest.Platform` is currently `required`, not assignable as omitted; `SupportedTargets`/matrix behavior doesn't exist yet.

- [ ] **Step 3: Make the request fields nullable and add `DimensionFilter`**

In `src/Omen.Executors/Orchestration/BuildOrchestrator.cs`, change `BuildOrchestratorRequest`:

```csharp
public sealed class BuildOrchestratorRequest
{
    public required string TargetFile { get; init; }
    public TargetPlatform? Platform { get; init; }
    public TargetArchitecture? Architecture { get; init; }
    public BuildConfiguration? Configuration { get; init; }
    /// <summary>Allowed values per custom dimension - a dimension absent from this map is
    /// unconstrained. Null means no custom-dimension filter at all.</summary>
    public IReadOnlyDictionary<Type, IReadOnlyList<Enum>>? DimensionFilter { get; init; }
    public int? Jobs { get; init; }
}
```

(`CreateRequest` in the test file's helper already assigns concrete, non-null values to `Platform`/`Architecture`/`Configuration` — this compiles unchanged since assigning a value to a nullable property is always valid.)

- [ ] **Step 4: Extract the existing single-context body into a private method**

Rename the current body of `BuildAsync` (everything from `var digestCalculator = new Sha256DigestCalculator();` through the `return result;` at the end, per the file as it stands after the Options plan's fixes) into a new private method with this shape, unchanged in substance:

```csharp
private async Task<BuildResult?> BuildOneCombinationAsync(
    BuildContext context,
    TargetRules targetRules,
    IReadOnlyList<ModuleRules> modules,
    int? jobs,
    IProgress<OrchestratorEvent>? events,
    IProgress<BuildProgress>? buildProgress,
    CancellationToken ct)
{
    // ... exact existing body: layering validation is done by the caller before this is
    // invoked (see Step 5) since it needs the pre-gating module list; this method starts
    // from "var digestCalculator = new Sha256DigestCalculator();" and is otherwise
    // byte-for-byte what BuildAsync's body already does today.
}
```

- [ ] **Step 5: Rewrite `BuildAsync` as resolve-then-loop**

Replace `BuildAsync`'s new (post-extraction) body with:

```csharp
public async Task<BuildResult?> BuildAsync(
    BuildOrchestratorRequest request,
    IProgress<OrchestratorEvent>? events,
    IProgress<BuildProgress>? buildProgress,
    CancellationToken ct = default)
{
    var workingDir = Path.GetDirectoryName(request.TargetFile) ?? Path.GetPathRoot(request.TargetFile) ?? request.TargetFile;
    var optionCacheStore = new OptionCacheStore(Path.Combine(workingDir, "Intermediate", "omen-cache.json"));
    var cachedOptionValues = optionCacheStore.Load();

    var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));
    CompiledRules compiledRules;
    try
    {
        events?.Report(new OrchestratorEvent("Compiling build rules...", OrchestratorEventLevel.Info));
        compiledRules = await ruleCompiler.CompileRulesAsync(workingDir, ct);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        events?.Report(new OrchestratorEvent($"Error compiling rules: {ex.Message}", OrchestratorEventLevel.Error));
        return null;
    }

    var targetType = compiledRules.FindTargetType();
    if (targetType == null)
    {
        events?.Report(new OrchestratorEvent("No target found.", OrchestratorEventLevel.Error));
        return null;
    }

    var matrix = compiledRules.GetSupportedTargets(targetType);
    IReadOnlyList<ResolvedTarget> resolvedTargets;

    if (matrix == null)
    {
        // Today's exact behavior: one combination, defaulted the same way BuildCommand's
        // ParsePlatform/ParseArchitecture/ParseConfiguration always have.
        var platform = request.Platform ?? DefaultPlatformForHostOS();
        var architecture = request.Architecture ?? DefaultArchitectureForHostOS();
        var configuration = request.Configuration ?? BuildConfiguration.Development;
        resolvedTargets = [new ResolvedTarget(new Dictionary<Type, Enum>
        {
            [typeof(TargetPlatform)] = platform,
            [typeof(TargetArchitecture)] = architecture,
            [typeof(BuildConfiguration)] = configuration
        })];
    }
    else
    {
        var all = matrix.Resolve();
        if (all.Count == 0)
        {
            events?.Report(new OrchestratorEvent(
                $"'{targetType.Name}'s SupportedTargets matrix resolved to zero combinations - check its Exclude rules.",
                OrchestratorEventLevel.Error));
            return null;
        }

        resolvedTargets = all.Where(rt => MatchesFilter(rt, request)).ToList();
        if (resolvedTargets.Count == 0)
        {
            events?.Report(new OrchestratorEvent("No targets matched your filter.", OrchestratorEventLevel.Error));
            return null;
        }
    }

    var results = new List<BuildResult>();
    foreach (var resolved in resolvedTargets)
    {
        if (resolvedTargets.Count > 1)
            events?.Report(new OrchestratorEvent($"=== Building {resolved} ===", OrchestratorEventLevel.Info));

        var platform = resolved.Get<TargetPlatform>();
        var configuration = resolved.Get<BuildConfiguration>();
        var suffix = string.Join("_", resolved.Values
            .Where(kv => kv.Key != typeof(TargetPlatform) && kv.Key != typeof(BuildConfiguration))
            .OrderBy(kv => kv.Key.Name)
            .Select(kv => kv.Value.ToString()));
        var dirName = suffix.Length > 0 ? $"{platform}_{configuration}_{suffix}" : $"{platform}_{configuration}";

        var context = new BuildContext
        {
            Platform = platform,
            Architecture = resolved.Get<TargetArchitecture>(),
            Configuration = configuration,
            AdditionalDimensions = resolved.Values
                .Where(kv => kv.Key != typeof(TargetPlatform) && kv.Key != typeof(TargetArchitecture) && kv.Key != typeof(BuildConfiguration))
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate", dirName),
            OutputDirectory = Path.Combine(workingDir, "Binaries", dirName),
            ParallelJobs = request.Jobs ?? Environment.ProcessorCount,
            CachedOptionValues = cachedOptionValues
        };

        var sdk = PlatformFactory.GetSDK(context.Platform);
        if (sdk == null)
        {
            events?.Report(new OrchestratorEvent($"No SDK found for platform {context.Platform}", OrchestratorEventLevel.Error));
            return null;
        }
        var toolchain = PlatformFactory.CreateToolchain(context.Platform, context.Architecture);
        if (toolchain == null)
        {
            events?.Report(new OrchestratorEvent($"Could not create toolchain for {context.Platform}/{context.Architecture}", OrchestratorEventLevel.Error));
            return null;
        }

        var targets = compiledRules.CreateTargetRules(context);
        var targetRules = targets.FirstOrDefault();
        if (targetRules == null)
        {
            events?.Report(new OrchestratorEvent("No target found.", OrchestratorEventLevel.Error));
            return null;
        }

        var modules = compiledRules.CreateModuleRules(context);
        // Gating is always silent, never an error - even when the gated-out module is the
        // one LaunchModuleName names. Most real dependencies are referenced unconditionally
        // (ExtraModules.Add with no platform check), so a module reference is never a
        // promise that the module builds everywhere; a combination left with zero included
        // modules this way falls through to the existing "no modules to build" warning path
        // a few lines below, exactly as it already does for a target with no modules at all.
        var includedModules = modules.Where(m => IsModuleIncluded(compiledRules, m, resolved)).ToList();

        try
        {
            LayeringValidator.Validate(includedModules);
        }
        catch (LayeringViolationException ex)
        {
            events?.Report(new OrchestratorEvent($"Layering violation: {ex.Message}", OrchestratorEventLevel.Error));
            return null;
        }

        events?.Report(new OrchestratorEvent($"Found 1 target(s), {includedModules.Count} module(s)", OrchestratorEventLevel.Info));

        if (includedModules.Count == 0)
        {
            events?.Report(new OrchestratorEvent("No modules to build.", OrchestratorEventLevel.Warning));
            results.Add(ZeroActionResult());
            continue;
        }

        var combinationResult = await BuildOneCombinationAsync(context, targetRules, includedModules, request.Jobs, events, buildProgress, ct);
        if (combinationResult == null)
            return null;
        results.Add(combinationResult);
    }

    return AggregateResults(results);
}

private static bool IsModuleIncluded(CompiledRules compiledRules, ModuleRules module, ResolvedTarget resolved)
{
    var moduleMatrix = compiledRules.GetSupportedTargets(module.GetType());
    if (moduleMatrix == null) return true; // no gate declared - participates in everything, as today.

    return moduleMatrix.Resolve().Any(candidate =>
        candidate.Values.All(kv => resolved.TryGet(kv.Key, out var value) && Equals(value, kv.Value)));
}

private static bool MatchesFilter(ResolvedTarget resolved, BuildOrchestratorRequest request)
{
    if (request.Platform is { } p && (!resolved.TryGet<TargetPlatform>(out var rp) || rp != p)) return false;
    if (request.Architecture is { } a && (!resolved.TryGet<TargetArchitecture>(out var ra) || ra != a)) return false;
    if (request.Configuration is { } c && (!resolved.TryGet<BuildConfiguration>(out var rc) || rc != c)) return false;
    if (request.DimensionFilter == null) return true;

    foreach (var (dimensionType, allowedValues) in request.DimensionFilter)
    {
        if (resolved.Values.TryGetValue(dimensionType, out var value) && !allowedValues.Contains(value))
            return false;
    }
    return true;
}

private static BuildResult AggregateResults(IReadOnlyList<BuildResult> results) => results.Count == 1
    ? results[0]
    : new BuildResult
    {
        Success = results.All(r => r.Success),
        TotalDuration = TimeSpan.FromTicks(results.Sum(r => r.TotalDuration.Ticks)),
        TotalActions = results.Sum(r => r.TotalActions),
        SuccessfulActions = results.Sum(r => r.SuccessfulActions),
        FailedActions = results.Sum(r => r.FailedActions),
        SkippedActions = results.Sum(r => r.SkippedActions),
        CachedActions = results.Sum(r => r.CachedActions),
        ActionResults = results.SelectMany(r => r.ActionResults).ToList(),
        OutputFiles = results.SelectMany(r => r.OutputFiles).ToList()
    };

private static TargetPlatform DefaultPlatformForHostOS()
{
    if (OperatingSystem.IsWindows()) return TargetPlatform.Windows;
    if (OperatingSystem.IsLinux()) return TargetPlatform.Linux;
    if (OperatingSystem.IsMacOS()) return TargetPlatform.iOS;
    if (OperatingSystem.IsFreeBSD()) return TargetPlatform.FreeBSD;
    return TargetPlatform.Windows;
}

private static TargetArchitecture DefaultArchitectureForHostOS() =>
    System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
        System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
        System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.ARM64,
        _ => TargetArchitecture.X64
    };
```

Delete the illustrative `gatedOutRequiredModule`/`IsSubsetMatch` lines above (they were left in as a thinking-trace in this plan text only, showing the reasoning before landing on `IsModuleIncluded` — the real code uses only `includedModules`/`excludedRequired`/`IsModuleIncluded`; do not transcribe the illustrative lines into the actual file).

`ZeroActionResult()` already exists in the file — reuse it as-is.

`Sha256DigestCalculator`/`ActionGraphBuilder`/`ActionDigestStore`/`LocalActionCache`/`ParallelExecutor` usage inside `BuildOneCombinationAsync` is exactly what was in `BuildAsync`'s body before this task — untouched in substance, just moved and taking `modules`/`targetRules`/`context` as parameters instead of reading them from enclosing scope.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Omen.Executors.Tests --filter "FullyQualifiedName~BuildOrchestratorTests"`
Expected: PASS — all pre-existing tests plus the 6 new ones from Step 1.

- [ ] **Step 7: Run the full suite and commit**

Run: `dotnet build Omen.sln && dotnet test tests/Omen.Core.Tests && dotnet test tests/Omen.Executors.Tests`
Expected: build succeeds, all tests PASS.

```bash
git add src/Omen.Executors/Orchestration/BuildOrchestrator.cs tests/Omen.Executors.Tests/BuildOrchestratorTests.cs
git commit -m "feat: resolve-then-loop matrix support and module gating in BuildOrchestrator"
```

---

### Task 6: `ProjectGenerationOrchestrator` + `VisualStudioGenerator` multi-combination generation

**Files:**
- Modify: `src/Omen.Executors/Orchestration/ProjectGenerationOrchestrator.cs`
- Modify: `src/Omen.Core/Generators/VisualStudioGenerator.cs`
- Test: `tests/Omen.Executors.Tests/ProjectGenerationOrchestratorTests.cs`

**Interfaces:**
- Consumes: `TargetMatrix`/`ResolvedTarget` (Task 1), `CompiledRules.FindTargetType()`/`GetSupportedTargets()` (Task 3), the module-gating logic pattern from Task 5 (`IsModuleIncluded`, duplicated here rather than shared — see note in Step 3).
- Produces: a new `VisualStudioGenerator.GenerateAsync(IReadOnlyList<(TargetRules Target, IReadOnlyList<ModuleRules> Modules)> combinations)` overload alongside the existing single-combination `GenerateAsync(TargetRules target, IReadOnlyList<ModuleRules> modules)`, which stays untouched and is what a no-matrix target keeps using.

Read `src/Omen.Executors/Orchestration/ProjectGenerationOrchestrator.cs` and `src/Omen.Core/Generators/VisualStudioGenerator.cs` in full before editing. `VisualStudioGenerator.cs` is a large file (500+ lines) generating one `.vcxproj` per module plus a `.sln`; only its `<ItemGroup Label="ProjectConfigurations">` emission (currently hardcoded to `["Debug","Development","Shipping"] x ["x64","ARM64"]`, one per module's generated project file) needs to become combination-driven for a matrix-declaring target.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Omen.Executors.Tests/ProjectGenerationOrchestratorTests.cs` (read the existing file first for its helper/fixture pattern, likely mirroring `BuildOrchestratorTests.cs`'s `_projectRoot`/`Dispose` shape):

```csharp
[Fact]
public async Task GenerateAsync_TargetWithoutSupportedTargets_UsesTodaysSingleConfigurationList()
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
            public AppTarget(BuildContext context) : base(context)
            {
                Type = TargetType.Executable;
                LaunchModuleName = "AppModule";
                ExtraModules.Add("AppModule");
            }
        }
        """);
    var orchestrator = new ProjectGenerationOrchestrator();

    var success = await orchestrator.GenerateAsync(
        new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.VS2022 },
        events: null);

    success.Should().BeTrue();
    var vcxproj = File.ReadAllText(Directory.GetFiles(_projectRoot, "AppModule.vcxproj", SearchOption.AllDirectories).Single());
    vcxproj.Should().Contain("Debug|x64");
    vcxproj.Should().Contain("Shipping|ARM64");
}

[Fact]
public async Task GenerateAsync_TargetWithSupportedTargets_EmitsOneProjectConfigurationPerResolvedCombination()
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
                .For(TargetArchitecture.X64, TargetArchitecture.ARM64)
                .For(BuildConfiguration.Debug);

            public AppTarget(BuildContext context) : base(context)
            {
                Type = TargetType.Executable;
                LaunchModuleName = "AppModule";
                ExtraModules.Add("AppModule");
            }
        }
        """);
    var orchestrator = new ProjectGenerationOrchestrator();

    var success = await orchestrator.GenerateAsync(
        new ProjectGenerationOrchestratorRequest { ProjectRoot = _projectRoot, Ide = IdeKind.VS2022 },
        events: null);

    success.Should().BeTrue();
    var vcxproj = File.ReadAllText(Directory.GetFiles(_projectRoot, "AppModule.vcxproj", SearchOption.AllDirectories).Single());
    vcxproj.Should().Contain("Debug|x64");
    vcxproj.Should().Contain("Debug|ARM64");
    vcxproj.Should().NotContain("Shipping"); // matrix declared only Debug - today's hardcoded list must not leak in.
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Omen.Executors.Tests --filter "FullyQualifiedName~ProjectGenerationOrchestratorTests"`
Expected: the second test FAILs (still emits the hardcoded Debug/Development/Shipping list regardless of the declared matrix); the first should already pass since it matches today's behavior — confirm it does before proceeding, since it's the regression guard.

- [ ] **Step 3: Implement in `ProjectGenerationOrchestrator`**

In `GenerateAsync`, after `compiledRules = await ruleCompiler.CompileRulesAsync(workingDir);` and before constructing `context`, add the same resolve step Task 5 added to `BuildOrchestrator` (this is intentionally duplicated rather than extracted into a shared helper at this stage — the two orchestrators' surrounding control flow differs enough, per this codebase's existing convention of each orchestrator owning its own compiled-rules-to-BuildContext pipeline, that a premature shared abstraction would cost more than it saves; revisit only if a third caller needs the same logic):

```csharp
var targetType = compiledRules.FindTargetType();
if (targetType == null)
{
    events?.Report(new OrchestratorEvent("No target found.", OrchestratorEventLevel.Error));
    return false;
}
var matrix = compiledRules.GetSupportedTargets(targetType);

var optionCacheStore = new OptionCacheStore(Path.Combine(workingDir, "Intermediate", "omen-cache.json"));
var cachedOptionValues = optionCacheStore.Load();

List<(TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context)> combinations = [];

if (matrix == null)
{
    var context = new BuildContext
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Development,
        ProjectRoot = workingDir,
        IntermediateDirectory = Path.Combine(workingDir, "Intermediate"),
        OutputDirectory = Path.Combine(workingDir, "Binaries"),
        CachedOptionValues = cachedOptionValues
    };
    var targets = compiledRules.CreateTargetRules(context);
    var modules = compiledRules.CreateModuleRules(context);
    if (targets.Count == 0)
    {
        events?.Report(new OrchestratorEvent("No targets found.", OrchestratorEventLevel.Error));
        return false;
    }
    combinations.Add((targets.First(), modules, context));
}
else
{
    var resolvedTargets = matrix.Resolve();
    if (resolvedTargets.Count == 0)
    {
        events?.Report(new OrchestratorEvent(
            $"'{targetType.Name}'s SupportedTargets matrix resolved to zero combinations - check its Exclude rules.",
            OrchestratorEventLevel.Error));
        return false;
    }

    foreach (var resolved in resolvedTargets)
    {
        var platform = resolved.Get<TargetPlatform>();
        var configuration = resolved.Get<BuildConfiguration>();
        var context = new BuildContext
        {
            Platform = platform,
            Architecture = resolved.Get<TargetArchitecture>(),
            Configuration = configuration,
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate", $"{platform}_{configuration}"),
            OutputDirectory = Path.Combine(workingDir, "Binaries", $"{platform}_{configuration}"),
            CachedOptionValues = cachedOptionValues
        };
        var target = compiledRules.CreateTargetRules(context).First();
        var modules = compiledRules.CreateModuleRules(context)
            .Where(m => compiledRules.GetSupportedTargets(m.GetType()) is not { } moduleMatrix
                        || moduleMatrix.Resolve().Any(c => c.Values.All(kv => resolved.TryGet(kv.Key, out var v) && Equals(v, kv.Value))))
            .ToList();
        combinations.Add((target, modules, context));
    }
}

var target = combinations[0].Target;
var modules = combinations[0].Modules;
var context = combinations[0].Context; // used below by the existing compile_commands.json step, unchanged
```

Delete the existing single `var context = new BuildContext { ... }` / `var targets = ...` / `var modules = ...` / `var target = targets.First();` block that currently sits at this point in the file (Step 3's code above replaces it entirely) — but keep everything from `switch (request.Ide)` onward for now; Step 4 changes the VS-specific branch only.

- [ ] **Step 4: Thread combinations into the VS branch, leave VSCode/CMake/Rider untouched**

Change the `case IdeKind.VS2019:`/`VS2022:`/`VS2026:` branches to call a new overload:

```csharp
case IdeKind.VS2019:
    await GenerateVisualStudioAsync(workingDir, combinations, VisualStudioGenerator.VisualStudioVersion.VS2019, events);
    break;
```

(same pattern for VS2022/VS2026). `GenerateVisualStudioAsync`'s signature changes from `(string projectRoot, TargetRules target, IReadOnlyList<ModuleRules> modules, ...)` to `(string projectRoot, IReadOnlyList<(TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context)> combinations, ...)`, and its body changes from:

```csharp
var generator = new VisualStudioGenerator(projectRoot, version);
await generator.GenerateAsync(target, modules);
```

to:

```csharp
var generator = new VisualStudioGenerator(projectRoot, version);
if (combinations.Count == 1)
    await generator.GenerateAsync(combinations[0].Target, combinations[0].Modules);
else
    await generator.GenerateAsync(combinations.Select(c => (c.Target, c.Modules)).ToList());
```

`GenerateVSCodeAsync`/`GenerateCMakeAsync` keep their existing `(TargetRules target, IReadOnlyList<ModuleRules> modules)` signatures, called with `combinations[0].Target`/`combinations[0].Modules` at their call sites — VSCode/CMake generation for a multi-combination target uses only the first resolved combination for now (out of scope to extend every generator; the spec's "Project generation output" section specifically calls out VS's `<ProjectConfiguration>` model, not VSCode/CMake).

The `compile_commands.json` step at the bottom of `GenerateAsync` keeps using `context`/`target`/`modules` from `combinations[0]` (today's exact behavior for the primary combination), unchanged.

- [ ] **Step 5: Add the multi-combination overload to `VisualStudioGenerator`**

In `src/Omen.Core/Generators/VisualStudioGenerator.cs`, find the existing `public async Task GenerateAsync(TargetRules target, IReadOnlyList<ModuleRules> modules)` method (or equivalent — confirm its exact current name/signature by reading the file, since this plan was written from a partial read). Add a new overload:

```csharp
/// <summary>
/// Multi-combination generation for a target with a declared SupportedTargets matrix -
/// each tuple's Platform/Configuration becomes one &lt;ProjectConfiguration&gt; entry
/// instead of today's hardcoded Debug/Development/Shipping x x64/ARM64 list. All tuples
/// must share the same Target (only Modules legitimately varies per combination, via
/// module gating) and the same set of module names across combinations that include them.
/// </summary>
public async Task GenerateAsync(IReadOnlyList<(TargetRules Target, IReadOnlyList<ModuleRules> Modules)> combinations)
{
    // Implementation note for the task's implementer: read the existing single-combination
    // GenerateAsync/its per-module project-file-writing helper in full first. The
    // hardcoded `foreach (var config in new[] { "Debug", "Development", "Shipping" })
    // foreach (var platform in new[] { "x64", "ARM64" })` loop that emits
    // <ProjectConfiguration> entries (and the matching per-configuration
    // <ItemDefinitionGroup Condition="...">) needs to iterate `combinations` instead -
    // one <ProjectConfiguration Include="{Configuration}|{Platform}"> and one
    // <ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='{Configuration}|{Platform}'">
    // per tuple, using that tuple's own Modules for the ItemDefinitionGroup's per-module
    // defines/include paths (mirroring how the single-combination path already derives
    // them from `modules`). The solution (.sln) file and per-module .vcxproj file set
    // stay driven by combinations[0].Modules (module *identity* doesn't vary across
    // combinations - only which modules are *included* does, per module gating - so the
    // set of .vcxproj files to emit is the union of every combination's Modules, and a
    // module absent from a given combination simply has no <ItemDefinitionGroup> for that
    // one <ProjectConfiguration> in its own .vcxproj, which MSBuild treats as "not built
    // for that configuration").
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Omen.Executors.Tests --filter "FullyQualifiedName~ProjectGenerationOrchestratorTests"`
Expected: PASS, both tests.

- [ ] **Step 7: Run the full suite and commit**

Run: `dotnet build Omen.sln && dotnet test tests/Omen.Core.Tests && dotnet test tests/Omen.Executors.Tests`
Expected: build succeeds, all tests PASS.

```bash
git add src/Omen.Executors/Orchestration/ProjectGenerationOrchestrator.cs src/Omen.Core/Generators/VisualStudioGenerator.cs tests/Omen.Executors.Tests/ProjectGenerationOrchestratorTests.cs
git commit -m "feat: multi-combination project generation for targets with a declared matrix"
```

---

### Task 7: CLI — filters instead of requirements, `--dim`, whole-matrix default

**Files:**
- Modify: `src/Omen.CLI/Commands/BuildCommand.cs`
- Test: manual verification only (CLI argument parsing has no existing unit-test coverage in this codebase — `BuildCommand.cs` is exercised end-to-end via `Omen.Executors.Tests`' orchestrator tests and this plan's Task 11 dogfooding pass, matching this repo's established convention).

**Interfaces:**
- Consumes: `BuildOrchestratorRequest.Platform`/`.Architecture`/`.Configuration`/`.DimensionFilter` (Task 5), `DimensionLookup` (Task 4).

Read `src/Omen.CLI/Commands/BuildCommand.cs` in full before editing.

- [ ] **Step 1: Make the parse helpers nullable and remove their auto-defaulting**

Replace the three private parse helpers at the bottom of the file:

```csharp
private static bool TryParsePlatform(string? platform, out TargetPlatform? result)
{
    if (string.IsNullOrEmpty(platform)) { result = null; return true; }
    if (Enum.TryParse<TargetPlatform>(platform, ignoreCase: true, out var parsed)) { result = parsed; return true; }
    result = null;
    return false;
}

private static TargetArchitecture? ParseArchitecture(string? arch)
{
    if (string.IsNullOrEmpty(arch)) return null;
    return Enum.TryParse<TargetArchitecture>(arch, ignoreCase: true, out var result) ? result : TargetArchitecture.X64;
}

private static BuildConfiguration? ParseConfiguration(string? config)
{
    if (string.IsNullOrEmpty(config)) return null;
    return Enum.TryParse<BuildConfiguration>(config, ignoreCase: true, out var result) ? result : BuildConfiguration.Development;
}
```

(Previously, `ParsePlatform` defaulted to the host OS and `ParseConfiguration`'s default came from the option's own `() => "Development"` factory — both defaults now live in `BuildOrchestrator`'s no-matrix branch from Task 5, so the CLI layer no longer needs them. `ParseArchitecture`/`ParseConfiguration` keep their existing "invalid non-empty input silently falls back to a default" behavior unchanged — only "not specified at all" now returns `null` instead of a defaulted value.)

- [ ] **Step 2: Update the `--configuration` option definition**

Find `configOption` in `Create()`:

```csharp
var configOption = new Option<string>(
    ["--configuration", "-c"],
    () => "Development",
    "Build configuration (Debug, Development, Shipping)");
```

Change to:

```csharp
var configOption = new Option<string?>(
    ["--configuration", "-c"],
    "Build configuration - omitted means unconstrained across a declared matrix, or Development for a target without one (Debug, Development, Release, Shipping)");
```

(Removing the `() => "Development"` factory default is required — otherwise System.CommandLine injects "Development" even when the user typed nothing, making "not specified" indistinguishable from "explicitly chose Development.")

- [ ] **Step 3: Add the `--dim` option**

After the `dryRunOption` declaration, add:

```csharp
var dimOption = new Option<string[]>(
    "--dim",
    () => [],
    "Filter a custom dimension, e.g. --dim RenderApi=DirectX12 (repeatable)");
```

Add `command.AddOption(dimOption);` alongside the other `AddOption` calls, and thread a `dim` parameter through `SetHandler`'s lambda and into `ExecuteBuildAsync`'s parameter list (as `string[] dim`).

- [ ] **Step 4: Rewrite the platform/config/arch resolution and request construction in `ExecuteBuildAsync`**

Replace:

```csharp
var targetPlatform = ParsePlatform(platform);
if (targetPlatform == null)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] Invalid platform '{platform}'");
    return 1;
}

var targetArch = ParseArchitecture(arch);
var buildConfig = ParseConfiguration(configuration);
```

with:

```csharp
if (!TryParsePlatform(platform, out var targetPlatform))
{
    AnsiConsole.MarkupLine($"[red]Error:[/] Invalid platform '{platform}'");
    return 1;
}

var targetArch = ParseArchitecture(arch);
var buildConfig = ParseConfiguration(configuration);
```

Then, before constructing `BuildOrchestratorRequest`, add `--dim` parsing (this needs the compiled rules assembly to resolve custom dimension names, which the request/orchestrator doesn't expose to the CLI directly - resolve dimension filters against `typeof(BuildDimensionAttribute).Assembly` plus a fresh, lightweight rules compile purely for name lookup, mirroring how `ExecuteDryRunAsync` already compiles rules independently a few lines below):

```csharp
IReadOnlyDictionary<Type, IReadOnlyList<Enum>>? dimensionFilter = null;
if (dim.Length > 0)
{
    var lookupCompiler = new RuleCompiler(Path.Combine(Path.GetDirectoryName(targetFile)!, "Intermediate", "RuleCache"));
    var lookupRules = await lookupCompiler.CompileRulesAsync(Path.GetDirectoryName(targetFile)!);
    var filter = new Dictionary<Type, List<Enum>>();
    foreach (var entry in dim)
    {
        var parts = entry.Split('=', 2);
        if (parts.Length != 2)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --dim expects Name=Value, got '{entry}'");
            return 1;
        }
        var dimensionType = DimensionLookup.FindDimensionType(lookupRules.Assembly, parts[0]);
        if (dimensionType == null || !DimensionLookup.TryParseValue(dimensionType, parts[1], out var value))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unknown dimension or value in '--dim {entry}'");
            return 1;
        }
        if (!filter.TryGetValue(dimensionType, out var list))
            filter[dimensionType] = list = [];
        list.Add(value!);
    }
    dimensionFilter = filter.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Enum>)kv.Value);
}
```

Then update the request construction:

```csharp
var request = new BuildOrchestratorRequest
{
    TargetFile = targetFile,
    Platform = targetPlatform,
    Architecture = targetArch,
    Configuration = buildConfig,
    DimensionFilter = dimensionFilter,
    Jobs = jobs
};
```

- [ ] **Step 5: Update the printed target summary to tolerate unresolved values**

The lines printing `[blue]Platform:[/] {targetPlatform}` etc. (before the `if (dryRun)` branch) need to handle `null` gracefully now — change to:

```csharp
AnsiConsole.MarkupLine($"[blue]Platform:[/] {(targetPlatform?.ToString() ?? "(matrix default)")}");
AnsiConsole.MarkupLine($"[blue]Architecture:[/] {(targetArch?.ToString() ?? "(matrix default)")}");
AnsiConsole.MarkupLine($"[blue]Configuration:[/] {(buildConfig?.ToString() ?? "(matrix default)")}");
```

- [ ] **Step 6: `ExecuteDryRunAsync` keeps today's exact single-context behavior**

`ExecuteDryRunAsync`'s signature currently takes non-nullable `TargetPlatform targetPlatform, TargetArchitecture targetArch, BuildConfiguration buildConfig`. Since `targetPlatform`/`targetArch`/`buildConfig` are now nullable in the caller, update the call site to apply the same host-OS/Development defaults `BuildOrchestrator`'s no-matrix branch uses (dry-run intentionally stays single-context only — matrix-aware dry-run output is out of scope for this plan):

```csharp
if (dryRun)
{
    return await ExecuteDryRunAsync(
        targetFile,
        targetPlatform ?? DefaultPlatformForHostOS(),
        targetArch ?? DefaultArchitectureForHostOS(),
        buildConfig ?? BuildConfiguration.Development,
        jobs);
}
```

Add private copies of `DefaultPlatformForHostOS()`/`DefaultArchitectureForHostOS()` to `BuildCommand.cs` (same bodies as Task 5's `BuildOrchestrator` versions — this small duplication across CLI and orchestrator is acceptable; `BuildCommand.cs` already duplicated `ParsePlatform`'s OS-detection logic before this task, so this preserves that existing convention rather than introducing shared-utility scope creep for two nearly-identical private helpers).

- [ ] **Step 7: Build and manually verify**

Run: `dotnet build Omen.sln`
Expected: 0 errors.

Run: `dotnet run --project src/Omen.CLI -- build --help`
Expected: help text lists `--dim` and shows the updated `--configuration` description.

- [ ] **Step 8: Run the full suite and commit**

Run: `dotnet test tests/Omen.Core.Tests && dotnet test tests/Omen.Executors.Tests`
Expected: all tests PASS (this task doesn't touch `Omen.Core.Tests`/`Omen.Executors.Tests` directly, but confirms nothing broke).

```bash
git add src/Omen.CLI/Commands/BuildCommand.cs
git commit -m "feat: CLI filters instead of requirements, --dim, whole-matrix-by-default"
```

---

### Task 8: CLI — `generate project` filters

**Files:**
- Modify: `src/Omen.CLI/Commands/GenerateCommand.cs`

**Interfaces:**
- Consumes: nothing new — `ProjectGenerationOrchestratorRequest` (Task 6) doesn't currently take Platform/Architecture/Configuration/filter fields at all (it generates for whatever the matrix resolves, unfiltered, always). This task only adds CLI *visibility* into which IDE to target; it does not add filtering to `generate` — see rationale below.

Read `src/Omen.CLI/Commands/GenerateCommand.cs`'s `CreateProjectFilesCommand()` in full before editing.

**Deliberately narrow scope:** `generate project` has no `--platform`/`--configuration`/`--dim` today, and this plan does not add them — `ProjectGenerationOrchestrator` (Task 6) always generates the whole resolved matrix into one multi-configuration project file (that's the entire point of a `<ProjectConfiguration>`-per-combination `.vcxproj` - VS itself is where a developer picks which configuration to build from the generated file, matching CMake+Visual-Studio's own division of labor). Filtering which combinations to *generate* would only matter if generation were expensive enough to want to skip some, which it isn't (it doesn't compile anything). This task makes no functional change — it's a documentation-only check confirming that absence is intentional, not an oversight:

- [ ] **Step 1: Confirm no change needed and record why**

Read `GenerateCommand.cs` and confirm `CreateProjectFilesCommand()` has no platform/configuration/arch options today. Add a one-line comment above `ideOption`'s declaration:

```csharp
// generate project intentionally has no --platform/--configuration/--dim: it always
// generates the whole resolved matrix into one multi-configuration project file (see
// ProjectGenerationOrchestrator) - the IDE, not this CLI command, is where a developer
// picks which configuration to build.
var ideOption = new Option<string>(
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build Omen.sln`
Expected: 0 errors.

```bash
git add src/Omen.CLI/Commands/GenerateCommand.cs
git commit -m "docs: record why generate project takes no dimension filter flags"
```

---

### Task 9: GUI — `MatrixPanelViewModel` and `MainWindowViewModel` wiring

**Files:**
- Create: `src/Omen.GUI/ViewModels/MatrixPanelViewModel.cs`
- Modify: `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Omen.Executors.Tests/` — none (this task is GUI ViewModel code with no dedicated `Omen.GUI.Tests` project in this repo, matching this codebase's established convention for prior GUI ViewModel tasks — verified by build + manual run, per the plan's Task 11 dogfooding pass).

**Interfaces:**
- Consumes: `TargetMatrix`/`ResolvedTarget` (Task 1), `CompiledRules.FindTargetType()`/`GetSupportedTargets()` (Task 3), `BuildOrchestratorRequest.DimensionFilter` (Task 5).
- Produces: `MatrixPanelViewModel.IsActive` (bool, true once a matrix was resolved for the current project), `MatrixPanelViewModel.DimensionGroups` (`ObservableCollection<DimensionGroupViewModel>`), `MatrixPanelViewModel.ResolveAsync(string targetFile)`, `MatrixPanelViewModel.BuildFilter() -> (TargetPlatform? Platform, TargetArchitecture? Architecture, BuildConfiguration? Configuration, IReadOnlyDictionary<Type, IReadOnlyList<Enum>>? DimensionFilter)` — Task 10 (View) binds to `DimensionGroups`; `MainWindowViewModel.BuildAsync`/`RebuildAsync` call `BuildFilter()` when `MatrixPanel.IsActive`.

- [ ] **Step 1: Implement `MatrixPanelViewModel`**

Create `src/Omen.GUI/ViewModels/MatrixPanelViewModel.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.GUI.ViewModels;

public sealed partial class DimensionValueViewModel : ViewModelBase
{
    public required Enum Value { get; init; }
    public string DisplayName => Value.ToString();

    [ObservableProperty]
    private bool _isChecked = true;
}

public sealed partial class DimensionGroupViewModel : ViewModelBase
{
    public required Type DimensionType { get; init; }
    public required string DisplayName { get; init; }
    public ObservableCollection<DimensionValueViewModel> Values { get; } = [];
}

/// <summary>
/// Resolves a target's declared TargetMatrix (if any) into one checkbox group per varying
/// dimension - unchecking a value is equivalent to a CLI --dim/--platform exclusion on that
/// value. For a target without a declared matrix, IsActive stays false and the GUI's
/// existing single-combination Platform/Configuration ComboBoxes are what's shown instead
/// (see MainWindow.axaml).
/// </summary>
public sealed partial class MatrixPanelViewModel : ViewModelBase
{
    private static readonly Type[] BuiltInOrder = [typeof(TargetPlatform), typeof(TargetArchitecture), typeof(BuildConfiguration)];

    public ObservableCollection<DimensionGroupViewModel> DimensionGroups { get; } = [];

    [ObservableProperty]
    private bool _isActive;

    public async Task ResolveAsync(string targetFile)
    {
        DimensionGroups.Clear();
        IsActive = false;

        var workingDir = Path.GetDirectoryName(targetFile) ?? Path.GetPathRoot(targetFile) ?? targetFile;
        var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));
        CompiledRules compiledRules;
        try
        {
            compiledRules = await ruleCompiler.CompileRulesAsync(workingDir);
        }
        catch
        {
            return; // Configure/Build's own error reporting already surfaces rule-compile failures.
        }

        var targetType = compiledRules.FindTargetType();
        if (targetType == null) return;

        var matrix = compiledRules.GetSupportedTargets(targetType);
        if (matrix == null) return;

        var resolved = matrix.Resolve();
        if (resolved.Count == 0) return;

        var varyingDimensions = resolved[0].Values.Keys
            .Where(dimensionType => resolved.Select(r => r.Values[dimensionType]).Distinct().Count() > 1)
            .OrderBy(t => Array.IndexOf(BuiltInOrder, t) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(t => t.Name);

        foreach (var dimensionType in varyingDimensions)
        {
            var group = new DimensionGroupViewModel { DimensionType = dimensionType, DisplayName = dimensionType.Name };
            foreach (var value in resolved.Select(r => r.Values[dimensionType]).Distinct().OrderBy(v => v.ToString()))
            {
                group.Values.Add(new DimensionValueViewModel { Value = value });
            }
            DimensionGroups.Add(group);
        }

        IsActive = true;
    }

    /// <summary>Reads current checkbox state into the shape BuildOrchestratorRequest expects.
    /// An unchecked dimension group (nothing checked) is treated as "everything" rather than
    /// "nothing" - an empty result would mean "match nothing," which is never what an
    /// all-unchecked group means to a user.</summary>
    public (TargetPlatform? Platform, TargetArchitecture? Architecture, BuildConfiguration? Configuration, IReadOnlyDictionary<Type, IReadOnlyList<Enum>>? DimensionFilter) BuildFilter()
    {
        TargetPlatform? platform = null;
        TargetArchitecture? architecture = null;
        BuildConfiguration? configuration = null;
        var customFilter = new Dictionary<Type, IReadOnlyList<Enum>>();

        foreach (var group in DimensionGroups)
        {
            var checkedValues = group.Values.Where(v => v.IsChecked).Select(v => v.Value).ToList();
            if (checkedValues.Count == 0 || checkedValues.Count == group.Values.Count)
                continue; // unconstrained: nothing checked, or everything checked - same effective meaning.

            if (group.DimensionType == typeof(TargetPlatform) && checkedValues.Count == 1)
                platform = (TargetPlatform)checkedValues[0];
            else if (group.DimensionType == typeof(TargetArchitecture) && checkedValues.Count == 1)
                architecture = (TargetArchitecture)checkedValues[0];
            else if (group.DimensionType == typeof(BuildConfiguration) && checkedValues.Count == 1)
                configuration = (BuildConfiguration)checkedValues[0];
            else
                customFilter[group.DimensionType] = checkedValues;
        }

        return (platform, architecture, configuration, customFilter.Count > 0 ? customFilter : null);
    }
}
```

- [ ] **Step 2: Wire into `MainWindowViewModel`**

In `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`:

Add the property (near `public OptionsPanelViewModel OptionsPanel { get; } = new();`):

```csharp
public MatrixPanelViewModel MatrixPanel { get; } = new();
```

In `LoadProject`, alongside the existing `_ = ConfigureOptionsAsync();`, add:

```csharp
_ = ConfigureMatrixAsync();
```

Add the new method (near `ConfigureOptionsAsync`):

```csharp
public async Task ConfigureMatrixAsync()
{
    if (ProjectPath == null) return;

    try
    {
        var targetFile = Directory.GetFiles(ProjectPath, "*.target.cs", SearchOption.AllDirectories).FirstOrDefault();
        if (targetFile == null) return;

        await MatrixPanel.ResolveAsync(targetFile);
    }
    catch (Exception ex)
    {
        AppendLine($"Unexpected error resolving build matrix: {ex.Message}", OrchestratorEventLevel.Error);
    }
}
```

In `BuildAsync`, change the `BuildOrchestratorRequest` construction to use the matrix filter when active:

```csharp
var request = MatrixPanel.IsActive
    ? BuildRequestFromMatrix(targetFile)
    : new BuildOrchestratorRequest
    {
        TargetFile = targetFile,
        Platform = SelectedPlatform.Value,
        Architecture = TargetArchitecture.X64,
        Configuration = SelectedConfiguration
    };
```

Add the helper:

```csharp
private BuildOrchestratorRequest BuildRequestFromMatrix(string targetFile)
{
    var (platform, architecture, configuration, dimensionFilter) = MatrixPanel.BuildFilter();
    return new BuildOrchestratorRequest
    {
        TargetFile = targetFile,
        Platform = platform,
        Architecture = architecture,
        Configuration = configuration,
        DimensionFilter = dimensionFilter
    };
}
```

`BuildAsync`'s existing guard `if (ProjectPath == null || SelectedPlatform == null) return;` needs to become `if (ProjectPath == null) return;` plus a matrix-aware branch, since a matrix-active target no longer requires `SelectedPlatform` to be set (the Platform/Configuration `ComboBox`es are hidden for it — see Task 10):

```csharp
public async Task BuildAsync(bool manageBusyState = true)
{
    if (ProjectPath == null) return;
    if (!MatrixPanel.IsActive && SelectedPlatform == null) return;
    // ... rest unchanged except the request-construction block above.
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Omen.GUI`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Omen.GUI/ViewModels/MatrixPanelViewModel.cs src/Omen.GUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat: add MatrixPanelViewModel and wire matrix-aware Build into MainWindowViewModel"
```

---

### Task 10: GUI — matrix pane in `MainWindow.axaml`

**Files:**
- Modify: `src/Omen.GUI/Views/MainWindow.axaml`

Read the current `MainWindow.axaml` in full before editing — it already has the three-pane collapsible layout, Signal theme classes, and toolbar icons from the prior Options+Polish plan; this task adds a fourth pane following the exact same collapsible pattern the Options pane already established, and hides the Platform/Configuration `ComboBox`es when a matrix is active.

- [ ] **Step 1: Add the matrix pane**

Following the Options pane's existing `Border Classes="pane"` + collapsible-toggle-strip structure (read it first — Task 5 of the prior GUI plan built it), add a fourth pane bound to `MatrixPanel`, visible only when `MatrixPanel.IsActive` is true:

```xml
<Border Classes="pane" IsVisible="{Binding MatrixPanel.IsActive}">
    <DockPanel>
        <TextBlock DockPanel.Dock="Top" Classes="paneHeader" Text="Build Matrix" />
        <ItemsControl ItemsSource="{Binding MatrixPanel.DimensionGroups}">
            <ItemsControl.ItemTemplate>
                <DataTemplate DataType="{x:Type vm:DimensionGroupViewModel}">
                    <StackPanel Margin="8,4">
                        <TextBlock Text="{Binding DisplayName}" FontWeight="SemiBold" />
                        <ItemsControl ItemsSource="{Binding Values}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate DataType="{x:Type vm:DimensionValueViewModel}">
                                    <CheckBox Content="{Binding DisplayName}" IsChecked="{Binding IsChecked}" Margin="16,2,0,0" />
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </StackPanel>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </DockPanel>
</Border>
```

Confirm `xmlns:vm="using:Omen.GUI.ViewModels"` (or whatever alias the file already uses for its ViewModels namespace) is declared on the root `Window`/`UserControl` element — the Options pane's own `DataTemplate`s already reference `vm:BuildOptionViewModel`, so this alias should already exist; reuse it rather than adding a second one.

Place this pane using the same `Grid`/`GridSplitter` column pattern the Options pane uses, adding one more column and splitter — read the current column definitions before editing so the new pane's column index is correct.

- [ ] **Step 2: Hide Platform/Configuration `ComboBox`es when a matrix is active**

Find the toolbar's Platform/Configuration `ComboBox`es (built in the prior GUI plan) and add:

```xml
IsVisible="{Binding !MatrixPanel.IsActive}"
```

to both `ComboBox` elements (and their adjacent "Platform:"/"Config:" `TextBlock` labels).

- [ ] **Step 3: Build and manually verify**

Run: `dotnet build src/Omen.GUI`
Expected: 0 errors (Avalonia's compiled-binding XAML compiler validates every `{Binding}` path against the real ViewModel members at build time - a clean build here is real evidence the bindings resolve).

Run: `dotnet run --project src/Omen.GUI` for a few seconds to confirm no startup exception, then close.

- [ ] **Step 4: Commit**

```bash
git add src/Omen.GUI/Views/MainWindow.axaml
git commit -m "feat: add collapsible build-matrix pane to MainWindow"
```

---

### Task 11: End-to-end verification

**Files:**
- Modify: `examples/GemSample/GemSample.target.cs` (or a new, small dedicated sample if `GemSample`'s existing single-platform shape doesn't suit a matrix demonstration well — decide after reading its current state; a `SupportedTargets` declaration spanning 2 platforms x 2 configurations is enough to prove the feature without a large new sample).

This mirrors the rigor of the prior Options plan's own Task 8: real, non-simulated verification against a real sample project via the actual CLI, not just unit tests.

- [ ] **Step 1: Add a real declared matrix to a sample target**

Read `examples/GemSample/GemSample.target.cs`'s current content (it already declares a `VERBOSE_GREETING` build option from the prior plan). Add:

```csharp
public static TargetMatrix SupportedTargets => TargetMatrix.Empty
    .For(TargetPlatform.Windows)
    .For(TargetArchitecture.X64)
    .For(BuildConfiguration.Debug, BuildConfiguration.Development);
```

(Scoped to Windows/X64 only, matching what's actually buildable in this environment — Debug and Development as the two varying configurations, so the whole-matrix build below produces two real, observably-different binaries without needing cross-platform toolchains this environment doesn't have.)

- [ ] **Step 2: Full solution build and test suite**

Run: `dotnet build Omen.sln`
Expected: 0 errors.

Run: `dotnet test tests/Omen.Core.Tests && dotnet test tests/Omen.Executors.Tests`
Expected: both fully green.

- [ ] **Step 3: Real CLI verification of whole-matrix build**

Run: `dotnet run --project src/Omen.CLI -- build examples/GemSample/GemSample.target.cs` (no `--configuration` flag - matrix declared, so this builds the whole matrix by default per this plan's CLI default).

Expected: the build report shows both Debug and Development were built; confirm both `examples/GemSample/Binaries/Windows_Debug/GemSample.exe` and `examples/GemSample/Binaries/Windows_Development/GemSample.exe` exist and both run successfully.

- [ ] **Step 4: Real CLI verification of a filtered build**

Run: `dotnet run --project src/Omen.CLI -- build examples/GemSample/GemSample.target.cs --configuration Debug`

Expected: only the Debug combination builds (report shows one combination, not two).

- [ ] **Step 5: Real CLI verification of module gating**

Temporarily add a `SupportedTargets => TargetMatrix.Empty.For(BuildConfiguration.Development)` to one of `GemSample`'s existing gem modules (e.g. `Greeter.Runtime` — read its current `.module.cs` first), rebuild the whole matrix, and confirm: the Development combination includes the module as before, the Debug combination's action graph has zero actions for it (check `Intermediate/Windows_Debug/Greeter.Runtime/` doesn't exist while `Intermediate/Windows_Development/Greeter.Runtime/` does). Revert this temporary change afterward - it's a verification probe, not a permanent sample addition (unlike Task 11 Step 1's `SupportedTargets` on the target itself, which stays).

- [ ] **Step 6: Commit**

```bash
git add examples/GemSample/GemSample.target.cs
git commit -m "feat: declare a real SupportedTargets matrix on GemSample for end-to-end verification"
```

---

## Post-plan notes for the controller

- Task 5's `IsModuleIncluded`/module-gating logic and Task 6's near-identical inline equivalent inside `ProjectGenerationOrchestrator` are intentionally duplicated (see Task 6 Step 3's note) — if a third orchestrator ever needs the same resolve-then-loop shape, extract a shared helper then, not preemptively.
- This plan does not touch `CleanOrchestrator` — a `Clean`/`Rebuild` against a matrix-declaring target cleans `Intermediate`/`Binaries` at the project root today (deleting everything under both, regardless of per-combination subfolder structure), which already correctly removes every combination's output without needing matrix awareness itself. Confirm this holds during Task 11's manual verification; if `CleanOrchestrator` turns out to need a matrix-aware change, that's a new, small follow-up task, not a sign this plan under-scoped Clean.
- `OptionsOrchestrator`/`OptionsPanelViewModel` (the prior plan's build-options feature) are unaffected — options remain resolved once per `omen-cache.json`, shared across every combination in a matrix, per this spec's own non-goals ("one cache file per project, shared across every Platform/Configuration combination" already applied to build options generally, now also true across the matrix).
