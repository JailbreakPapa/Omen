# Omen — O3DE Readiness Audit (2026-09-01)

Goal: Omen replaces CMake for an O3DE-family engine tree. "Same capabilities as CMake" means
covering what O3DE actually uses CMake *for* — not reimplementing the CMake language.

Baseline at audit time: clean build, 392/392 tests passing.

## Fixed in this pass

1. **Header edits now invalidate rebuilds** (correctness). Compile actions listed only the
   .cpp (+ PCH) as inputs, so editing a header rebuilt nothing. New `IncludeScanner`
   (Omen.Core/Graph) statically resolves each TU's transitive includes and records them as
   action inputs; PCH actions get the same treatment. Verified end-to-end on GemSample:
   no-op rebuild skips 6/6 actions; touching `GreeterRuntime.h` rebuilds exactly the 4
   dependent actions.
2. **Unity builds wired** (perf). `UnityBuildGenerator` moved into Omen.Core/Graph and hooked
   into `ActionGraphBuilder`; `UnityBuildExclusions` respected, excluded files still compile
   individually. Opt-in (`TargetRules.UseUnityBuild` now defaults false): flipping every
   project into unity the day the flag became functional would be a silent semantic change.
   Its self-defeating generation timestamp (forced a rebuild every run) removed.
3. **Pre/PostBuildSteps actually run.** `TargetRules.PreBuildSteps/PostBuildSteps` had zero
   consumers. BuildOrchestrator now runs them through the platform shell; a failing step
   without `ContinueOnError` fails the build. Pre-build steps run before the graph is built,
   so they can generate sources.
4. **LTO request override honored.** `BuildOrchestratorRequest.EnableLinkTimeOptimization`
   was stored-but-ignored; it now overrides `TargetRules.EnableLTO` like the unity/PCH
   overrides.
5. **`UsePCHFiles`/`UsePCH` vetoes honored** on the PCH action path.
6. **GemSample fixed.** It stopped building when a gem's Runtime flavor was renamed to the
   bare gem name (commit 1a02fc9) — the sample still depended on "Greeter.Runtime".
7. **Dead code removed / truth restored.** `IncludeAnalyzer` (unwired, superseded) deleted;
   README no longer claims distributed builds and unity/PCH work unconditionally.

## Capability matrix vs CMake-for-O3DE

| Capability | Status |
|---|---|
| Target/module model (`ly_add_target`, FILES_CMAKE, INTERFACE/STATIC/SHARED/EXECUTABLE) | ✅ ModuleRules/TargetRules/GemRules; 25 gem flavor kinds mapped to real O3DE target families |
| Per-config/per-compiler flags (`Configurations_*.cmake`) | ✅ GlobalToolchainOptions per CompilerFamily |
| Gem enablement + aliases (`ly_enable_gems`, `ly_create_alias`) | ✅ incl. multi-target and absolute aliases |
| Incremental builds | ✅ header-aware as of this pass (static scan; upgrade path: compiler depfiles `/showIncludes`, `-MD`) |
| Unity builds / PCH | ✅ opt-in unity; per-module PCH. Shared PCH + ForcedIncludes still inert |
| Build matrix (multi-platform/config in one invocation) | ✅ — beyond CMake. Per-gem matrix control still a no-op (`GemRules.SupportedTargets`) |
| IDE generation | ✅ VS2019/22/26 + compile_commands.json. No Ninja/Make/Xcode; emitted CMakeLists is a toy |
| Custom build steps (`add_custom_command`) | ⚠️ target-level pre/post steps now run; no per-output codegen actions in the graph yet |
| Third-party packages (`find_package`, O3DE 3rdParty manifest + download) | ✅ resolved from the local O3DE package store (landed later the same day, see addendum). Downloader still pending |
| Code generation (AzAutoGen, settings-registry `.setreg`, Qt moc/uic/rcc as build actions) | ❌ Qt is generator-only metadata; no graph actions |
| Testing (`ly_add_googletest`/CTest) | ❌ Tests flavors build, nothing runs them; no `omen test` |
| Install / export / packaging (`ly_install`, CPack) | ❌ `IsPrivateTarget` round-trips with no consumer |
| PAL per-platform fragments (`PLATFORM_INCLUDE_FILES`) | ⚠️ trait table wired per platform; fragments recorded, never evaluated — converter must hand-translate |
| Monolithic builds | ✅ folding + StaticModuleRegistration. `LinkType.Modular` unimplemented |
| Distributed builds / shared cache | ⚠️ local digest + action cache work; OmenNet has no agent implementation, `HybridExecutor` never constructed |
| Console platforms (restricted PAL overlays) | ❌ deliberate stubs (Prospero/Xbox) |
| Generator expressions | n/a — C# rules with a real BuildContext replace them outright |

## Roadmap (ranked by blocker order for the port)

1. ~~**Third-party package resolution.**~~ **Landed same day** — see the addendum below.
   Remaining slice: the downloader (missing packages currently must already be in the store).
2. **Codegen as graph actions.** A generic "this action produces these outputs from these
   inputs+command" node (the `Copy` type shows the shape), then AzAutoGen, `.setreg`
   generation, and Qt moc/uic/rcc on top. Prerequisite for AzCore/editor targets.
3. **`omen test`.** Discover Tests-flavor executables, run them, aggregate results.
4. **Install/export + packaging** (CPack equivalent) — needed for engine SDK snapshots.
5. **Single source of truth for include/define resolution.** ActionGraphBuilder,
   VisualStudioGenerator, and GenerateCMakeAsync each re-derive it; they drift.
6. **Depfile-accurate incrementality.** Swap the static include scan for compiler-emitted
   deps (`/showIncludes`, `-MD -MF`) captured at execution time.
