# Framework Library Conversion (AzCore/AzFramework/CryCommon) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Author `.module.cs` files for AzCore, AzFramework, and CryCommon's primary library targets so the `AZ::AzCore`/`AZ::AzFramework`/`Legacy::CryCommon` dependency names the 6 already-converted leaf gems already reference (and silently fail to resolve today) resolve to real include paths (gem-level resolution blocked on a separate RuleCompiler discovery-scoping gap — see Task 4 and this plan's Self-review notes).

**Architecture:** Three independent, near-identical authoring tasks (Tasks 1-3, no dependency between them — safe to hand to separate workers), each translating one framework library's `ly_add_target` primary-target block into a plain `ModuleRules` subclass using the two-argument constructor `ModuleRules(context, explicitName)` so the module's `Name` matches the exact `"AZ::AzCore"`/`"AZ::AzFramework"`/`"Legacy::CryCommon"` strings the gems already use (a plain C# class name can't contain `::`). Task 4 (depends on 1-3) re-runs all 7 gems' existing dry-run and confirms the improvement.

**Tech Stack:** C# rule files only (`.module.cs`) — no Omen.Core/Omen.CLI source changes anticipated in this plan; everything needed (`ModuleRules`, `BinaryType`, `LinkType`-driven monolithic folding) already exists from Phase 1.

## Global Constraints

- Windows-only. `${pal_dir}`/`${common_dir}` resolve to real, hardcoded Windows paths (`Platform/Windows`, `Platform/Common`) — no PAL abstraction.
- Scope is exactly the 3 primary library targets (`AzCore`, `AzFramework`, `CryCommon`). No test targets, no `AzFramework.NativeUI`, no `CryCommonMocks`, no other framework library.
- Do not modify any existing `CMakeLists.txt`, `gem.json`, or already-committed `.gem.cs`/`.target.cs` file. These are pure additions.
- Every `.module.cs` file keeps the existing project's copyright header is NOT required here (NightFox repo convention, not Omen's) — match the header style already used in `Gems/Camera/Camera.gem.cs` (no header comment at all; just the `using` statements).
- Dependencies on modules/gems outside this plan's scope (`AZ::AzNetworking`, `3rdParty::zstd`, `3rdParty::lz4` on `AzFramework`) are declared anyway (matching CMake faithfully) even though they won't resolve — this is the same accepted gap every gem conversion so far already has for its own out-of-scope dependencies.

---

### Task 1: `AzCore.module.cs`

**Files:**
- Create: `F:\engine\Code\Framework\AzCore\AzCore.module.cs`

No automated test — this task's acceptance check is Task 4's dry-run re-verification, same pattern as the Camera pilot (Task 11 of `2026-08-04-nightfox-cmake-replacement.md`) and all 6 leaf-gem conversions.

**Interfaces:**
- Consumes: `ModuleRules` (`Omen.Core.Rules`), specifically the `protected ModuleRules(BuildContext context, string? explicitName)` constructor.
- Produces: a module named exactly `"AZ::AzCore"`.

