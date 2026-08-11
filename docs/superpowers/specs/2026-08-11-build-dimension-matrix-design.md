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
Non-goals). This spec covers the configuration-dimension system itself
(Sharpmake's "Fragment" concept, adapted to Omen) plus GUI matrix selection,
cross-dimension exclusion rules, and per-module matrix gating — three pieces
initially scoped out and pulled back in during design review.

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
- **Reusable, named exclusion rule libraries** (e.g. a shared "no ARMv7 on
  desktop platforms" rule referenced by name across many targets).
  `TargetMatrix.Exclude` takes an inline predicate per target for this spec;
  a shared-rule catalog is a natural follow-up once real usage shows which
  exclusions repeat often enough to warrant one, not designed speculatively
  now.
- **GUI selection persistence beyond the option cache.** Which
  dimension-value checkboxes a user last selected in the GUI's matrix panel
  is not persisted to `omen-cache.json` or anywhere else in this spec — it
  resets to "everything" on reopen. Persisting the last selection is a small
  follow-up, not designed here.

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
    public TargetMatrix Exclude(Func<ResolvedTarget, bool> predicate);
    public IReadOnlyList<ResolvedTarget> Resolve();
}
```

`TargetRules` and `GemRules` gain the *optional* convention (`ModuleRules`
too, in a narrower role — see below):

```csharp
public class MyTarget : TargetRules
{
    public static TargetMatrix SupportedTargets => TargetMatrix.Empty
        .For(TargetPlatform.Windows, TargetPlatform.Linux)
        .For(TargetArchitecture.X64, TargetArchitecture.ARM64)
        .For(BuildConfiguration.Debug, BuildConfiguration.Release, BuildConfiguration.Shipping)
        .Exclude(t => t.Get<TargetPlatform>() == TargetPlatform.Windows
                    && t.Get<TargetArchitecture>() == TargetArchitecture.ARM64);

    public MyTarget(BuildContext context) : base(context) { ... } // unchanged
}
```

This is read via reflection *before* any `BuildContext` exists — solving
the chicken-and-egg problem of "which combinations exist" needing to be
known ahead of constructing a context-bound rules instance. `.Resolve()`
computes the cartesian product across every constrained dimension, defaults
every unconstrained *registered* dimension to "all its values," applies
every `.Exclude` predicate in declaration order to drop invalid
combinations, and returns one `ResolvedTarget` per surviving combination.
`Exclude` is a predicate rather than declared value-pairs because it
generalizes to any number of dimensions (including custom ones) without a
combinatorial rule table growing per dimension added — a target with an
unusual constraint just writes the boolean expression for it.

**Backward compatibility is load-bearing here:** if a rules file does not
declare `SupportedTargets` at all, the orchestrator layer (next section)
treats it exactly as today — one `BuildContext`, built from whatever the
caller (CLI flags / GUI selection) resolves, zero expansion. The matrix only
activates for a target that opts in. Defaulting an *undeclared* target to
"cartesian product of everything" would explode every existing `.target.cs`
in NightFox/GemSample/ExampleGame the moment this ships — not acceptable.

### `ModuleRules.SupportedTargets` — a gate, not an independent matrix

A module never decides *which combinations get built* — the owning
target's matrix (or today's single caller-resolved combination) is always
authoritative for that. What a module's own, equally optional
`SupportedTargets` decides is narrower: *whether this module participates
in a given resolved combination at all*. This is the real-world case a pure
target-level matrix can't express on its own: a `WindowsInput` module that
should simply not be part of the action graph when the resolved combination
is Linux, without the target author having to duplicate that exclusion on
every target that depends on it.

Concretely: after the orchestrator resolves the target's matrix into a list
of `ResolvedTarget`s (previous section), for *each* one it also calls
`CreateModuleRules` and checks each module's `SupportedTargets` (if
declared) against that specific `ResolvedTarget`. A module whose matrix
doesn't contain the current combination is excluded from the action graph
for that combination only — silently, the same way an unmet
`ForbiddenDependencies`/layering check today prevents a module from
appearing, not an error. A module with no declared `SupportedTargets`
participates in every combination, exactly as today. No merging or
negotiation between a target's matrix and a module's matrix is needed:
`.Resolve()` is only ever called on the target's own matrix; a module's
`SupportedTargets` is only ever evaluated as a yes/no membership test
against one already-resolved `ResolvedTarget` at a time.

Gating is always silent, never an error — including when the gated-out
module is the one a target's `LaunchModuleName` names. A target whose
`LaunchModuleName` (or every module it references) is gated out for a given
combination degrades to the same "no modules to build" warning-then-
zero-action-success path that already exists today for a target with no
modules at all, rather than a new error class. This matters because most
real dependencies are referenced unconditionally (`ExtraModules.Add(...)`
with no platform check) — a module reference isn't a promise that the
module builds everywhere, so gating it out can never be treated as the
caller having done something wrong.

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

### GUI matrix selection

The GUI's existing Platform/Configuration `ComboBox`es and the whole
single-combination Build flow stay exactly as they are for a target
*without* a declared matrix — no behavior change, no new UI shown.

For a target *with* a declared `SupportedTargets`, `MainWindowViewModel`
resolves it the same way `BuildOrchestrator` does (reflect, `.Resolve()`)
and surfaces a new collapsible pane — following the same collapsible-panel
pattern the Options panel already established — showing one checkbox group
per dimension actually varying in the resolved matrix (a dimension the
target pinned to a single value via `.For(oneValue)` doesn't get a group,
since there's nothing to choose). All boxes start checked (unfiltered =
build everything, matching the CLI default). Unchecking a value in a
dimension's group is exactly equivalent to a CLI `--dim`/`--platform`
exclusion on that value: Build/Rebuild pass the resulting per-dimension
allow-lists to the same orchestrator filter step the CLI uses, so GUI and
CLI filtering share one implementation, not two.

The Platform/Configuration `ComboBox`es are hidden (superseded by the
matrix panel's Platform/Configuration checkbox groups) when a target has
declared a matrix, to avoid two conflicting ways of picking the same thing
on screen at once.

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
- **Modify** `src/Omen.Core/Rules/TargetRules.cs`, `GemRules.cs`,
  `ModuleRules.cs` — document the optional `static TargetMatrix
  SupportedTargets` convention on all three (no base class change needed;
  it's read via reflection, same as today's constructor-shape convention);
  `ModuleRules`'s copy is documented as a membership gate, not a resolvable
  matrix in its own right (see Architecture)
- **Modify** `src/Omen.Executors/Orchestration/BuildOrchestrator.cs`,
  `ProjectGenerationOrchestrator.cs` — add the resolve-then-loop step and
  the per-combination module-gating check described above
- **Modify** `src/Omen.CLI/Commands/BuildCommand.cs`,
  `GenerateCommand.cs` — flags become filters; add `--dim`
- **Modify** VS project generator — multi-`<ProjectConfiguration>` emission
  per resolved target
- **Create** `src/Omen.GUI/ViewModels/MatrixPanelViewModel.cs` — resolves a
  target's declared matrix, exposes one checkbox-group collection per
  varying dimension, and produces the same per-dimension filter the CLI's
  `--dim` flags produce
- **Modify** `src/Omen.GUI/ViewModels/MainWindowViewModel.cs` — gains a
  `MatrixPanel` property, resolved on project load alongside the existing
  Options panel resolution; Build/Rebuild pass its filter through to the
  orchestrator when present
- **Modify** `src/Omen.GUI/Views/MainWindow.axaml`/`.axaml.cs` — new
  collapsible matrix pane (mirrors the Options pane's collapse pattern);
  Platform/Configuration `ComboBox`es hidden when a matrix is active

## Data flow

1. `RuleCompiler` compiles rules files as today; `CompiledRules` gains a way
   to read a discovered `TargetRules`/`GemRules` type's static
   `SupportedTargets` property via reflection, without constructing an
   instance (no `BuildContext` needed yet).
2. Orchestrator resolves the matrix (or the single today's-behavior
   fallback), applies any caller filter, gets `IReadOnlyList<ResolvedTarget>`.
3. For each `ResolvedTarget`: construct `BuildContext`, run
   `CreateTargetRules`/`CreateModuleRules` as today, then filter the module
   list down to those whose `SupportedTargets` (if declared) contains this
   `ResolvedTarget`, then run the existing `ActionGraphBuilder` →
   `ParallelExecutor` (build) or → VS generator (generate) pipeline against
   that filtered module list, unchanged.
4. Build: results aggregate across all resolved combinations (overall
   success = all succeeded); a failure in one combination doesn't stop
   others from running, mirroring how CI matrix builds typically report.
   Generate: one project file gains one more `<ProjectConfiguration>` per
   combination.
5. GUI only: on project load, `MainWindowViewModel` resolves the matrix (if
   any) into `MatrixPanelViewModel`'s checkbox groups, same as step 2's
   resolution; on Build/Rebuild, the panel's current checkbox state becomes
   the "caller filter" step 2 intersects against — no separate code path
   from the CLI's filter application.

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
- `TargetMatrix.Resolve()` returning zero combinations because `.Exclude`
  predicates removed everything the `.For` calls declared → a clear
  discovery-time error naming the target (this is a target-authoring
  mistake, distinct from the caller-filter-narrowed-to-zero case above,
  which is a caller mistake) — not a silent zero-action success.
- A module gated out of a combination is never an error, including when
  it's the module `LaunchModuleName` names — see Architecture. A
  combination left with zero included modules this way degrades to the
  existing "no modules to build" warning + zero-action success path, not a
  new error.

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
- `TargetMatrix.Exclude`: a predicate correctly drops matching combinations
  and leaves others untouched; multiple `.Exclude` calls compose (each
  removes its own matches); a target whose excludes remove every
  combination produces the discovery-time "matrix resolved to zero
  combinations" error, not a silent empty build.
- Module gating: a module with no `SupportedTargets` participates in every
  resolved combination (today's behavior, unchanged); a module whose
  `SupportedTargets` excludes a given `ResolvedTarget` is absent from that
  combination's action graph but present in others where it's included; a
  required (`LaunchModuleName`) module excluded from a combination produces
  that combination's specific error without affecting other combinations.
- `MatrixPanelViewModel`: resolves a target's matrix into the correct
  per-dimension checkbox groups (only varying dimensions get a group); the
  filter it produces from a given checkbox state matches what the
  equivalent CLI `--dim` flags would produce for the same selection.
