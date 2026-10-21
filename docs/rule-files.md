# Omen rule files: `.target.cs` vs `.module.cs`

Omen builds are described by C# classes in four kinds of rule files, discovered by file
pattern and compiled together by Roslyn. The two you write daily are targets and modules;
this page is mostly about the difference between them.

| File | Base class | One sentence |
|---|---|---|
| `*.target.cs` | `TargetRules` | **What** to build: one shippable product and the modules that go into it |
| `*.module.cs` | `ModuleRules` | **How** one library/unit of code compiles and what it depends on |
| `*.gem.cs` | `GemRules` | An O3DE-style gem: a bundle of module *flavors* (Static/Runtime/Editor/...) expanded into ordinary modules |
| `*.omenproject.cs` | `OmenProjectRules` | Project-wide settings: source roots, enabled gems, PAL traits, engine-wide compiler/linker options |

## The mental model

A **module** is a unit of code — a library, usually one folder of sources. It describes
itself: where its sources are, what it needs to compile (includes, definitions), and what
it depends on. Modules are reusable; they don't know or care which product they end up in.

A **target** is a product — a game executable, an editor, a dedicated server, a tool. It
names an entry-point module (`LaunchModuleName`), pulls in extra modules, and makes
product-wide decisions: monolithic vs modular linking, which platforms/configurations it
supports, global definitions, LTO, unity builds, pre/post build steps.

One `omen build <X>.target.cs` = one target = one output binary (plus the module binaries
its `BinaryType`-carrying modules produce). Many targets can share the same modules — a
`Game.target.cs` and `Server.target.cs` reusing `Core.module.cs` is the intended shape.

If you know other build systems:

| | Target | Module |
|---|---|---|
| Unreal (UBT) | `*.Target.cs` | `*.Build.cs` |
| CMake | top-level `project()` + the executable and its config | `add_library()` + `target_include_directories`/`target_link_libraries`/`target_compile_definitions` |

## What goes where

**`TargetRules` (product-wide):**
- `Type` — Executable / StaticLibrary / SharedLibrary
- `LaunchModuleName`, `ExtraModules` — which modules are in this product. The build only
  compiles the dependency *closure* reachable from these seeds.
- `LinkType.Monolithic` — fold all shared-library modules into one binary (generates
  `StaticModuleRegistration.g.cpp`)
- `SupportedTargets` (static `TargetMatrix`) — every platform/arch/config combination one
  invocation builds
- `GlobalDefinitions` — defines applied to every module in this target's build
- `UseUnityBuild` (opt-in), `UsePCHFiles`, `EnableLTO`, `GenerateDebugInfo`
- `PreBuildSteps` / `PostBuildSteps` — shell commands around the build (pre-steps run
  before the action graph is built, so they may generate sources)

**`ModuleRules` (per-unit-of-code):**
- `SourceDirectory` (globbed) or explicit `SourceFiles` (authoritative, like O3DE's
  FILES_CMAKE — lets several modules share one folder)
- `PublicDependencies` / `PrivateDependencies` — other modules. *Public* propagates your
  include paths/definitions to whoever depends on you; *private* doesn't.
- `PublicIncludePaths` / `PrivateIncludePaths`, `PublicDefinitions` / `PrivateDefinitions`
  (a `Public/` subfolder under the module's source dir is picked up automatically)
- `ThirdPartyDependencies` — prebuilt package references: `3rdParty::X` (O3DE package
  store) or `Vcpkg::X` (a vcpkg installed tree, via `VCPKG_ROOT` or manifest-mode
  `vcpkg_installed/`). Both resolve to real include paths, per-config libs, and staged
  runtime DLLs.
- `BinaryType` — set it and the module links as its own binary (dll/lib); leave it null
  and its objects fold into the target's link
- Per-module compile settings: `CppStandard`, `WarningLevel`, `EnableRTTI`,
  `EnableExceptions`, `OptimizeCode`, PCH file, unity opt-in/exclusions
- `CodeGenSteps` — build-time code generation (the `add_custom_command` role): each step's
  command runs before the module compiles, its outputs are tracked incrementally, generated
  `.cpp`/`.c` outputs compile into the module, and the module's intermediate directory
  (where relative outputs land) joins its include paths. Tokens: `$(ModuleDir)`,
  `$(IntermediateDir)`, `$(ProjectRoot)`, `$(OutputDir)`.
- `RuntimeDependencies` — needed next to the output at runtime, never linked
- `ForbiddenDependencies` — layering rules, enforced with a reason

Rule of thumb: if removing a module from the product would flip the setting, it belongs on
the target; if the setting travels with the code wherever it's used, it belongs on the
module. `GlobalDefinitions` vs `PublicDefinitions` is the sharpest example: the first is
"this product defines X everywhere", the second is "using this module implies X".

## The other two, briefly

- **`GemRules`** (`*.gem.cs`): declares which flavors a gem builds (`DefineFlavor`) and the
  aliases O3DE code references (`CreateAlias("Clients", ...)`). Each flavor is expanded
  into an ordinary module before the graph ever sees it — a gem's Runtime flavor becomes a
  module named after the gem itself, matching O3DE's `ly_add_target NAME ${gem_name}`.
- **`OmenProjectRules`** (`*.omenproject.cs`, at most one, project root): `SourceRoots`,
  `ExternalSubdirectories`, `EnabledGems`/`Variant` (O3DE's `ly_enable_gems`),
  `PlatformTraits` per platform (PAL traits consulted by `RequiredTraits` gates),
  `ToolchainOptions` per compiler family (the `Configurations_msvc.cmake` equivalent), and
  `FetchContent` — Omen's CMake-FetchContent equivalent: declare a git repo (+ tag/branch/
  commit) or archive URL (+ SHA256) and it is fetched into `External/<Name>` before rule
  discovery, so a fetched tree's own `*.module.cs` files participate directly; for a plain
  C++ library, write a module whose `SourceDirectory` points into the fetched tree.

Both constructors receive a `BuildContext`, so platform/config branching is plain C#:
`ConfigureForPlatform(TargetPlatform.Windows, () => ...)` — this is what replaces CMake's
generator expressions.

Working examples: `examples/ExampleGame/` (target + modules, PCH) and
`examples/GemSample/` (gem flavors, aliases, `TargetMatrix`, build options).

