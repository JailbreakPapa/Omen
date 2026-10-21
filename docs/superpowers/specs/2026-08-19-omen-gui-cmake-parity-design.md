# Omen.GUI: closing the gap with cmake-gui's Options panel

## Context

`Omen.GUI` (Avalonia, `src/Omen.GUI`) already has a working Options panel:
`Omen.Core.Options.BuildOptions.Declare(...)` lets a `.module.cs`/`.target.cs`/`.gem.cs`
declare a user-configurable option (in the spirit of CMake's `option()`/`set(... CACHE)`),
recorded onto `BuildContext.DeclaredOptions`; `OptionsPanelViewModel.ConfigureAsync`
already mirrors cmake-gui's Configure button (persist edits, re-run discovery); 4 typed
widgets exist via `BuildOptionViewModel` (`Bool`/`String`/`Int`/`Path`,
checkbox/textbox/numeric-updown/path-textbox respectively); a separate
`GenerateProjectFilesAsync` already exists on `MainWindowViewModel` for the
Generate-project-files step.

Comparing directly against cmake-gui (screenshot reviewed during brainstorming: search
bar, Grouped/Advanced checkboxes, Add Entry/Remove Entry, Configure/Generate/Open
Project buttons, a Name/Value table), several real gaps exist. This spec closes them.

**Explicitly not a gap:** cmake-gui's list in the reviewed screenshot (`ASSIMP_*`,
`BOX3D_*`, ...) is CMake auto-surfacing every vendored third-party library's own
`option()` calls — hundreds of entries from libraries Omen doesn't control the build of.
Omen's options list only ever shows what a rules file explicitly calls
`BuildOptions.Declare` for. This is correct behavior, not something to replicate — the
list grows exactly as fast as things get converted to Omen, same as everything else in
this project.

## Scope

**In scope, additions to `Omen.Core.Options`:**

- `BuildOptionDeclaration.IsAdvanced` (`bool`, default `false`) and a matching
  `BuildOptions.Declare(context, name, description, defaultValue, advanced: false)`
  overload on all 4 existing `Declare`/`DeclarePath` methods (optional parameter,
  source-compatible with every existing call site — `GemSampleTarget.target.cs`'s
  `VERBOSE_GREETING`/`LOAD_WINDOW` declarations keep compiling unchanged).
- A new `BuildOptionType.Enum` value, plus `BuildOptions.DeclareEnum(context, name,
  description, string[] allowedValues, string defaultValue)`. `BuildOptionDeclaration`
  gains `IReadOnlyList<string>? AllowedValues` (null for every non-Enum type).

**In scope, additions to `Omen.GUI`:**

- **Search box** above the options list (`OptionsPanelViewModel.SearchText`,
  `[ObservableProperty]`), filtering `Options` by substring match against `Name` and
  `Description`, case-insensitive. A `FilteredOptions` computed collection (or a
  `CollectionView`-style filter, whichever fits the existing Avalonia binding pattern
  in `OptionsPanelViewModel`) is what the view binds to instead of `Options` directly.
- **Advanced toggle**: a checkbox (`ShowAdvanced`, default `false`, matching cmake-gui's
  default-hidden behavior) hiding/showing options where `IsAdvanced` is true.
- **Grouped toggle**: a checkbox (`GroupByPrefix`) that, when on, buckets
  `FilteredOptions` by the option name's prefix before its first `_` (matching
  cmake-gui's grouping convention) into a nested/grouped list structure for the view.
- **Add Entry / Remove Entry**: two buttons. Add Entry opens a small dialog
  (name/description/type/default value) and appends a `BuildOptionViewModel` not backed
  by any rules-file declaration; Remove Entry deletes the selected row. Both persist
  through the same `orchestrator.SaveOptions` path `ConfigureAsync` already uses — an
  entry added this way survives a Configure re-run because it's saved as a cached
  option value, the same mechanism a rules-file-declared option's edited value already
  uses (`context.CachedOptionValues`).
- **Path browse button**: `BuildOptionViewModel.IsPath` already exists; wire an
  Avalonia `OpenFolderDialog`/`OpenFileDialog` (folder vs. file distinction follows
  whatever the existing `Path` type already implies — this spec doesn't add a new
  FilePath-vs-DirectoryPath split, since `BuildOptions.DeclarePath` doesn't distinguish
  them today either) behind a browse button next to the existing path textbox.
- **Enum widget**: a dropdown (`ComboBox`) bound to `AllowedValues`, shown when
  `Type == BuildOptionType.Enum`, following the same `IsBool`/`IsString`/etc. boolean-
  flag pattern `BuildOptionViewModel` already uses (add `IsEnum`).
- **Changed-since-configure highlighting**: track each option's value at the start of
  the current session/last Configure; a row whose current `Value` differs gets a
  distinct visual treatment (matching cmake-gui's red-until-next-Configure convention).
  Exact styling deferred to implementation (follow `Styles/Signal.axaml`'s existing
  conventions rather than inventing new ones).

**Out of scope:**
- Auto-discovering or proxying third-party CMake cache variables (see "Explicitly not a
  gap" above).
- A distinct visual "Generate" button if `MainWindowViewModel.GenerateProjectFilesAsync`
  already has one wired in the view — confirm during implementation; only add one if it
  turns out Configure and Generate are currently collapsed into a single control.
- Any change to how `.module.cs`/`.target.cs`/`.gem.cs` files declare options beyond
  the new optional `advanced` parameter and `DeclareEnum` — existing call sites need no
  changes.

## Testing

- `Omen.Core.Tests`: unit tests for `BuildOptions.Declare(..., advanced: true)` setting
  `IsAdvanced`, and `DeclareEnum` populating `AllowedValues` and clamping/validating the
  default against them (following the existing `BuildOptionDeclaration`/`ResolveAndRecord`
  test-free pattern — check whether `Omen.Core.Tests` already covers `BuildOptions.cs`
  before assuming a from-scratch test file).
- `Omen.GUI` has no existing test project visible under `tests/` for GUI logic
  specifically — confirm during implementation whether one exists; if not, keep new
  logic (search filtering, grouping, advanced filtering) in plain, testable
  `OptionsPanelViewModel` methods/properties rather than code-behind, so it's unit-
  testable without spinning up Avalonia's UI thread, consistent with the MVVM split the
  existing `ViewModels/` folder already follows.
- Manual verification: run `Omen.GUI`, load a target with several declared options
  (mix of advanced/non-advanced, at least one Enum), confirm search/grouping/advanced
  filtering behave as cmake-gui's do, confirm Add Entry survives a Configure re-run.

## Risks / open questions

- `FilteredOptions`/grouping interacting with in-place edits (editing a
  `BuildOptionViewModel.Value` while filtered/grouped) needs the same underlying
  `BuildOptionViewModel` instances referenced from both the flat and grouped views,
  not copies — an implementation detail to get right, not a design fork.
- Whether `Omen.GUI` already has an Avalonia folder/file picker dependency wired
  elsewhere (for a different panel) — reuse it rather than introducing a second pattern
  if so; check before implementing.

