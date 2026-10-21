# Omen source recovery - October 21, 2026

The missing `F:\Omen-githubpublic` source tree has been restored on the local branch
`codex/restore-omen-source`. The command-line build system, core rules and action graph,
Windows/Linux/Apple/Android toolchains, distributed coordinator and agent, dashboard,
WPF desktop GUI, build-server tray, tests, examples, and recovered documentation are present.

## Provenance

- The surviving, untouched `F:\Omen` repository provided commit
  `81a72c3195343bc32a058746e1ce1dbb5f6c5408` and its public history.
- The deleted checkout's recorded head was `460c54655e50236878e530ece5df77329b2b01f4`.
  It was 248 commits ahead of the public branch in the archived records. Neither the
  surviving Git object store nor the GitHub repository supplied that commit during recovery.
  Its original commit history and original worktree registrations have **not** been recovered.
- Original source text, edits, and file-history backups were recovered from local archived
  Claude and Codex records. Original comments were retained where available.
- The installed October 7 Omen package and portable symbols supplied an independent source
  inventory, checksums, public API, implementation fallback, and documentation comments.
  At the initial restoration, **106 of its 122 non-generated source documents matched their original
  SHA-256 checksums byte-for-byte**, including Luna's separate `UnityC` object naming fix.
  Subsequent VS2026 and native workflow additions intentionally change five of those documents;
  101 remain byte-identical in the current tree. Fifteen unavailable
  source documents were reconstructed from the installed assemblies, sometimes split into
  separate files per type. `Home.razor` uses recovered source whose checksum differs from
  the installed package. This is a tested restoration, not a claim of a byte-identical
  historical checkout.
- Missing desktop integration was reconstructed around the recovered WPF views and models.
  Original branding and licensed SVG icons came from `E:\ProductICONS`. The ICO was regenerated
  from the original Omen logo. The original custom font payloads were not recovered; the
  existing font fallback lists remain in use.

The file inventory and checksums are in [recovery/source-manifest.json](recovery/source-manifest.json).
Detailed forensic evidence, all extracted source variants, the untouched installed package,
test logs, package output, and recovery scripts are in `F:\Omen-recovery-20261021`.

## Validation

- `dotnet build Omen.sln -c Release`: succeeds, including CLI, dashboard, GUI, and tray.
- Core: 403 tests pass. Executors/build server/workflows: 149 tests pass. GUI models/services:
  23 tests pass. Total: **575 tests pass**.
- A real isolated Windows/MSVC project compiles two C and two C++ sources into separate
  `SmokeModule.UnityC0.obj` and `SmokeModule.Unity0.obj`, links, and exits successfully.
  A second build skips all three actions without compiling or linking again.
- WPF startup, resource loading, project/options/output docking panes, and offscreen rendering
  were exercised. The screenshot is `F:\Omen-recovery-20261021\omen-gui.png`. This is not a
  complete manual test of every desktop interaction or every distributed deployment mode.
- `dotnet pack src/Omen.CLI -c Release --no-build`: produces an installable tool package.

Some recovered tests predated later plugin naming and project-name behavior. Their expectations
were reconciled with the installed implementation. Asynchronous test progress collectors now
use the recovered `SynchronousProgress` implementation. Desktop repairs include the diagnostic
validation race, root-aware matrix discovery, search/document wiring, and missing theme resources.
AvalonDock is pinned to 4.72.1, whose .NET 5 Windows assembly supports this .NET 8 GUI; version
5.0.0 in the archived project only offered .NET 9/10 and .NET Framework assemblies.

The installed global `omen` tool was left intact. LunaEngine source and its ongoing build were
not reset, cleaned, or replaced. The actual LunaEngine build was not rerun as part of this
recovery because a separate Luna build was active; compiler/linker validation used an
isolated project. No cause of the original deletion has been established.

## Later functionality repaired

The first restored GUI omitted VS2026 and Rider from its visible Project menu. The installed
package's VS2026 generator also emitted only a traditional `.sln`. A follow-up implements
VS2026 `.slnx` generation alongside the compatibility `.sln`, restores both visible menu
commands, fixes menu-header spacing, and detects VS2026 before older side-by-side installations.
Target discovery now runs asynchronously through the core project discovery API, respecting
source roots and exclusions, instead of recursively scanning the entire engine tree on the UI
thread. IDE generation also runs off the UI thread.

`generate project --ide vs2026` was exercised on a real C/C++ fixture and a five-project mixed
C++/C# fixture with two targets and six solution configurations. Microsoft's installed solution
parser verified that SLNX configuration mappings, build inclusion and folder placement match
the compatibility solution. VS2026 MSBuild accepted `ValidateSolutionConfiguration`. The native
fixture compiled, linked, and exited 0 with the updated SDK discovery.

A real LunaEngine generation exposed a cross-platform evaluation bug: the Windows Visual Studio
exporter instantiated Linux matrix rows, triggering Linux Qt symlink and vcpkg filesystem probes
on Windows. Visual Studio generation now selects Windows rows before evaluating rules, matching
its Windows SDK/debugger/output model. LunaEngine's source rules were preserved. The real CLI
successfully generated 146 projects across 75 combinations, and Microsoft's solution parser
validated all project mappings against the compatibility solution. The actual WPF VS2026 menu
also completed generation successfully (`omen-luna-generation.png` and `wpf-luna-generation.log`
in the evidence directory). Regression tests cover
VS2019, VS2022 and VS2026.

See [recovery/FEATURE_AUDIT.md](recovery/FEATURE_AUDIT.md) for evidence and remaining limits.

The user's subsequent CMake comparison request adds native presets/workflows, process-test execution
and JUnit reports, component installation, reproducible ZIP packages, configured-file generation,
compiler/link/run probes, target discovery and GUI workflow entry points. The implementation and
remaining CMake gaps are documented in [docs/cmake-feature-comparison.md](docs/cmake-feature-comparison.md).
The same C++ sample was built/tested/installed/packaged with Omen and a separate CMake/CTest/CPack
reference. Package hashes, relocation, configuration files, CLI error paths and actual WPF menu
execution were checked. Detailed logs are in `F:\Omen-recovery-20261021`.

## Build and retain

From this directory:

```powershell
dotnet build Omen.sln -c Release
dotnet test Omen.sln -c Release --no-build
dotnet pack src/Omen.CLI -c Release -o artifacts
```

Run the restored executable directly with
`dotnet F:\Omen-githubpublic\src\Omen.CLI\bin\Release\net8.0\omen.dll <arguments>`.
Current verified binaries are staged in `artifacts\restored\CLI` and `artifacts\restored\GUI`.
Launch `artifacts\restored\GUI\Omen.GUI.exe` for the updated desktop; an already-running older
GUI keeps its loaded assemblies until closed. The installed global tool remains unchanged.

The recovered tree is committed locally and independently backed up as a Git bundle and
source archive under `E:\Backups\Omen\2026-10-21`. It has not been pushed to GitHub.
Portable symbols now embed all C# source files so future binaries can retain their own source.
