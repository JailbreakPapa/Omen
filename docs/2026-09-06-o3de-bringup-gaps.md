# Omen — gaps found bringing up an O3DE tree (2026-09-05/06)

Context: the first two real targets built end to end under Omen on the NightFox tree — the
Atom sample viewer launcher (62–70 modules, ten builds to a clean link) and AssetProcessorBatch
(74 modules). Everything below was found by those builds, not by review. Companion to
[2026-09-01-o3de-readiness-audit.md](2026-09-01-o3de-readiness-audit.md).

## Fixed in Omen during the bring-up

| Commit | Defect |
|---|---|
| 43d5831 | `--dry-run` previewed the first target reflection listed, whatever `-t` named. |
| 22aa865 | GUI: `FindTargetFile` took the first `*.target.cs` under the root (a plugin's pilot), so the options panel threw and the matrix was empty; VS generator wrote `<TargetName>` raw (`AZ::X.lib` → MSBuild `%(FullPath)` errors on every project). |
| 5f9b8e9, 6614ef0 | `/D` values lost their quotes (`LY_CMAKE_TARGET="X"`); the fix then double-escaped values the converter had pre-escaped. Negative-lookbehind escape. |
| 24540cd | `ResolveSourceFiles` globbed the SourceDirectory for any extension the explicit list lacked (a two-file exe module moc'd every unit test under it); rcc `-name` per file. |
| 4ebd925 | rcc `-name` must be the .qrc basename — `Q_INIT_RESOURCE(Icons)` is `qInitResources_Icons`; qualify only a contested basename. |
| 9330722 | A module's link was ordered after its *direct* dependencies while its libraries came from the transitive walk: a static lib reached through an object library had no edge and was opened mid-write (LNK1104). |

## Still missing vs CMake-for-O3DE (worked around by hand in the tree)

1. **Module registry generation.** CMake's `ly_set_gem_variant_to_load` + delayed generation
   wrote `<bin>/Registry/cmake_dependencies.<target>.setreg` listing each enabled gem's
   module DLLs per variant (Clients/Builders/Tools). The engine loads gems from it; without it
   a launcher starts with no gems and the Asset Processor with no builders. Hand-written by
   `Projects/NightFoxSampleViewer/scripts/deploy_omen_runtime.py` from the target's
   `ExtraModules`. Omen has all the inputs (target closure, module BinaryType, plugin
   membership); the file format is trivial. Note the file's object keys ARE the active plugin
   set (`GetPluginsInfo` visits `/O3DE/Plugins`): every enabled plugin must appear, asset-only
   ones with an empty target set, or the Asset Processor never scans their Assets folder.
2. **Runtime file deployment.** CMake's `ly_add_target_files` / package `RUNTIME_DEPENDENCIES`
   copied Qt DLLs + plugins, `Builders/AZSLc/azslc.exe`, `Builders/DirectXShaderCompiler/*`,
   and package DLLs (assimp) beside the executables. Nothing in Omen copies a file that is not
   a build output. Same script covers it.
3. **INTERFACE (consumer-only) definitions.** `PublicDefinitions` applies to the module itself
   too; O3DE's `INTERFACE` compile definitions (ImGui's `IMGUI_API_IMPORT` for consumers vs
   export for the library) had to be split into public/private with a header-side fallback.
4. **Per-file compile flags** (`ly_add_source_properties … COMPILE_OPTIONS`). Only a whole
   module has `AdditionalCompilerFlags`, so MaskedOcclusionCulling's AVX2 file became its own
   module. Acceptable, but every such file costs a rule.
5. **`LY_CMAKE_TARGET`.** CMake defined the target name for every target; O3DE's
   `*BuildTarget.cpp` files `#error` without it. Each such module carries it by hand
   (AssetBuilder, AssetProcessorBatch, AssetProcessorBatch.Static). A module-name define
   (any spelling) from the graph would retire all three.
6. **Platform include files.** `Platform/<P>/platform_<p>.cmake` carried per-platform
   `BUILD_DEPENDENCIES`, `COMPILE_DEFINITIONS` and `LY_BUILD_DEPENDENCIES` (Vulkan's
   `VK_USE_PLATFORM_WIN32_KHR`, QtComponents' `Magnification.lib`); the converter never read
   them.
7. **System libraries.** `PublicLibraries` is vendored paths relative to the module;
   `PublicSystemLibraries` is the bare-name list. Nothing warns when a bare name lands in the
   former (it becomes a relative path that does not exist).

## Converter defects (fixed tree-wide in the engine, the converter itself untouched)

- Same-plugin flavors referenced by KIND (`$"{Name}.PrivateObject"`), cross-plugin ones as
  `"Plugin::X"` / `"X.Runtime"`; `PluginFlavorNaming.ModuleName` spells them
  `X.Private.Object`, bare `X`. 116 + 93 + 17 sites.
- Every `*_shared_files.cmake` list dropped — the `Module.cpp` of each GEM_MODULE, so DLLs
  linked that could never load.
- Stub targets (the unsupported-platform `else` branch, `*_stub_files.cmake`) merged into the
  real target's list — DX12's Private and Builders both.
- Trait-gated lists (`if(PAL_TRAIT_…)`) and `PLATFORM_INCLUDE_FILES` ignored.
- 3rdParty modules for config packages (OpenEXR, OIIO, assimp, mikkelsen) emitted as empty
  shells or not at all; OpenEXR's targets list three include roots.
- Appended lines (the converter's own late additions and the omissions fixer alike) landed inside
  the trailing `if (Mac)` block — a rule
  file's last block is not the constructor body.
- Lists taken from an older tree (Profiler: a file that no longer exists, one that was
  added since).

