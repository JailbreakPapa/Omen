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
  **106 of its 122 non-generated source documents match their original SHA-256 checksums
  byte-for-byte**, including Luna's separate `UnityC` object naming fix. Fifteen unavailable
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
- Core: 385 tests pass. Executors/build server: 127 tests pass. GUI models/services: 21 tests
  pass, including a new nested-target/project-root regression. Total: **533 tests pass**.
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
recovery because a separate LunaTests build was active; compiler/linker validation used an
isolated project. No cause of the original deletion has been established.

## Build and retain

From this directory:

```powershell
dotnet build Omen.sln -c Release
dotnet test Omen.sln -c Release --no-build
dotnet pack src/Omen.CLI -c Release -o artifacts
```

Run the restored executable directly with
`dotnet F:\Omen-githubpublic\src\Omen.CLI\bin\Release\net8.0\omen.dll <arguments>`.
The desktop executable is `src\Omen.GUI\bin\Release\net8.0-windows\Omen.GUI.exe`.

The recovered tree is committed locally and independently backed up as a Git bundle and
source archive under `E:\Backups\Omen\2026-10-21`. It has not been pushed to GitHub.
Portable symbols now embed all C# source files so future binaries can retain their own source.
