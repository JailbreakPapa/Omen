# NightFox framework libraries → Omen: AzCore, AzFramework, CryCommon

## Context

Phase 1 (`2026-08-04-nightfox-cmake-replacement-design.md`) added the `GemRules` concept
to Omen and proved it on one gem (`Gems/Camera`), then extended to 6 more leaf gems
(GameState, SaveData, StartingPointMovement, CertificateManager, Presence,
TickBusOrderViewer) — all verified with a clean `omen build --dry-run`, CMake left
untouched and authoritative for every one of them.

Every one of those 7 gems declares `PrivateDependencies`/`PublicDependencies` on
`AZ::AzCore`, `AZ::AzFramework`, or `Legacy::CryCommon` (the O3DE framework libraries
every gem builds against). None of those three exist as Omen modules today, so those
dependency names silently resolve to nothing — `ActionGraphBuilder.BuildIncludePaths`'s
`_moduleDict.TryGetValue` lookup just misses and contributes no include paths. Every gem
converted so far is missing its framework include paths in its dry-run output. This was
explicitly deferred in Phase 1's spec ("Migrating the remaining ~400 CMakeLists.txt
files" was out of scope) — this spec is that deferred work, narrowly scoped to just
enough of the framework tree to close the dependency-resolution gap.

## Decision this spec resolves

Convert only the **3 primary library targets** — `AzCore`, `AzFramework`, `CryCommon` —
to Omen `.module.cs` files, Windows-only, using the exact `ly_add_target` →
`ModuleRules` translation pattern already proven on 7 gems. Everything else in
`Code/Framework/` and `Code/Legacy/` (test infrastructure, other framework libraries
like `AzToolsFramework`, `AzNetworking`, `AtomCore`) stays out of scope — this closes
the one gap blocking the gems already converted, not a general framework migration.

### Current-state findings

- `AzCore/CMakeLists.txt` (224 lines, 6 `ly_add_target` calls): the primary `AzCore`
  target uses `NAME AzCore ${PAL_TRAIT_MONOLITHIC_DRIVEN_LIBRARY_TYPE}` (the same PAL
  indirection already handled transparently by Omen's `TargetRules.LinkType` +
  `ActionGraphBuilder`'s monolithic-folding logic from Phase 1 Task 4 — no new work
  needed for this), `FILES_CMAKE` across `AzCore/azcore_files.cmake`,
  `AzCore/std/azstd_files.cmake`, and a per-platform file list; `INCLUDE_DIRECTORIES
  PUBLIC . ${pal_dir} ${common_dir}`; `COMPILE_DEFINITIONS PRIVATE AZCORE_EXPORTS`. 268
  `.cpp` files. The other 5 targets (`AzCoreTestCommon`, `AzTestShared`,
  `AzCoreTestDLL`, `AzCore.Tests`) are test infrastructure — none of the 7 gems depend
  on them.
- `AzFramework/CMakeLists.txt` (167 lines, 6 targets): same `PAL_TRAIT_MONOLITHIC_DRIVEN_
  LIBRARY_TYPE` pattern for the primary `AzFramework` target, plus a secondary
  `AzFramework.NativeUI` target and 4 test targets. Only `AzFramework` itself is in
  scope — none of the 7 gems reference `AzFramework.NativeUI`.
- `Legacy/CryCommon/CMakeLists.txt` (42 lines, 2 targets): `CryCommon STATIC` (in
  scope — `TickBusOrderViewer` depends on it) + `CryCommonMocks HEADERONLY` (out of
  scope, nothing depends on it).
- `${pal_dir}`/`${common_dir}` resolve, on Windows, to fixed real directories under each
  library's own tree (e.g. `AzCore/Platform/Windows`, `AzCore/Platform/Common`) — these
  get hardcoded to their real Windows paths in the `.module.cs`, the same way the 6
  leaf gems already hardcode Windows-only assumptions (no PAL abstraction exists in
  Omen yet, and building one is bigger scope than this spec needs).

## Scope

