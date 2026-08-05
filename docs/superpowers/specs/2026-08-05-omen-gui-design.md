# Omen GUI (Avalonia)

## Context

Omen's README has advertised "a GUI for configuring & generating projects" since before this spec, and `src/Omen.QtUI` looked like an empty reserved directory until inspected. It isn't: it holds a real, fairly complete Qt6/C++ prototype (`OmenUI`) — a main window with menu/toolbar, a project file tree, a colored build-output pane, a dark theme, and platform/configuration selectors — that drives Omen entirely by shelling out to the CLI (`dotnet Omen.CLI.dll build/rebuild/clean/generate`) via `QProcess` and scraping its stdout for the words "error"/"warning"/"success" to decide line color. It's a separate C++/CMake project, not part of `Omen.sln`, and has a checked-in `build/` directory with a working `Release/OmenUI.exe` and its Qt6 DLLs from a prior build.

Two things make this the wrong foundation to build on:

- It depends on CMake to build the GUI for a tool whose other stated goal (this repo's broader arc) is replacing CMake-driven builds elsewhere. Building Omen's own GUI still needs a second toolchain.
- It only ever sees what the CLI prints to stdout. It can't show a target/module/gem dependency graph, a layering violation, or a rule-compilation error as anything other than colored text, because nothing about the subprocess boundary carries structure.

This spec replaces it with a new Avalonia (cross-platform, C#) app in the existing solution, calling into `Omen.Core` directly.

## Non-goals for this pass

- Graph/dependency visualization (target/module/gem tree beyond the plain file-system project tree). The existing Qt prototype didn't have this either; it's real, valuable, and explicitly deferred to a follow-up rather than folded in here.
- Linux/Mac verification. The app is written to not hard-code Windows assumptions (platform list comes from `PlatformFactory`, not a hard-coded string list), but only Windows is built and run as part of this work.
- A CLI-path settings dialog. It doesn't exist because it isn't needed — the GUI is in-process with `Omen.Core`, not a subprocess wrapper.

## Architecture

A new `src/Omen.GUI/Omen.GUI.csproj` (Avalonia desktop app, net8.0), added to `Omen.sln`, referencing `Omen.Core`, `Omen.Platforms`, and `Omen.Executors` directly. Standard MVVM via `CommunityToolkit.Mvvm` (source-generator based `[ObservableProperty]`/`[RelayCommand]` — no ReactiveUI; this app's flows are plain request → progress → result, nothing reactive-stream-shaped enough to justify it).

The CLI's build orchestration is not duplicated into the GUI. `BuildCommand.ExecuteBuildAsync` today inlines: resolve the target file, build a `BuildContext`, compile rules via `RuleCompiler`, resolve the toolchain via `PlatformFactory`, validate layering via `LayeringValidator`, build the graph via `ActionGraphBuilder`, apply the digest-based skip pass, execute via `ParallelExecutor`, then persist digests. That sequence moves into a new `Omen.Executors/Orchestration/` folder as three classes — `BuildOrchestrator` (build and rebuild — rebuild is a clean pass followed by build), `CleanOrchestrator`, `ProjectGenerationOrchestrator` — each shaped as **typed request in, `IProgress<OrchestratorEvent>` events out, typed result out**.

**Correction from design review:** the orchestrators can't live in `Omen.Core` as originally scoped here — `BuildOrchestrator` needs `ParallelExecutor` (in `Omen.Executors`) and `LocalActionCache` (in `Omen.Distributed`), and `Omen.Executors` already references `Omen.Core`, so putting them in `Omen.Core` would create a circular project reference (`Core` → `Executors` → `Core`). `Omen.Executors` already references both `Omen.Core` and `Omen.Distributed` — it only needs one more reference added, to `Omen.Platforms` (for `PlatformFactory`) — making it the correct home. Both `Omen.CLI` (already) and `Omen.GUI` (new) reference `Omen.Executors`, so both reach the orchestrators the same way.

`BuildCommand.cs`/`CleanCommand.cs`/`GenerateCommand.cs` become thin wrappers: parse CLI args into the request type, call the orchestrator, render `OrchestratorEvent`s through `AnsiConsole`. The GUI's ViewModels call the identical orchestrators and render the identical events into the output pane. There is exactly one implementation of "how a build/clean/generate actually runs" — the CLI and GUI cannot drift apart on it, because they're calling the same code.

`OrchestratorEvent` is a small `{ string Message, OrchestratorEventLevel Level }` type (`Info`/`Warning`/`Error`/`Success`), replacing the Qt prototype's approach of pattern-matching raw output text for the word "error" — the level is decided once, at the source, by code that already knows whether something failed.

## Components

- **`MainWindowViewModel`** — selected project path, selected `TargetPlatform`/`BuildConfiguration` (bound directly to enum-backed combo boxes — no string parsing, unlike the CLI's `ParsePlatform`/`ParseConfiguration` which exist only because CLI args arrive as strings), an `ObservableCollection<OutputLine>` for the log pane, `IsBuilding`, progress value, status text, and commands: `OpenProjectCommand`, `BuildCommand`, `RebuildCommand`, `CleanCommand`, `CancelCommand`, `GenerateProjectCommand(ideKind)`.
- **`ProjectTreeViewModel` / `ProjectTreeNode`** — a recursive file-tree view model. Same skip-list as the Qt prototype's `ProjectTree::populateTree` (`bin`, `obj`, `.git`, `.vs`, `node_modules`, `Intermediate`, `Binaries`, and anything starting with `.`) and the same relevant-file filter (`*.target.cs`, `*.module.cs`, `*.gem.cs`, `*.cs`, `*.cpp`, `*.c`, `*.h`, `*.hpp`, `*.json`, `*.xml`) — `*.gem.cs` is new, added here since the Qt prototype predates the Gem model. Double-clicking a file raises an event the shell can use later (e.g. to open an editor); v1 doesn't need to act on it beyond that.
- **Output pane** — an `ItemsControl` bound to `ObservableCollection<OutputLine>` (`{ string Text, OrchestratorEventLevel Level }`), colored per level via a converter. Auto-scrolls to the newest line.
- **Settings** — one value: the last-opened project path, persisted to `%APPDATA%/Omen/gui-settings.json` (`Environment.SpecialFolder.ApplicationData`), read on startup and written on successful project open. Nothing else needs persisting for v1.
- **Dark theme** — Avalonia's Fluent dark theme as the base, not a hand-rolled stylesheet like the Qt version's `applyDarkTheme()`; Fluent dark is close enough to the GitHub-dark palette the Qt prototype used by hand that re-deriving colors isn't worth it.

## Data flow

1. User clicks **Open Project** → folder picker → `ProjectTreeViewModel` populates from the chosen path → path saved to settings.
2. User selects platform/configuration from combo boxes bound to `TargetPlatform`/`BuildConfiguration` enum values. The platform combo's items come from `PlatformFactory.GetAvailablePlatforms()` (SDKs actually detected on this machine), not the full `TargetPlatform` enum — so the console/Prospero/Xbox stub platforms from earlier in this session's work never appear as selectable options here (their `IsAvailable` is `false`), and nothing in the GUI needs updating if a real console toolchain is added later.
3. User clicks **Build** → `MainWindowViewModel` builds a `BuildOrchestratorRequest { TargetFile, Platform, Architecture, Configuration }` → `IsBuilding = true`, output pane cleared → `BuildOrchestrator.BuildAsync(request, progress, cancellationToken)` runs on a background task → each `OrchestratorEvent` appends a colored `OutputLine`; each `BuildProgress` update (already emitted by `ParallelExecutor` today) updates the progress bar → on completion, `IsBuilding = false`, status text and a final Success/Error line reflect the `BuildResult`.
4. **Rebuild** is the same flow with `CleanOrchestrator.CleanAsync` run first. **Clean** and **Generate Project Files** (VS2022/VS2019/VSCode/CMake, matching the Qt prototype's menu) follow the identical request→events→result shape through their own orchestrators.
5. **Cancel** signals the `CancellationTokenSource` driving the current operation; `ParallelExecutor` already observes its `CancellationToken` (existing behavior, not new), so no new cancellation plumbing is needed below the orchestrator boundary.

## Error handling

Every orchestrator catches what the CLI path today catches inline (`RuleCompilationException` from `RuleCompiler`, `LayeringViolationException` from `LayeringValidator`, a missing/unavailable toolchain from `PlatformFactory`) and turns it into an `Error`-level `OrchestratorEvent` plus a failed result — never an exception reaching the UI thread. Error events show the exception's message only, matching the CLI's existing behavior (not a full stack trace — that's noise for this audience, and the CLI never showed one either). A genuinely unexpected exception from within an orchestrator is still caught at the ViewModel boundary as a last-resort guard and surfaced the same way, so a bug in new orchestrator code degrades to an error line instead of crashing the app.

## Testing

`BuildOrchestrator`/`CleanOrchestrator`/`ProjectGenerationOrchestrator` get real unit test coverage in a new `Omen.Executors.Tests` project (mirroring `Omen.Core.Tests`' conventions — no such test project exists for `Omen.Executors` yet), following the `FakeToolchain`/temp-directory fixture pattern already established this session (`ActionGraphBuilderTests.cs` et al.) — this is where the actual logic lives, and it's the same kind of graph-construction/toolchain-resolution logic already under test. Specific cases: a request for a target file that doesn't exist produces a failed result with a clear message rather than throwing; a rule-compilation failure and a layering violation each produce the correct event level and failed result; a successful build's events arrive in the expected order (rules compiled → graph built → executing → result).

The GUI shell itself (Avalonia views, bindings, the tree/output panes) is verified by actually running it against a real project — `GemSample` or `ExampleGame`, both already proven to build and run end-to-end earlier this session — and confirming Open/Build/Rebuild/Clean/Generate each genuinely work, not just that the app launches. This matches the standing bar this session has held throughout ("verified by running it," not "compiles").

## Cleanup

`src/Omen.QtUI/` — including its checked-in CMake `build/` output (a full Qt6 DLL set and a built `.exe`) — is deleted as part of this work. It's being replaced, not extended; keeping a second, unmaintained GUI around (with its own build toolchain) is repo bloat and a maintenance trap, not a useful reference. Its UX ideas (project tree + output pane split, dark theme, colored log levels) carry forward in spirit above, not as code to port.
