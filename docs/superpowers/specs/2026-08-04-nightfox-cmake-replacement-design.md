# Omen ↔ NightFox: replacing CMake, phase 1

## Context

**Omen** (this repo) is a UBT-style build tool: C# `*.module.cs`/`*.target.cs` rules,
a content-addressed action graph, MSVC/Clang/Android/Apple toolchains, a Visual Studio
project generator, unity builds/PCH, and an in-progress distributed layer (OmenNet).

**NightFox** (`F:\engine`) is a fork of **O3DE** (Open 3D Engine), not a from-scratch
engine. Its CMake build spans 401 `CMakeLists.txt` files across `Code/`, 100+ `Gems/`,
`Templates/`, `Tools/`, `Assets/`, plus `restricted/Prospero` (PS5) and `restricted/Xbox`
overlays. It has concepts Omen has no equivalent for: Gems (plugin modules with a
`gem.json` manifest and 2-4 build flavors + alias targets), a Platform Abstraction Layer
with per-platform overlay directories, monolithic-build static-module registration,
a settings-registry codegen step, runtime-dependency staging, a Test Impact Framework,
and CPack packaging.

**LuminaBuildTool** (github.com/MrDrElliot/LuminaEngine) is the same category of tool as
Omen — also UBT-inspired — built for a from-scratch engine with none of the above (no
Gems, no PAL, no packaging, Windows/MSVC only). Its value here is a handful of proven
design decisions, not a template to copy wholesale.

### Decision this spec resolves

A full CMake replacement (all 401 files, PAL, packaging, asset pipeline) is a multi-phase
project. This spec covers **Phase 1**: harden Omen's own architecture with Lumina's best
ideas, add the Gem concept Omen currently has zero support for, and prove the combination
on one real Gem, running side-by-side with the existing CMake build rather than replacing
it yet.

Confirmed out of scope for this spec (tracked as later phases, not dropped):
- Settings-registry codegen, runtime-dependency staging, Test Impact Framework, CPack
  packaging, Python asset-pipeline integration.
- Migrating the remaining ~400 `CMakeLists.txt` files.
- Console (PS5/Xbox) `IToolchain` implementations. The team has console SDK access, but
  actually writing those toolchains is deferred — this spec only guarantees the extension
  seam exists so they can be dropped in later without touching Omen core.

## Current-state findings (informs every decision below)

From reviewing `src/Omen.Core`, `src/Omen.Platforms`, `src/Omen.CLI`, `src/Omen.Optimizations`:

- `PlatformFactory.DiscoverAllSdks()` is a hardcoded array literal of concrete SDK types.
  `TargetPlatform` is a closed enum. An unused `NDAPlatforms` enum (PS4/PS5/XB1/XBX/NS1/NS2)
  exists but is wired into nothing.
- `TargetRules.LinkType` (`Default`/`Monolithic`/`Modular`) is declared and never read by
  `ActionGraphBuilder` or `VisualStudioGenerator`. Monolithic linking does not work today.
- Source discovery is a recursive directory walk over `ModuleRules.SourceDirectory`
  (`*.cpp`/`*.c`), not an explicit file list — closer to Lumina than to O3DE's
  `FILES_CMAKE`/`*_files.cmake` pattern.
- `PublicDependencies`/`PrivateDependencies` (and matching include-path/definition pairs)
  exist on `ModuleRules` and propagate correctly (public-only, one level) in
  `ActionGraphBuilder`.
- No layering/forbidden-dependency check exists anywhere (`grep -rn "Forbid|Layering"` is
  empty). Only cycle detection exists.
- `VisualStudioGenerator` emits real MSBuild `.vcxproj` files with their own independent
  include-path/definition resolution — a second implementation that can drift from
  `ActionGraphBuilder`'s actual command lines. No NMake-style shell-out exists.
- `Omen.Distributed` ("OmenNet") has a real coordinator, agent registry, and operation
  queue, but no concrete `IOmenAgent` implementation and no wiring from `BuildCommand`
  (`--distribute` is parsed and never read downstream). Distributed builds do not function
  end-to-end today. Out of scope for this spec; noted because Phase A must not regress it.
- Nothing resembling a Gem (manifest + multiple build flavors + alias targets) exists.
  `ModuleRules` is one class = one build artifact.

## Phase A — Omen core hardening