**Source-of-truth CMake** (`F:\engine\Code\Framework\AzCore\CMakeLists.txt:64-78`):
```
ly_add_target(
    NAME AzCore ${PAL_TRAIT_MONOLITHIC_DRIVEN_LIBRARY_TYPE}
    NAMESPACE AZ
    FILES_CMAKE
        AzCore/azcore_files.cmake
        AzCore/std/azstd_files.cmake
        ${pal_dir}/platform_${PAL_PLATFORM_NAME_LOWERCASE}_files.cmake
    COMPILE_DEFINITIONS
        PRIVATE
            AZCORE_EXPORTS
    INCLUDE_DIRECTORIES
        PUBLIC
            .
            ${pal_dir}
            ${common_dir}
)
```
Confirmed during design: `azcore_files.cmake`'s and `azstd_files.cmake`'s `FILES` entries are both relative to `Code/Framework/AzCore/AzCore/` (the former lists e.g. `AzCoreModule.cpp`, `Asset/AssetCommon.cpp`; the latter's `std/` files live at `AzCore/AzCore/std/`) — a single recursive `SourceDirectory = "Code/Framework/AzCore/AzCore"` covers both file lists exactly, since Omen's source discovery is a recursive `*.cpp`/`*.c` walk. The `${pal_dir}` platform file list (`Platform/Windows/platform_windows_files.cmake`) lives in a **sibling** directory (`Code/Framework/AzCore/Platform/Windows`, not nested under `AzCore/AzCore/`) and is explicitly **not** picked up by this single `SourceDirectory` — out of scope per this plan's Global Constraints (no PAL abstraction); note it as a known limitation in the file's own comment rather than silently omitting it.

- [ ] **Step 1: Author the file**

Create `F:\engine\Code\Framework\AzCore\AzCore.module.cs`:

```csharp
using Omen.Core.Configuration;
using Omen.Core.Rules;

// Converts AzCore's primary library target (Code/Framework/AzCore/CMakeLists.txt) so the
// AZ::AzCore dependency name already used by every converted gem resolves to real include
// paths. Windows-only; platform-specific files under Platform/Windows are NOT included
// here (a single SourceDirectory can't span both AzCore/AzCore and the sibling Platform/
// tree) - known limitation, not an oversight, see the framework-library-conversion spec.
public class AzCoreModule : ModuleRules
{
    public AzCoreModule(BuildContext context) : base(context, explicitName: "AZ::AzCore")
    {
        SourceDirectory = "Code/Framework/AzCore/AzCore";
        BinaryType = TargetType.StaticLibrary;
        PrivateDefinitions.Add("AZCORE_EXPORTS");
        PublicIncludePaths.Add("../../../../Code/Framework/AzCore");
        PublicIncludePaths.Add("../../../../Code/Framework/AzCore/Platform/Windows");
        PublicIncludePaths.Add("../../../../Code/Framework/AzCore/Platform/Common");
    }
}
```

Note on the `PublicIncludePaths` entries: `ActionGraphBuilder.BuildIncludePaths` resolves a non-rooted `PublicIncludePaths` entry as `Path.Combine(baseDir, p)` where `baseDir = Path.Combine(_context.ProjectRoot, SourceDirectory)`. When this module is consumed by a gem (e.g. `Gems/GameState`, where `omen build` runs with `ProjectRoot = F:\engine\Gems\GameState`), `baseDir` for AzCore's own `SourceDirectory` becomes `F:\engine\Gems\GameState\Code\Framework\AzCore\AzCore` — nonsensical, since `SourceDirectory` is only meaningful relative to *this module's own* `ProjectRoot` (`F:\engine\Code\Framework\AzCore`), not the consuming gem's. Use `Path.GetFullPath` with an absolute base instead so the path is correct regardless of which gem's `ProjectRoot` is active:

Replace the three `PublicIncludePaths.Add(...)` lines above with:

```csharp
        var azCoreRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Code", "Framework", "AzCore"));
```

Actually — `AppContext.BaseDirectory` is Omen's own install location, not the engine's, and is wrong here too. The correct fix: `.gem.cs`/`.module.cs` files don't have a reliable "my own directory" handle at all today (confirmed during Task 11 of the prior plan — `LoadManifest` works around this the same way, by taking a path *relative to `Context.ProjectRoot`*, i.e. relative to whichever gem's directory `omen build` was invoked from). Since AzCore's own include paths must resolve correctly **regardless of which gem is building**, they must be **absolute, hardcoded to this engine checkout's real path** — same class of Windows-only, single-checkout assumption already accepted for `${pal_dir}` above. Use:

```csharp
using Omen.Core.Configuration;
using Omen.Core.Rules;

public class AzCoreModule : ModuleRules
{
    public AzCoreModule(BuildContext context) : base(context, explicitName: "AZ::AzCore")
    {
        const string root = @"F:\engine\Code\Framework\AzCore";
        SourceDirectory = root + @"\AzCore";
        BinaryType = TargetType.StaticLibrary;
        PrivateDefinitions.Add("AZCORE_EXPORTS");
        PublicIncludePaths.Add(root);
        PublicIncludePaths.Add(root + @"\Platform\Windows");
        PublicIncludePaths.Add(root + @"\Platform\Common");
    }
}
```