**In scope:**
- `Code/Framework/AzCore/AzCore.module.cs` — `AzCore`'s public include root (`.`,
  matching the nested `AzCore/AzCore/` namespace-folder convention already confirmed
  during the gem batch), Windows PAL/common include dirs, `AZCORE_EXPORTS` as a private
  definition, `SourceDirectory` covering `azcore_files.cmake`'s and `azstd_files.cmake`'s
  source tree (both live under `AzCore/AzCore/` and `AzCore/AzCore/std/`).
- `Code/Framework/AzFramework/AzFramework.module.cs` — same treatment for the
  `AzFramework` target.
- `Code/Legacy/CryCommon/CryCommon.module.cs` — same treatment for `CryCommon`
  (public include root is `.` itself per its own CMakeLists.txt comment: "Lots of code
  without CryCommon/").
- `BinaryType` set to match each library's real link type (`StaticLibrary` — matching
  what `${PAL_TRAIT_MONOLITHIC_DRIVEN_LIBRARY_TYPE}` resolves to for a non-monolithic
  Windows dev build) so a consuming target's `LinkType` (Modular/Monolithic) drives
  linking the same way it already does for gem `Runtime` flavors.
- Re-verify all 7 already-converted gems' `omen build --dry-run` and confirm real
  framework include paths now appear.

**Out of scope (explicitly, not an oversight):**
- Every other framework library (`AzToolsFramework`, `AzNetworking`, `AzGameFramework`,
  `AtomCore`, `AzQtComponents`, `AzManipulatorTestFramework`, `AzAndroid`) and all of
  `Code/Legacy/` beyond `CryCommon`.
- Test targets for all three libraries in scope.
- Non-Windows PAL directories/platforms.
- Actually compiling AzCore/AzFramework/CryCommon's 268+ source files for real (this
  spec's acceptance test is dry-run parity — real compilation is Phase C's own
  eventual scope, unblocked but not exercised here).
- A general PAL-directory-resolution abstraction in Omen (each `.module.cs` hardcodes
  its real Windows paths, same as every gem so far).

## Testing

- Author the 3 `.module.cs` files.
- `omen build --dry-run` for each of the 7 already-converted gems (from their own
  directories, same invocation pattern already established), confirming the dry-run
  output's `/I` include paths now contain AzCore's/AzFramework's/CryCommon's real
  directories instead of silently omitting them.
- No new Omen.Core code changes are anticipated — this is authoring within the
  capabilities Phase 1 already built (`ModuleRules`, `BinaryType`, `LinkType`-driven
  folding). If AzCore's actual `FILES_CMAKE`/`INCLUDE_DIRECTORIES` structure turns out
  to need something `ModuleRules` doesn't support once its full 268-file source tree is
  read in detail, that's a finding to surface, not something to design around blind.

## Risks / open questions

- `azcore_files.cmake` and `azstd_files.cmake` are two separate file lists rather than
  one flat directory walk; `ModuleRules.SourceDirectory`'s convention (recursive walk
  of one directory) may need `SourceDirectory` pointed at their common parent
  (`AzCore/AzCore`) rather than trying to express two separate lists — needs confirming
  against the actual file contents during implementation, not assumed here.
- AzCore is known to use platform-conditional code generation and heavy template use;
  268 files compiling cleanly through Omen's plain `.cpp`/`.c` directory walk (no
  custom per-file exclusions) is an assumption to verify, not a guarantee — if specific
  files need exclusion (e.g. platform variants for non-Windows), that surfaces during
  the dry-run verification step and gets handled as a normal `UnityBuildExclusions`-style
  list, not a design change.