Platform-agnostic; benefits Omen independent of NightFox.

### A1. Pluggable platform registry

`PlatformFactory` keeps its built-in `IPlatformSDK` list, and additionally scans for
extra implementations under `restricted/<Platform>/omen/*.dll` (or a project-configured
extra-platforms directory) at startup, loading and registering any `IPlatformSDK` found
via reflection. This reuses O3DE's own `restricted/<Platform>` overlay convention instead
of inventing a new one, and is what makes a future console toolchain a drop-in rather
than a core edit.

`TargetPlatform` gains `Prospero` and `Xbox` enum members now (they're known, named
platforms — no need for a fully dynamic string-typed platform system). Both ship in this
phase as `IPlatformSDK`/`IToolchain` stub classes that throw `NotImplementedException`
with a comment pointing at where the real compiler/linker invocation goes — implementing
the actual toolchains is separate follow-up work once someone sits down with the console
SDKs, but the registry, enum members, and stub classes land now so that work is additive.

### A2. Command-line-hash invalidation

`ActionGraph`'s up-to-date check currently compares file mtimes. Change it to also check
`BuildAction.ComputeDigest()` (already hashes the command line) against the digest
recorded for that output on the previous build. A `.module.cs`/`.target.cs` edit that
changes no compiler flag then invalidates nothing; an edit that does invalidates exactly
the actions whose command line changed, plus their dependents.

### A3. Layering / forbidden-dependency checks

New on `ModuleRules`:

```csharp
public List<(string ModuleName, string Reason)> ForbiddenDependencies { get; } = new();
```

A validation pass runs after `RuleCompiler` resolves the full module graph and before
`ActionGraphBuilder` builds actions. For every module, it walks the full transitive
closure of `PublicDependencies`/`PrivateDependencies` and fails the build if a forbidden
module is reachable, reporting the shortest path found and the quoted reason. Two checks
apply with no declaration required: a module of `ModuleType.ThirdParty` may never depend
on a non-third-party module, and a target's own `ForbiddenDependencies` are checked across
its whole resolved closure (routing through an intermediate module doesn't evade it).
A blank or missing reason is a build error — the check exists to keep a rule's rationale
attached to it.

### A4. Monolithic linking

`ActionGraphBuilder` reads `TargetRules.LinkType`:
- `Modular` (today's only working path): unchanged, per-module shared libraries.
- `Monolithic`: every included module (including ones flavor-configured as a shared
  library) is archived as a static lib instead, and one final link produces the target
  binary. A generated `StaticModuleRegistration.g.cpp` (written to
  `IntermediateDirectory`, added as a compile input of the launch module) lists the
  entry-point symbol of every included Gem runtime module — Omen's equivalent of O3DE's
  `StaticModules.inl`, generated by Omen rather than configured by CMake.

### A5. Visual Studio projects stop being a second build description

Target projects (the ones that actually build something) switch from real MSBuild
item/property groups to NMake-style projects whose Build/Rebuild/Clean commands shell
into `omen build <target> ...`. Module (browse-only) projects keep real include-path and
definition lists for IntelliSense accuracy but carry no build command — same three-way
split Lumina documents (target / module / rules projects). This closes the drift risk
called out above: there is exactly one place that decides compiler flags.

A `compile_commands.json` writer is added alongside, sourced from the same
`ActionGraphBuilder` command lines (not a third independent implementation), for clangd
and non-VS editors.

## Phase B — Gem model

New concept; nothing today models a Gem. `gem.json` stays authoritative for identity,
version, and dependencies — it's read by O3DE tooling outside the build (Project Manager,
gem repo), so Omen doesn't get to redefine it. This is the approach-B split confirmed with
the user: Lumina's own `.lplugin` descriptor + separate `Build.cs` files follow the same
pattern.

### B1. `GemRules`

New abstract class in `Omen.Core.Rules`, subclassed by a `<GemName>.gem.cs` file placed
at the gem's `Code/` root (sibling to where `CMakeLists.txt` sits today). Its constructor
reads the gem's `../gem.json` (one directory up, matching O3DE's layout) via a small
`GemManifest` JSON reader for `gem_name`, `version`, and `dependencies` — these become the
gem's default public dependencies. A flavor may add further private dependencies, but may
not redeclare what `gem.json` already states, so there is one source of truth.

### B2. Flavors

