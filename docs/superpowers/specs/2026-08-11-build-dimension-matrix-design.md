# Omen: N-Dimensional Build Configuration Matrix

## Context

Omen's `BuildContext` currently hardcodes exactly three configuration axes —
`Platform`, `Architecture`, `Configuration` — as fixed, `required` fields.
Every consumer (`ActionGraphBuilder`, `PlatformFactory`, `IToolchain`,
`BuildOrchestratorRequest`, the CLI's `--platform`/`--configuration`/`--arch`
flags, the GUI's dropdowns) assumes exactly this triple and exactly one
resolved combination per build/generate invocation.

This is the first of several planned initiatives motivated by research into
Ubisoft's Sharpmake (a C#-based project-generation tool used across large
AAA codebases): the goal is for Omen to reach "everything Sharpmake has and
more" ahead of fully replacing NightFox's CMake build system, with two
further capabilities on the near/medium horizon — a .NET build backend
(WPF/Avalonia, since the toolset is migrating to WPF) and, further out,
Rust/Go support. Those are explicitly **separate, later specs** (see
Non-goals). This spec covers only the configuration-dimension system itself:
Sharpmake's "Fragment" concept, adapted to Omen.

Sharpmake's fragments are enums tagged with an attribute; any `ITarget`
picks which fragments it uses; `Targets.BuildTargets()` computes the
cartesian product across a project's declared fragment values to produce
every configuration it should generate. Omen's Platform/Architecture/
Configuration triple is functionally three fixed, always-present fragments
with no way to add a fourth without editing core types.

## Non-goals

- **The .NET/WPF/Avalonia build backend.** `IToolchain` today is entirely
  C++-shaped (`CppStandard`, RTTI/exceptions flags, object/static/shared-lib
  extensions). Supporting .NET UI projects is a second build model with
  effectively no shared shape with the first, and deserves its own spec.
  This spec's dimension system must not preclude a future "Toolchain" or
  "Language" dimension sitting alongside Platform/Architecture/
  Configuration, but does not design or build one.
- **Rust/Go support.** Explicitly a future concern; same reasoning as above.
- **GUI multi-select.** The GUI's Platform/Configuration dropdowns and Build
  button stay single-combination for this spec. Selecting an arbitrary
  subset of a declared matrix from the GUI is a natural follow-up, not
  designed here.