- **The Testing section's "re-verify all 7 gems' dry-run" step does not actually show
  framework include paths, and this is not a bug in `AzCoreModule`/`AzFrameworkModule`/
  `CryCommonModule`.** `RuleCompiler.CompileRulesAsync` (`src/Omen.Core/Rules/RuleCompiler.cs:34-36`)
  discovers `*.module.cs`/`*.target.cs`/`*.gem.cs` via
  `Directory.GetFiles(projectRoot, ..., SearchOption.AllDirectories)` — recursive but
  strictly *downward* from `projectRoot`. `ExecuteDryRunAsync` (`src/Omen.CLI/Commands/
  BuildCommand.cs:273,290`) sets `projectRoot`/`workingDir` to
  `Path.GetDirectoryName(targetFile)`, i.e. the invoked target's own directory (e.g.
  `Gems\GameState`), regardless of the shell's current directory. Since
  `Code\Framework\AzCore\AzCore.module.cs` lives outside `Gems\GameState`'s subtree (and
  every other gem's), a gem's own dry-run can never discover it — the gem's declared
  `PrivateDependencies.Add("AZ::AzCore")` silently resolves to nothing, exactly as
  before this spec's 3 `.module.cs` files existed. This is a pre-existing Omen.Core
  discovery-scope property (by design, per Phase 1's gem-scoped-directory pilot), not
  something fixable from the NightFox-repo side.
  - Verified 2026-08-19: each of the 3 new `.module.cs` files is individually correct.
    A throwaway `_VerifyPilot.target.cs` (`Type = TargetType.StaticLibrary;
    LaunchModuleName = "AZ::AzCore"` / `"AZ::AzFramework"` / `"Legacy::CryCommon"`, run
    via `omen build VerifyPilot --dry-run` from each library's own directory) produced
    real `[Compile]` actions for every source file with the module's own
    `PublicIncludePaths` present on every command line —
    `/I"...\Code\Framework\AzCore"` and `/I"...\Code\Framework\AzCore\AzCore"` on all of
    them for AzCore, same pattern for AzFramework, and
    `/I"...\Code\Legacy\CryCommon"` + `/I"...\Code\Legacy"` + the Windows PAL dir on all
    of them for CryCommon. Zero errors/exceptions in any of the three.
    **Corrected count** (2026-08-19 final review): the real per-target primary-source
    counts are AzCore 266, AzFramework 248, CryCommon 4 (CryCommon's count was already
    exact). Omen's dry-run instead reported 268 / 250 / 4 — 2 higher than correct for
    each of AzCore and AzFramework — because `SourceDirectory`'s recursive, unfiltered
    walk also picks up 2 files each that belong to out-of-scope sibling CMake targets,
    not the primary target being converted: `AzCore/UnitTest/MockComponentApplication.cpp`
    belongs to `AzCoreTestCommon`, and `AzFrameworkNativeUIModule.cpp` +
    `Components/NativeUISystemComponent.cpp` belong to `AzFramework.NativeUI` — both
    targets explicitly out of scope for this plan. This is a documentation-accuracy
    correction only, not a bug in the 3 `.module.cs` files: real compilation was never
    this plan's acceptance test, only dry-run include-path resolution was, and that
    remains correct. Each target's count is also short 27 (AzCore) / 8 (AzFramework)
    PAL-platform files this plan's Windows-only scope doesn't include. Confirmed as a side effect:
    AzFramework's own `PrivateDependencies.Add("AZ::AzCore")` and CryCommon's
    `PublicDependencies.Add("AZ::AzCore"/"AZ::AzFramework")` show the identical gap —
    the cross-directory sibling include paths never appear in their own compile lines
    either, with no warning/error emitted (a silent miss, not a thrown error) — so the
    gap is symmetric and not specific to gem→framework lookups.
  - Then re-ran `GameStatePilot --dry-run` from `Gems\GameState` (which already declares
    `staticFlavor.PrivateDependencies.Add("AZ::AzCore")` correctly in `GameState.gem.cs`
    — the gem's own authoring was never at fault): output still shows only
    `/I"...\Gems\GameState\Code/Source"`, no `AZ::AzCore` include path anywhere,
    confirming the remaining gap is exactly the discovery-scope issue above, not an
    authoring mistake in this spec's 3 modules or in any of the 7 gems.
  - **Real remaining blocker (out of this spec's scope — an `Omen.Core`/`RuleCompiler`
    change):** `RuleCompiler` needs a way to discover rule files outside the invoked
    target's own directory subtree — e.g. walking upward to a project root / workspace
    marker, or an explicit additional-search-paths mechanism — before a gem's dry-run
    (or real build) can resolve dependencies on `Code/Framework/*` or `Code/Legacy/*`
    libraries. Until that lands, the 7 already-converted gems' dry-runs remain missing
    their framework include paths despite this spec's `.module.cs` files being correct.