`SourceDirectory` as an absolute path also sidesteps the `ProjectRoot`-relative resolution problem for the module's own source discovery (`ActionGraphBuilder.BuildModuleActions` does `Path.Combine(_context.ProjectRoot, sourceDir)` — combining an already-absolute `sourceDir` with any base via `Path.Combine` just returns the absolute path unchanged, per `Path.Combine`'s documented behavior when the second argument is rooted).

- [ ] **Step 2: Commit**

```bash
git add Code/Framework/AzCore/AzCore.module.cs
git commit -m "feat: convert AzCore's primary library target to an Omen module"
```

---

### Task 2: `AzFramework.module.cs`

**Files:**
- Create: `F:\engine\Code\Framework\AzFramework\AzFramework.module.cs`

No automated test — same acceptance pattern as Task 1.

**Interfaces:**
- Consumes: `ModuleRules` (same as Task 1).
- Produces: a module named exactly `"AZ::AzFramework"`.

**Source-of-truth CMake** (`F:\engine\Code\Framework\AzFramework\CMakeLists.txt:18-42`):
```
ly_add_target(
    NAME AzFramework ${PAL_TRAIT_MONOLITHIC_DRIVEN_LIBRARY_TYPE}
    NAMESPACE AZ
    FILES_CMAKE
        AzFramework/azframework_files.cmake
        ${pal_dir}/platform_${PAL_PLATFORM_NAME_LOWERCASE}_files.cmake
    COMPILE_DEFINITIONS
        PRIVATE
            AZF_EXPORTS
    INCLUDE_DIRECTORIES
        PUBLIC
            .
            ${pal_dir}
        PRIVATE
            ${common_dir}
    BUILD_DEPENDENCIES
        PRIVATE
            AZ::AzCore
        PUBLIC
            AZ::AzNetworking
            3rdParty::zstd
            3rdParty::lz4
)
```
`azframework_files.cmake`'s `FILES` entries are relative to `Code/Framework/AzFramework/AzFramework/` (same nesting convention as AzCore). `AZ::AzNetworking`/`3rdParty::zstd`/`3rdParty::lz4` are out of scope (Global Constraints) — declared anyway per the established faithful-translation convention.

- [ ] **Step 1: Author the file**

Create `F:\engine\Code\Framework\AzFramework\AzFramework.module.cs`:

```csharp
using Omen.Core.Configuration;
using Omen.Core.Rules;

// Converts AzFramework's primary library target (Code/Framework/AzFramework/CMakeLists.txt).
// Windows-only; Platform/Windows-specific files are not included, same known limitation as
// AzCoreModule (see Code/Framework/AzCore/AzCore.module.cs).
public class AzFrameworkModule : ModuleRules
{
    public AzFrameworkModule(BuildContext context) : base(context, explicitName: "AZ::AzFramework")
    {
        const string root = @"F:\engine\Code\Framework\AzFramework";
        SourceDirectory = root + @"\AzFramework";
        BinaryType = TargetType.StaticLibrary;
        PrivateDefinitions.Add("AZF_EXPORTS");
        PublicIncludePaths.Add(root);
        PublicIncludePaths.Add(root + @"\Platform\Windows");
        PrivateIncludePaths.Add(root + @"\Platform\Common");

        PrivateDependencies.Add("AZ::AzCore");
        PublicDependencies.Add("AZ::AzNetworking");
    }
}
```

`3rdParty::zstd`/`3rdParty::lz4` are third-party libraries, not first-party modules — `ModuleRules` has no `BUILD_DEPENDENCIES`-equivalent for arbitrary CMake `3rdParty::` targets beyond the `ExternalDependencies`/GitHub-fetch mechanism, which is a different concern (fetching a library from source) from referencing an already-vendored one. Omit them rather than misrepresenting them as first-party module dependencies; this doesn't affect the acceptance test (Task 4 only checks AzFramework's own include paths appear on consuming gems, not that AzFramework's own third-party deps resolve).

- [ ] **Step 2: Commit**

```bash
git add Code/Framework/AzFramework/AzFramework.module.cs
git commit -m "feat: convert AzFramework's primary library target to an Omen module"
```

---

