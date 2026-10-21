# Follow-up feature audit - October 21, 2026

The baseline recovery commit is `dad1e23f28e700f7ff8c9ce53d9467d4a911bb06`. This follow-up
addresses functionality the user identified as missing from that restored GUI.

| Feature | Result |
| --- | --- |
| VS2026 project generation | CLI and visible GUI Project menu support it; emits `.slnx` and compatibility `.sln`, with v145 C++ projects. |
| SLNX configuration mappings | Preserves C++/C# mappings, gated target configurations, build inclusion, dependencies, source folders and build-rule files. Read-only output can be regenerated. |
| VS2019 / VS2022 compatibility | Continue generating traditional `.sln`; regression tests cover both. |
| Rider generation | Existing generator restored to the visible Project menu, alongside VS Code and CMake. |
| VS2026 SDK discovery | Queries `vswhere` for the newest C++ installation before fallback paths; recognizes the `18` and `2026` directories and reports VS version 18.0. |
| Large-project GUI startup | Replaced synchronous whole-tree target enumeration with core asynchronous project discovery. |
| GUI responsiveness during generation | Generates IDE files on the thread pool and marshals progress back to the dispatcher. |
| Existing newer build features | Build matrices, rule editor diagnostics, namespaced module/project naming, plugin rules, Qt tooling, distributed/server source, and separate C/C++ unity objects remain in the restored source. The test suites cover their recovered behavior. |

## Verification

- Full solution Release build succeeds using isolated outputs under
  `F:\Omen-recovery-20261021\updated-artifacts` because the user has the baseline GUI open.
- 537 tests pass: 389 Core, 127 Executors, 21 GUI.
- CLI generation on `cpp-smoke` and `slnx-smoke` writes SLNX successfully. The latter has
  five mixed C++/C# projects, two targets and six solution configurations, including a tool
  that supports only Debug/x64.
- Microsoft's `Microsoft.VisualStudio.SolutionPersistence` parser from .NET SDK 10.0.401
  opens the SLNX and compares every project's configuration mapping and build inclusion
  against the generated traditional solution. All match.
- VS2026 MSBuild accepts `Smoke.slnx /t:ValidateSolutionConfiguration` for Development/x64.
- The isolated MSVC C/C++ fixture builds and its executable exits 0 after the SDK discovery fix.
- An offscreen WPF smoke test creates the actual main window, finds the visible VS2026/Rider
  Project menu items, invokes the VS2026 item, and confirms that `Smoke.slnx` is regenerated.
  It also checks menu header width and saves `omen-updated-gui.png` and
  `omen-updated-gui.menu.png`. The window initializes without the previous whole-tree scan.

Detailed logs, fixtures and validation utilities are retained in `F:\Omen-recovery-20261021`.

## Provenance and limits

The archive audit found the VS2026 enum, generator/toolset handling, and newer menu commands,
but did not find a recoverable original Omen SLNX emitter. SLNX output is a new implementation
using the recovered solution's folder, dependency and configuration model, with BCL XML APIs
and no new runtime dependency. It follows Microsoft's SLNX schema and is checked with its
solution parser. This is functional reconstruction, not a claim of historical byte identity.

The archived GUI's OmenNet Coordinator/Agent launch commands and Clear Rule Cache command still
report that they are not implemented. The recovered QtUI tree remains a separate unfinished
prototype. Original custom font payloads and the deleted unpublished Git history remain
unavailable. Full manual coverage of every desktop workflow and distributed deployment has
not been established; successful restoration tests do not prove complete historical parity.

The installed global tool and the running baseline GUI have not been replaced. Updated binaries
are staged in `F:\Omen-githubpublic\artifacts\restored` for explicit launch. Both baseline and
updated source backups are retained under `E:\Backups\Omen\2026-10-21`.
