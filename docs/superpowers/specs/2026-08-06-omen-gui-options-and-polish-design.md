# Omen GUI: Build Options Panel + UI Polish

## Context

The Omen GUI (Avalonia, added in a prior plan) has a working Tree | Output layout driving `BuildOrchestrator`/`CleanOrchestrator`/`ProjectGenerationOrchestrator`. Two gaps remain:

1. Omen has no equivalent of CMake's cache variables — `option()`/`set(... CACHE)` values that a `CMakeLists.txt` author declares, that `cmake-gui` shows in an editable table, and that persist across configures. `TargetRules`/`ModuleRules`/`GemRules` properties are all set in code today; there's no way for a rules-file author to expose a user-configurable toggle or value, and no way for the GUI to discover what's configurable for a given project.
2. The GUI's visual design is a functional-but-plain default Fluent Dark shell: text-only toolbar buttons, fixed-width panes, no visual hierarchy beyond what Avalonia gives for free.

This spec covers both, decided together since the options panel's placement is itself a layout/polish decision.

## Non-goals for this pass

- Per-platform/per-configuration option values. The cache is one file per project, shared across every `TargetPlatform`/`BuildConfiguration` combination — mirroring one `CMakeCache.txt` per build tree, not per-config caches.
- A generic plugin/extension system for custom option widgets. Four types (Bool, String, Int, Path) cover the real cases; anything more exotic is a `String` with documentation.
- Light theme support. "Signal" is a dark theme only for this pass — Avalonia's theme-variant system isn't precluded from adding one later, but nothing here builds it.
- Editing the option cache file by hand is not a supported workflow to design around (it's plain JSON and nothing stops a user from doing it, but the GUI doesn't need to gracefully merge concurrent hand-edits).

## Architecture — Build Options

### Declaring an option

`ModuleRules`, `TargetRules`, and `GemRules` are three separate, unrelated base-class hierarchies (the last added by an earlier plan), so a shared static entry point avoids touching all three inheritance chains:

```csharp
namespace Omen.Core.Options;

public static class BuildOptions
{
    public static bool Declare(BuildContext context, string name, string description, bool defaultValue);
    public static string Declare(BuildContext context, string name, string description, string defaultValue);
    public static int Declare(BuildContext context, string name, string description, int defaultValue);
    public static string DeclarePath(BuildContext context, string name, string description, string defaultValue);
}
```

Each overload does two things: records a `BuildOptionDeclaration` (name, description, type, default, and the *effective* current value) onto `context.DeclaredOptions` — a new mutable list on `BuildContext`, populated as rules run, exactly mirroring how CMake's `option()` calls register into the cache during Configure — and returns the effective value: the cache's override if `context.CachedOptionValues` has one for that name (parsed to the declared type), else the default.

A rules file author uses it exactly like CMake's `option()`:

```csharp
public class MyTarget : TargetRules
{
    public MyTarget(BuildContext context) : base(context)
    {
        if (BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable experimental feature X", false))
        {
            GlobalDefinitions.Add("FEATURE_X_ENABLED=1");
        }
    }
}
```