`GemRules` exposes a fixed set of flavors: `Static`, `Runtime`, `Editor`, `Tools`. Each is
configured like a `ModuleRules` block — its own source directory, its own private
dependencies — while sharing the gem-level public dependencies from `gem.json`.
`Runtime`'s binary type follows the target's `LinkType` (shared library when `Modular`,
static-with-registration-entry when `Monolithic`), matching O3DE's
`PAL_TRAIT_MONOLITHIC_DRIVEN_MODULE_TYPE`.

### B3. Aliases

`GemRules.Aliases` maps the symbolic names O3DE targets reference (`Clients`, `Servers`,
`Unified`, `Tools`, `Builders`) to one of the gem's flavors. `TargetRules.ExtraModules`
accepts `"Gem::Camera.Clients"`-style references; resolving one looks up the alias table
rather than requiring the target to know which concrete flavor backs it.

### B4. Discovery

`RuleCompiler` is extended to also find non-abstract `GemRules` subclasses (alongside
today's `ModuleRules`/`TargetRules` scan) and expand each into its 2-4 concrete
`ModuleRules` instances before handing off to `ActionGraphBuilder`. Everything in Phase A
(layering checks, monolithic linking, VS generation, the action graph itself) stays
Gem-unaware — by the time it sees the graph, a Gem is just modules.

## Phase C — Pilot: `Gems/Camera`

Chosen because it's small, has all the interesting shapes (Static + Runtime + Editor
flavors, a real gem-to-gem dependency on `Atom_RPI`, a definition injected into one
specific source file), and is well understood.

- Author `Gems/Camera/Camera.gem.cs` using B1-B3, translating
  `Gems/Camera/Code/CMakeLists.txt` flavor-for-flavor.
- **Parity check, not a cutover** (per the user's explicit choice): CMake keeps building
  `Camera` exactly as it does today; nothing is removed from the CMake tree. A parity
  script captures CMake's real compiler invocations for each flavor (MSBuild `/v:diag` or
  Ninja verbose output), captures Omen's derived command lines for the same flavor, and
  diffs normalized include paths, definitions, PCH usage, and output naming. Mismatches
  are reported; this stays a standing check for as long as both systems build the gem, not
  a one-time validation.
- A short written guide capturing the C1→C2 pattern, so migrating the next Gem doesn't
  require re-deriving the approach.

## Testing

- **A2/A3/A4** are pure graph-construction logic — unit tests in `Omen.Core.Tests`
  following the existing pattern (`ActionGraphTests.cs`, `ModuleRulesTests.cs`): a
  forbidden-dependency closure test with an intermediate hop, a third-party-depends-on-
  first-party rejection test, a monolithic-vs-modular link-action shape test, a
  digest-based invalidation test (edit a rule property that doesn't change the command
  line → nothing invalidated; edit one that does → only dependents invalidated).
- **A1** (platform registry): a test double `IPlatformSDK` in a separate test assembly,
  loaded from a temp "restricted-style" directory, confirming discovery without a
  `PlatformFactory` code change.
- **A5**: generate a project for a small fixture target, assert the `.vcxproj` contains an
  `NMakeBuildCommandLine` and no per-file compile item list; assert
  `compile_commands.json` entries match `ActionGraphBuilder`'s command lines byte-for-byte
  after normalization.
- **B1-B4**: unit tests analogous to existing `ModuleRulesTests`/`TargetRulesTests`, plus
  one using a fixture gem (small, checked into `tests/` — not a real O3DE gem) exercising
  manifest reading, flavor expansion, and alias resolution end to end.
- **Phase C** is its own test: the parity script's diff passing *is* the acceptance
  criterion for the pilot. No unit test substitutes for actually building `Camera` both
  ways and comparing.

## Risks / open questions carried into implementation

- `gem.json`'s `dependencies` array names other gems by their `gem_name`, not by O3DE
  target/module names — B1's manifest reader needs to resolve gem-name → concrete module
  names, which requires the same gem-discovery pass to have already run over all enabled
  gems. Sequencing this against `RuleCompiler`'s existing single-pass discovery needs
  care during implementation; flagging rather than resolving here since it's an
  implementation detail, not a design fork.
- A4's monolithic registration file needs each Gem's runtime module to expose a
  discoverable entry-point symbol; Camera's actual `CameraGem.cpp` module class name is
  the concrete case Phase C will validate this against.