### Task 3: `CryCommon.module.cs`

**Files:**
- Create: `F:\engine\Code\Legacy\CryCommon\CryCommon.module.cs`

No automated test — same acceptance pattern as Tasks 1-2.

**Interfaces:**
- Consumes: `ModuleRules` (same as Task 1).
- Produces: a module named exactly `"Legacy::CryCommon"`.

**Source-of-truth CMake** (`F:\engine\Code\Legacy\CryCommon\CMakeLists.txt:1-16`, confirmed during design):
```
o3de_pal_dir(pal_dir ${CMAKE_CURRENT_LIST_DIR}/Platform/${PAL_PLATFORM_NAME} ...)

ly_add_target(
    NAME CryCommon STATIC
    NAMESPACE Legacy
    FILES_CMAKE
        crycommon_files.cmake
        ${pal_dir}/crycommon_${PAL_PLATFORM_NAME_LOWERCASE}_files.cmake
    INCLUDE_DIRECTORIES
        PUBLIC
            .
            ..
            ${pal_dir}
)
```
Unlike AzCore/AzFramework, CryCommon's own CMakeLists.txt comment confirms its public include root is itself directly (`.`) — "Lots of code without CryCommon/" — plus its parent directory (`..`, flagged in the CMake comment as "Dangerous... exports Legacy's path" but present regardless, so translated faithfully). Only 4 `.cpp` files total (`crycommon_files.cmake`, confirmed during design) — much smaller than AzCore/AzFramework.

- [ ] **Step 1: Author the file**

Create `F:\engine\Code\Legacy\CryCommon\CryCommon.module.cs`:

```csharp
using Omen.Core.Configuration;
using Omen.Core.Rules;

// Converts CryCommon's primary library target (Code/Legacy/CryCommon/CMakeLists.txt).
// Windows-only. CryCommon's own CMakeLists.txt puts its public include root at "." itself
// (not a nested CryCommon/ folder, unlike AzCore/AzFramework) plus its parent "..".
public class CryCommonModule : ModuleRules
{
    public CryCommonModule(BuildContext context) : base(context, explicitName: "Legacy::CryCommon")
    {
        const string root = @"F:\engine\Code\Legacy\CryCommon";
        SourceDirectory = root;
        BinaryType = TargetType.StaticLibrary;
        PublicIncludePaths.Add(root);
        PublicIncludePaths.Add(@"F:\engine\Code\Legacy");
        PublicIncludePaths.Add(root + @"\Platform\Windows");
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add Code/Legacy/CryCommon/CryCommon.module.cs
git commit -m "feat: convert CryCommon's primary library target to an Omen module"
```

---

### Task 4: Re-verify all 7 gems' dry-run output

**Files:** none (verification-only task; depends on Tasks 1-3).

**Interfaces:**
- Consumes: `AzCoreModule`, `AzFrameworkModule`, `CryCommonModule` (Tasks 1-3); the existing `omen build --dry-run` command and the 7 already-converted gems' `.gem.cs`/`.target.cs` files (unchanged by this plan).