- **Cross-dimension validity rules** (e.g. "Architecture ARMv7 is invalid
  for Platform Windows"). `TargetMatrix` resolves the raw cartesian product
  of whatever values a target declares; a target that needs to exclude
  specific combinations scopes `SupportedTargets` more narrowly itself. No
  general exclusion-rule engine is designed here.
- **Module-level matrices.** `ModuleRules` does not gain its own
  `SupportedTargets` — see Architecture.

## Architecture

### Dimensions are attribute-tagged enums

```csharp
namespace Omen.Core.Configuration;

[AttributeUsage(AttributeTargets.Enum)]
public sealed class BuildDimensionAttribute : Attribute;
```

`TargetPlatform`, `TargetArchitecture`, `BuildConfiguration` (in
`BuildEnums.cs`) each gain `[BuildDimension]` — no other change. A studio
adds a custom axis (e.g. `RenderApi { DirectX12, Vulkan }`) by defining its
own `[BuildDimension]`-tagged enum anywhere a rules file can reference it —
no core-repo edit, mirroring Sharpmake's open fragment set.

Dimension *types* are discovered by scanning the compiled rules assembly
plus `Omen.Core` itself for `[BuildDimension]`-tagged enums (the same
reflection pass that already finds `TargetRules`/`ModuleRules`/`GemRules`
subclasses in `RuleCompiler`'s `CompiledRules`). "All registered values" for
a dimension means every value of that enum.

### `ResolvedTarget` — one point in the matrix

```csharp
public sealed class ResolvedTarget
{
    public T Get<T>() where T : struct, Enum;
    public bool TryGet<T>(out T value) where T : struct, Enum;
}
```

Holds one resolved value per dimension type present in this combination.
`BuildContext` gains an internal `ResolvedTarget` and its existing
`Platform`/`Architecture`/`Configuration` properties become read-through
accessors (`Platform => Resolved.Get<TargetPlatform>()`, etc.) instead of
independent `required` fields — every one of the ~13 files that reads
`context.Platform` today keeps compiling and behaving identically.
`context.Get<RenderApi>()` (or similar) is how a custom dimension is read.

### `TargetMatrix` — the `SupportedTargets` builder

```csharp
public sealed class TargetMatrix
{
    public static TargetMatrix Empty { get; }
    public TargetMatrix For<T>(params T[] values) where T : struct, Enum;
    public IReadOnlyList<ResolvedTarget> Resolve();
}
```

`TargetRules` and `GemRules` (not `ModuleRules` — see below) gain the
*optional* convention:

```csharp
public class MyTarget : TargetRules
{
    public static TargetMatrix SupportedTargets => TargetMatrix.Empty
        .For(TargetPlatform.Windows, TargetPlatform.Linux)
        .For(BuildConfiguration.Debug, BuildConfiguration.Release, BuildConfiguration.Shipping);
    // Architecture unconstrained -> every registered TargetArchitecture value

    public MyTarget(BuildContext context) : base(context) { ... } // unchanged
}
```

This is read via reflection *before* any `BuildContext` exists — solving
the chicken-and-egg problem of "which combinations exist" needing to be
known ahead of constructing a context-bound rules instance. `.Resolve()`
computes the cartesian product across every constrained dimension, defaults
every unconstrained *registered* dimension to "all its values," and returns
one `ResolvedTarget` per combination.

**Backward compatibility is load-bearing here:** if a rules file does not
declare `SupportedTargets` at all, the orchestrator layer (next section)
treats it exactly as today — one `BuildContext`, built from whatever the
caller (CLI flags / GUI selection) resolves, zero expansion. The matrix only
activates for a target that opts in. Defaulting an *undeclared* target to
"cartesian product of everything" would explode every existing `.target.cs`
in NightFox/GemSample/ExampleGame the moment this ships — not acceptable.

### Why `TargetRules`/`GemRules` only, not `ModuleRules`

A module is invoked once per whatever `ResolvedTarget` its owning target
resolves to, exactly as today. Letting `ModuleRules` declare its own matrix
would require a conflict-resolution rule for a target and one of its
dependency modules disagreeing on supported combinations — real complexity
for no benefit, since a module doesn't decide what gets built, it just
responds to whatever context it's given.

### Orchestrators: resolve, then loop the existing single-context pipeline

`BuildOrchestrator`/`ProjectGenerationOrchestrator` gain a resolve step
ahead of their existing logic:

1. Reflect for `SupportedTargets` on the resolved `TargetRules`/`GemRules`
   type (same pattern `CreateTargetRules` already uses to find the type).
2. If absent: one `ResolvedTarget`, built from the caller's (fully
   specified) Platform/Architecture/Configuration — today's exact behavior.
3. If present: `.Resolve()` the matrix, then intersect with any filter the
   caller supplied (CLI `--platform`/`--dim`, or an explicit GUI selection);
   an empty filter passes the whole matrix through unfiltered.
4. For each resulting `ResolvedTarget`, construct one `BuildContext` (own
   `IntermediateDirectory`/`OutputDirectory`, following today's
   `{Platform}_{Configuration}` naming so per-combination outputs never
   collide) and run the **existing, unmodified** single-context build or
   generate logic.

`ActionGraphBuilder`, `ParallelExecutor`, and `ActionDigestStore` never
learn about the matrix — they still only ever see one `BuildContext` per
call, identical to today. This is what keeps "resolve the full cartesian
matrix" tractable: the new layer sits above today's machinery, not inside
it.

### CLI

`--platform`/`--configuration`/`--arch` become filters, not requirements:
omitted means unconstrained on that dimension (pass every value the target
declared for it). A generic, repeatable `--dim <TypeName>=<Value>` covers
arbitrary custom dimensions without hardcoding a flag per dimension type,
mirroring CMake's `-D` / MSBuild's `/p:`.

**`omen build` with no filters, on a target that declared a matrix, builds
the whole matrix by default** — matching Sharpmake's own default of
generating for the full declared set, and giving CI parity for free (`omen
build` with no flags is a full-matrix build, useful as-is for a CI sweep). A
developer doing fast iteration is expected to narrow with filter flags, or
use the GUI, which continues to resolve to one combination.

### Project generation output

Visual Studio's own project model is two-dimensional
(`Configuration|Platform`). Any dimension beyond the built-in three folds
into the `Configuration` name string in generated output (e.g.
`Debug_DirectX12|x64`), since that's the only place a `.vcxproj` has room
for it. One `ResolvedTarget` maps to one `<ProjectConfiguration>` entry.

## Components

- **Create** `src/Omen.Core/Configuration/BuildDimensionAttribute.cs`
- **Create** `src/Omen.Core/Configuration/ResolvedTarget.cs`
- **Create** `src/Omen.Core/Configuration/TargetMatrix.cs`
- **Modify** `src/Omen.Core/Configuration/BuildEnums.cs` — tag
  `TargetPlatform`, `TargetArchitecture`, `BuildConfiguration` with
  `[BuildDimension]`
- **Modify** `src/Omen.Core/Configuration/BuildContext.cs` — `Platform`/
  `Architecture`/`Configuration` become read-through properties over an
  internal `ResolvedTarget`; every `new BuildContext { Platform = ..., ... }`
  call site (mostly tests) updates mechanically to construct the
  `ResolvedTarget` instead
- **Modify** `src/Omen.Core/Rules/TargetRules.cs`, `GemRules.cs` — document
  the optional `static TargetMatrix SupportedTargets` convention (no base
  class change needed; it's read via reflection, same as today's
  constructor-shape convention)
- **Modify** `src/Omen.Executors/Orchestration/BuildOrchestrator.cs`,
  `ProjectGenerationOrchestrator.cs` — add the resolve-then-loop step
  described above
- **Modify** `src/Omen.CLI/Commands/BuildCommand.cs`,
  `GenerateCommand.cs` — flags become filters; add `--dim`
- **Modify** VS project generator — multi-`<ProjectConfiguration>` emission
  per resolved target

## Data flow

1. `RuleCompiler` compiles rules files as today; `CompiledRules` gains a way
   to read a discovered `TargetRules`/`GemRules` type's static
   `SupportedTargets` property via reflection, without constructing an
   instance (no `BuildContext` needed yet).
2. Orchestrator resolves the matrix (or the single today's-behavior
   fallback), applies any caller filter, gets `IReadOnlyList<ResolvedTarget>`.
3. For each `ResolvedTarget`: construct `BuildContext`, run the existing
   `CreateTargetRules`/`CreateModuleRules` → `ActionGraphBuilder` →
   `ParallelExecutor` (build) or → VS generator (generate) pipeline,
   unchanged.
4. Build: results aggregate across all resolved combinations (overall
   success = all succeeded); a failure in one combination doesn't stop
   others from running, mirroring how CI matrix builds typically report.
   Generate: one project file gains one more `<ProjectConfiguration>` per
   combination.

## Error handling

- `SupportedTargets` referencing a value whose enum isn't
  `[BuildDimension]`-tagged, or a dimension type never registered anywhere
  → a clear discovery-time error, matching today's "must have a constructor
  that takes BuildContext" reflection-error style. Not a silent skip.
- A caller filter that narrows the resolved matrix to zero combinations →
  an explicit "no targets matched your filter" error, distinguishable from
  today's existing "no modules to build" zero-action *success* path (a
  filter mismatch is a user mistake worth surfacing).
- A per-combination build failure (in the whole-matrix case) is reported
  per-combination in the aggregated result; it does not abort remaining
  combinations.

## Testing

- `TargetMatrix.Resolve()`: cartesian-product correctness; unconstrained
  dimensions default to "all registered values" for that dimension type;
  custom-dimension values resolve correctly via the `[BuildDimension]`
  reflection scan.
- `BuildContext.Platform`/`.Architecture`/`.Configuration` back-compat: the
  acceptance bar is every existing test in the repo passes unchanged beyond
  mechanical `BuildContext` construction-site updates.
- Regression guard: a target *without* `SupportedTargets` resolves to
  exactly the one combination the caller specified — no expansion.
- A target *with* `SupportedTargets` resolves to the correct N
  combinations; `BuildOrchestrator` runs each independently with its own
  `IntermediateDirectory`/`OutputDirectory` (assert no collision, assert
  per-combination invocation).
- A CLI/GUI filter correctly narrows a declared matrix, including down to
  zero (assert the explicit "no targets matched" error, not a silent
  success).
- Generation: a multi-combination target produces one
  `<ProjectConfiguration>` per resolved combination in the emitted
  `.vcxproj`.