Declaring the same name twice in one discovery pass (e.g. from both a module and the target that depends on it) is not an error — the second `Declare` call is a no-op against the registry (first declaration wins, matching CMake's `option()` semantics) but still returns the correct effective value.

### Persistence

`Intermediate/omen-cache.json`, project-root-relative, one per project: `{ "ENABLE_FEATURE_X": "true", "INSTALL_PREFIX": "/usr/local" }` — flat, string-valued, matching CMakeCache.txt's own all-string storage (`ENABLE_FEATURE_X:BOOL=ON`). Typed parsing happens at the `Declare` call site based on which overload is called, not stored in the cache file itself.

### Discovery ("Configure")

A new `OptionsOrchestrator` in `Omen.Executors/Orchestration/` (alongside the existing three), shaped like its siblings:

```csharp
public sealed class OptionsOrchestratorRequest
{
    public required string TargetFile { get; init; }
}

public sealed class OptionsOrchestrator
{
    public async Task<IReadOnlyList<BuildOptionDeclaration>?> DiscoverAsync(
        OptionsOrchestratorRequest request,
        IProgress<OrchestratorEvent>? events,
        CancellationToken ct = default);
}
```

It runs the same resolve-target → `RuleCompiler.CompileRulesAsync` → `CreateTargetRules`/`CreateModuleRules` sequence `BuildOrchestrator` runs (extracted the same way — read from `BuildOrchestrator.BuildAsync` rather than re-derived), but stops after instantiation: the return value is `context.DeclaredOptions`, merged against the on-disk cache (loaded first, into `BuildContext.CachedOptionValues`, so `Declare` calls see current overrides while running). No graph is built, nothing compiles.

`BuildOrchestrator` and `ProjectGenerationOrchestrator` each gain one addition: load `omen-cache.json` (if it exists) into the `BuildContext` they construct, before calling `CreateTargetRules`/`CreateModuleRules`. This is the only change needed to make edited option values actually affect a real build — `Declare` calls made during a real Build already read from the same `CachedOptionValues` a Configure pass populates from disk.

## Architecture — UI Polish

### Layout

The main window's content area becomes three `GridSplitter`-separated panes: Project Tree | Build Output | Options. The Options pane can collapse to a narrow vertical toggle strip (a `⟨ Options ⟩` button rotated via `RotateTransform`) that expands it back — implemented as a bound `IsExpanded`-style property toggling the pane's `Width`/`MinWidth` between 0 (well, the strip's own width) and its last expanded width, not literal 0, so the toggle strip itself stays visible when collapsed. Collapsing doesn't discard unsaved edits in the Options table.

Each pane gets a labeled header ("Project", "Build Output", "Options") — currently the panes have no headers at all.

### Theme — "Signal"

A new `Signal.axaml` style resource (in `src/Omen.GUI/Styles/`), merged into `App.axaml` after `FluentTheme`, overriding specific control templates/brushes rather than replacing the whole theme:

- `AccentBrush` = `#3FA9F5`, window background `#10141A`, panel background `#181E27`, secondary text `#A8C2D9`, bright accent text (e.g. active progress) `#6EC1FF`.
- Toolbar/menu buttons and the Tree/Output/Options panel borders get 6-10px `CornerRadius`.
- Toolbar buttons gain icons: a small set of inline-SVG `Path` geometries (Open/Build/Rebuild/Clean/Cancel/Generate) defined as a XAML `ResourceDictionary` — no new NuGet dependency, no icon font, just path data drawn via `Avalonia.Controls.Shapes.Path` or a `Viewbox`-wrapped `Canvas`, matching how Avalonia samples typically embed small icon sets.
- A status-bar dot (`Ellipse`, 8px) bound to build state: gray (idle) / accent blue (building) / green (success) / red (failed) — sits to the left of the existing status text.
- `Avalonia.Fonts.Inter` (already a dependency) is applied deliberately to headers/labels via a `FontFamily` resource, rather than left at whatever Avalonia's default resolves to.

## Components

- **`Omen.Core/Options/BuildOptions.cs`** (new) — the four `Declare` overloads, `BuildOptionDeclaration`, `BuildOptionType` enum.
- **`Omen.Core/Configuration/BuildContext.cs`** (modified) — add `DeclaredOptions` (mutable, populated during rule instantiation) and `CachedOptionValues` (read-only, populated by the caller before instantiation).
- **`Omen.Executors/Orchestration/OptionsOrchestrator.cs`** (new) — discovery, described above.
- **`Omen.Executors/Orchestration/BuildOrchestrator.cs`, `ProjectGenerationOrchestrator.cs`** (modified) — load `omen-cache.json` before instantiating rules.
- **`Omen.GUI/ViewModels/OptionsPanelViewModel.cs`** (new) — holds the discovered options as an `ObservableCollection`, a "Configure" command that re-runs discovery and persists edits to `omen-cache.json`.
- **`Omen.GUI/Views/MainWindow.axaml`** (modified) — three-pane layout with splitters, the collapsible Options pane, section headers.
- **`Omen.GUI/Styles/Signal.axaml`** (new) — the theme resource dictionary described above.

## Data flow

1. On Open Project (or a "Configure" button in the Options pane), the GUI calls `OptionsOrchestrator.DiscoverAsync` → `OptionsPanelViewModel.Options` populates.
2. User edits a value (checkbox toggle, text edit, numeric updown, or path picker).
3. Clicking "Configure" again writes the edited values to `omen-cache.json`, then re-runs discovery (so a conditionally-declared option depending on another option's now-changed value gets refreshed — matching CMake's iterative-configure behavior where a changed option can reveal or hide others).
4. Build/Rebuild/Generate proceed as before; `BuildOrchestrator`/`ProjectGenerationOrchestrator` load the same `omen-cache.json` into the `BuildContext` they construct, so the edited values are what the actual build sees — no separate propagation step.

## Error handling

A rule-compilation failure during `OptionsOrchestrator.DiscoverAsync` reports an `Error`-level `OrchestratorEvent` and returns `null`, matching `BuildOrchestrator`'s existing failure shape — the Options pane shows "Configure failed" and keeps whatever options were last successfully discovered (doesn't clear the table on a failed re-configure, since a rules-file typo shouldn't erase a user's option edits). A malformed or unreadable `omen-cache.json` is treated as an empty cache (log a warning, don't crash) rather than failing the whole operation — consistent with `GuiSettings`'s existing malformed-JSON handling.

## Testing

`BuildOptions.Declare`'s four overloads, `OptionsOrchestrator.DiscoverAsync`, and the `omen-cache.json` round-trip get real unit tests in `Omen.Core.Tests`/`Omen.Executors.Tests` respectively, following the existing temp-directory-and-real-rule-compilation pattern. Specific cases: declaring the same option twice returns the first declaration's default; an edited cache value is returned instead of the default on the next discovery; a missing/corrupt cache file doesn't throw. The panel UI and the Signal theme are verified by running the app, per this GUI's established bar — no pixel/snapshot testing.