**Known constraint going in:** `RuleCompiler.CompileRulesAsync(workingDir)` (`BuildCommand.cs:290`) only discovers `.module.cs`/`.gem.cs`/`.target.cs` files under `workingDir`, which `ExecuteDryRunAsync` sets to `Path.GetDirectoryName(targetFile)` (`BuildCommand.cs:273`) — the *target file's own directory* (e.g. `F:\engine\Gems\GameState`), regardless of which directory `omen` was invoked from (confirmed by reading `ResolveTargetFile`, `BuildCommand.cs:342-361`: it searches `Environment.CurrentDirectory` for the target file, but the discovered `workingDir` is still the found file's own folder). A gem's dry-run therefore cannot discover `Code\Framework\AzCore\AzCore.module.cs` — it lives outside the gem's own directory tree. This is a pre-existing Omen.Core scoping property (by design, per Phase 1's gem-scoped-directory pilot), not something Tasks 1-3 can work around from the NightFox side.

Given that, this task has two parts: prove Tasks 1-3's `.module.cs` files are individually well-formed, and report the cross-directory discovery gap as the real remaining blocker (out of this plan's scope to fix — it's an `Omen.Core`/`RuleCompiler` change).

- [ ] **Step 1: Verify each `.module.cs` file in isolation**

None of `AzCore`, `AzFramework`, `CryCommon` has a `.target.cs` today (they're libraries, not build targets), so `omen build` has nothing to resolve without one. Add a throwaway, **not committed** verification target next to each module to prove it resolves and produces real compile actions:

`F:\engine\Code\Framework\AzCore\_VerifyPilot.target.cs`:
```csharp
using Omen.Core.Configuration;
using Omen.Core.Rules;

public class VerifyPilotTarget : TargetRules
{
    public VerifyPilotTarget(BuildContext context) : base(context)
    {
        Type = TargetType.StaticLibrary;
        LaunchModuleName = "AZ::AzCore";
        LinkType = LinkType.Modular;
    }
}
```

Run:
```bash
cd F:\engine\Code\Framework\AzCore && dotnet "F:\Omen-githubpublic\src\Omen.CLI\bin\Debug\net8.0\omen.dll" build VerifyPilot --dry-run --configuration Development
```
Expected: one `[Compile]` line per `.cpp` under `AzCore/AzCore`, each with `/I"F:\engine\Code\Framework\AzCore"` present. Repeat with the same pattern (`LaunchModuleName = "AZ::AzFramework"` from `Code\Framework\AzFramework`, `LaunchModuleName = "Legacy::CryCommon"` from `Code\Legacy\CryCommon`) for Tasks 2 and 3's modules.

Delete each `_VerifyPilot.target.cs` after confirming (or simply never `git add` it — `git status` showing no untracked `_VerifyPilot.target.cs` files confirms cleanup).

- [ ] **Step 2: Confirm the gem-side gap is exactly the discovery scoping issue, not an authoring mistake**

```bash
cd F:\engine\Gems\GameState && dotnet "F:\Omen-githubpublic\src\Omen.CLI\bin\Debug\net8.0\omen.dll" build GameStatePilot --dry-run --configuration Development
```
Expected: still no `AZ::AzCore` include path in the output (Step 1 already proved the module itself is correct, so this confirms the gap is discovery scope, not a mistake in Tasks 1-3).

- [ ] **Step 3: Record the outcome**

Write the finding (cross-directory discovery gap, `AzCoreModule`/`AzFrameworkModule`/`CryCommonModule` individually verified correct via the throwaway pilot in Step 1) into
`F:\Omen-githubpublic\docs\superpowers\specs\2026-08-19-framework-library-conversion-design.md`'s "Risks / open questions" section — that's where a future reader will look for it, not a throwaway commit message.

- [ ] **Step 4: Commit the spec update**

```bash
git add docs/superpowers/specs/2026-08-19-framework-library-conversion-design.md
git commit -m "docs: record cross-directory RuleCompiler discovery as the real blocker for gem-to-framework dependency resolution"
```

---

## Self-review notes

- **Spec coverage:** the spec's "In scope" bullets (3 `.module.cs` files, Windows-only paths, `BinaryType`, re-verify dry-run) are covered by Tasks 1-4. The spec's flagged risk (`SourceDirectory` needing confirmation against `azcore_files.cmake`'s real structure) was resolved during this plan's authoring (Task 1's note) rather than left open.
- **Placeholder scan:** Task 4 initially drafted as a simple "re-run dry-run, confirm paths appear" step, which on working through the actual invocation mechanics turned out to be **wrong** — the gem-scoped `RuleCompiler` discovery Camera's own pilot deliberately relies on doesn't span `Code/Framework`. Rather than paper over this with a vague "verify it works" step, Task 4 documents the real mechanism, provides a targeted single-module verification that *does* prove Tasks 1-3's authoring is correct, and reports the actual remaining blocker honestly instead of asserting false success.
- **Type consistency:** `ModuleRules(context, explicitName)`, `BinaryType`, `PublicIncludePaths`/`PrivateIncludePaths`/`PrivateDefinitions`/`PublicDependencies`/`PrivateDependencies` are used identically across Tasks 1-3, matching their real signatures in `src/Omen.Core/Rules/ModuleRules.cs`.