7. **Distributed builds: finish or fold.** Implement the agent + route `--distribute`, or
   delete the OmenNet surface until it's real — advertised-but-inert is the worst state.
8. Smaller: per-gem `SupportedTargets`, `LinkType.Modular`, shared PCH, adaptive unity
   (needs a digest-stable design), console toolchains (NDA-gated).

## Addendum (same day): third-party package resolution landed

Design: two-tier, local-cache-first (per review decision).

- `BuiltInPackagesReader` regex-parses the tree's own `ly_associate_package(...)` calls
  under `cmake/3rdParty/**` — no duplicated package table, no drift.
- `PackageStore` resolves package names against `LY_3RDPARTY_PATH`
  (default `~/.o3de/3rdParty/packages`). No downloader yet — a missing package surfaces
  through the existing unresolved-dependency warning.
- `FindModuleParser` evaluates the restricted CMake subset the per-package
  `Find<X>.cmake` files are written in (vars, conditionals, config generator expressions,
  imported targets, interface includes/defines/link libs, foreach, inert user functions).
  Anything outside the subset throws, naming the package and the fix — never a silent
  mistranslation. **Coverage against the real local store: 57 of 59 packages**; the two
  holdouts are OpenEXR (imports official CMake config-file packages — override-tier
  material) and Qt (excluded by design; Qt has Omen's own `UseQt` path).
- `ThirdPartyPackageResolver` (hooked into `CompiledRules.CreateModuleRules`) turns each
  referenced `3rdParty::X` into a synthesized HeaderOnly module carrying the package's
  include paths, prebuilt libraries, and definitions — the existing "a third-party package
  IS a module" shape, so the action graph needed zero changes. Cross-package references
  chase transitively. A hand-written module named `3rdParty::X` wins outright (the
  override tier).

Proof: `examples/GemSample` now declares `ThirdPartyDependencies.Add("3rdParty::Lua")`
against an O3DE-shaped `cmake/3rdParty/BuiltInPackages.cmake`; the built exe runs real Lua
from the package store (`Lua 5.4 says: 42`).

Follow-ups: downloader for O3DE packages (HttpClient + SHA256 + `tar -xf` — the archive
machinery now exists in ContentFetcher), routing the real toolchain's compiler family into
`$<CXX_COMPILER_ID>` evaluation.

## Addendum 2 (same day): Omen beyond O3DE — FetchContent + vcpkg

Omen is a general build system, not an O3DE port tool; two package sources landed that owe
nothing to O3DE:

- **`OmenProjectRules.FetchContent`** — CMake-FetchContent equivalent. Declares a git
  repository (tag/branch/commit) or archive URL (optional SHA256) per dependency; fetched
  into `External/<Name>` at rule-discovery time (stamp-guarded, refetches on any spec
  change), and the fetched directory joins the rule-file scan roots — a fetched tree
  carrying its own `*.module.cs` participates with no second configure pass. Plain C++
  libraries are consumed by writing a module over the fetched sources. Limitation by
  design: Omen does not run a fetched project's foreign build system (no nested CMake).
- **`Vcpkg::<port>` dependencies** — resolved from a vcpkg installed tree (project
  manifest-mode `vcpkg_installed/`, else `%VCPKG_ROOT%/installed`; triplet from
  platform/arch or `VCPKG_DEFAULT_TRIPLET`). No CMake involved: vcpkg's own install
  database (`info/*.list` + `status`) supplies per-config libs, the shared include tree,
  port DLLs, and transitive port dependencies (build-helper `vcpkg-*` ports skipped).
- **`ModuleRules.RuntimeFiles`** — concrete files (vcpkg DLLs, O3DE `ly_add_target_files`
  entries) now staged next to the target output via Copy actions, which closes the
  previous addendum's staging follow-up.

Proof: GemSample's exe now mixes all three sources in one binary — gem DLL + O3DE-store
Lua (static) + vcpkg zlib (dynamic, `zlib1.dll` auto-staged): it prints
`Lua 5.4 says: 42` and `zlib (from vcpkg) version: 1.3.1`.

## Addendum 3 (same day): codegen graph actions

`ModuleRules.CodeGenSteps` — the `add_custom_command` equivalent, closing roadmap item 2's
generic half. Each step is a real graph action (reusing the dormant `ActionType.Custom`):

- Declared Inputs/Outputs are tracked, so timestamp/digest incrementality applies to
  generation exactly as to compilation; a step with no Outputs is a hard error.
- Every compile (and the PCH) in the module orders after every step - generated headers
  exist before anything includes them.
- Declared outputs ending `.cpp`/`.c`/`.cc` compile into the module (joining after unity
  substitution, since they don't exist on disk at graph-build time); a module whose every
  TU is generated works.
- Relative Inputs resolve against the module source dir, Outputs against the module
  intermediate dir (which auto-joins the module's include paths); commands and paths take
  `$(ModuleDir)`/`$(IntermediateDir)`/`$(ProjectRoot)`/`$(OutputDir)`.

Two incremental-correctness bugs found while proving convergence: the include scanner
returned set-ordered inputs, making action digests unstable (now sorted), and the
up-to-date check used strictly-newer, so every timestamp-preserving Copy (dll staging)
reran forever (now make-style `>=`). GemSample no-op rebuilds now skip 10/10 actions, and
its exe prints a generated `#define` and links a generated TU.

AzAutoGen / `.setreg` / moc as *built-in* steps remain follow-ups - they are now
expressible as CodeGenSteps without touching the graph.

## Still-inert flags (documented in code, kept for compat)

`UseAdaptiveUnityBuild`, `UseIncrementalLinking` (linker path hardcodes it),
`SharedPCHHeaderFile`, `ForcedIncludes`, `EnableDistributedBuild`, `LinkType.Modular`,
GUI `LocalAgentJobs`.

