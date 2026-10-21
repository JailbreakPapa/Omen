# Omen.GUI: Avalonia → WPF, rebuilt in Rider's design language

## Context

`Omen.GUI` today is an Avalonia app (`src/Omen.GUI`, ~1300 lines across 17 files, one window,
no dialogs). Its Options panel just reached cmake-gui feature parity
(`2026-08-19-omen-gui-cmake-parity-design.md`) and the user manually verified every feature
works. The verdict on the result: the functionality is right, the presentation is not —
"buggy layout, doesn't look polished enough", a generic flat dark theme, poor information
density, and no native Windows feel.

An inventory of the current app confirms the "buggy" part is structural, not cosmetic:
`MainWindow.axaml:84` lays out `ColumnDefinitions="250,4,*,4,Auto,4,Auto"`, putting
`GridSplitter`s between star-sized columns and `Auto` columns that are pinned by fixed
`Width=280` children. A `GridSplitter` cannot meaningfully resize an `Auto` column pinned by
a fixed-width child, so dragging those splitters does nothing or snaps back. The same
inventory surfaced 32 other rough edges, several of them real bugs (see §7).

## Decision this spec resolves

Replace the Avalonia `Omen.GUI` outright with a WPF application whose layout, proportions,
and palette follow **JetBrains Rider's New UI** as its design north-star, using **AvalonDock**
(Dirkster.AvalonDock, MIT) for real dockable tool windows. Port every existing feature 1-to-1,
plus fix the defects found during the inventory. No new product features.

Confirmed with the user before writing:
- **Framework:** WPF, replacing Avalonia. The Avalonia project is deleted once WPF reaches parity.
- **North-star:** Rider, "1-to-1". Reference screenshots of Rider 2026 (New UI) were supplied
  and are the basis of §3 and §4.
- **Docking:** AvalonDock, not fixed panels and not a hand-rolled docking system.
- **Feature scope:** same features + the "obvious wins" (deferred minor findings and inventory
  bugs), not a product rethink.

### Boundary held deliberately

We replicate Rider's **layout, proportions, and color palette** — a widely-shared IDE design
language, and the thing the user actually asked for. We **draw our own icons** rather than
copying JetBrains' proprietary icon artwork. Visually equivalent result, no IP question.
Existing Material Design icon path data already in `Styles/Icons.axaml` is Apache-2.0 and
carries over as-is.

## Scope

**In scope:**
- A new WPF project replacing `src/Omen.GUI`, same assembly name (`Omen.GUI`), retargeted
  from `net8.0` to `net8.0-windows` with `UseWPF`, same three `ProjectReference`s
  (`Omen.Core`, `Omen.Platforms`, `Omen.Executors`).
- Every window/panel/action enumerated in §5, ported 1-to-1.
- The Rider-derived visual system in §3, applied as WPF `ResourceDictionary` theme files.
- A custom AvalonDock theme (§4.2) and the tool-window icon rails (§4.3).
- The defect fixes in §7.
- Deleting the Avalonia project and its packages once parity is reached.

**Out of scope (explicitly, not oversights):**
- New product features. Anything not in §5 is not built.
- Non-Windows platforms. WPF is Windows-only; this is a deliberate trade the user accepted
  in choosing WPF, and Omen's CLI remains the cross-platform entry point.
- Rider features Omen has no analogue for: editor tabs with real text editing, breadcrumb
  navigation into code, VCS chips, AI panels. The top bar borrows their *shape*, not their
  function.
- Theming beyond the single dark theme. No light theme, no user-customizable colors.
- Persisting dock layout across sessions (see §8, deferred with reasoning).

## 1. Project structure

```
src/Omen.GUI/
  Omen.GUI.csproj            net8.0-windows, UseWPF, WinExe
  App.xaml / App.xaml.cs     theme dictionary merge, startup
  Themes/
    Colors.xaml              raw color tokens (§3.1) — the ONLY place hex literals live
    Brushes.xaml             semantic brushes referencing Colors.xaml
    Controls.xaml            Button/TextBox/CheckBox/ComboBox/TreeView/ListBox styles
    Docking.xaml             AvalonDock retemplate (§4.2)
    Icons.xaml               Geometry resources
  Controls/
    ToolWindowRail.xaml      vertical icon rail (§4.3)
    TopBar.xaml              unified header (§4.1)
    StatusBar.xaml           breadcrumb + indicators
    PlaceholderTextBox.cs    watermark support (no WPF equivalent to Avalonia's)
    NumericTextBox.cs        integer entry (no WPF NumericUpDown)
  Views/                     MainWindow + one UserControl per tool window
  ViewModels/                ported near-verbatim from Avalonia (see §6)
  Models/                    OutputLine, ProjectTreeNode
  Converters/                bool→Visibility, inverse bool, level→brush
  Services/                  GuiSettings
```

Splitting the current single 226-line `MainWindow.axaml` into one `UserControl` per tool
window is a deliberate improvement: each pane becomes independently readable and testable,
and it removes the copy-pasted per-option template (inventory finding #9) by making the
options editor a single `DataTemplate` resource used by both flat and grouped lists.

## 2. What carries over unchanged

`CommunityToolkit.Mvvm` (`ObservableObject`, `[ObservableProperty]`) is framework-agnostic and
ports untouched. So do `ObservableCollection`, `IProgress<T>`, `CancellationTokenSource`, all
ViewModel logic, both Models, and `GuiSettings`.

**The backend seam is preserved verbatim.** The GUI's entire contract with Omen is:

```csharp
// Omen.Executors.Orchestration
BuildOrchestrator.BuildAsync(BuildOrchestratorRequest, IProgress<OrchestratorEvent>?,
                             IProgress<BuildProgress>?, CancellationToken)
CleanOrchestrator.CleanAsync(CleanOrchestratorRequest, IProgress<OrchestratorEvent>?)
ProjectGenerationOrchestrator.GenerateAsync(ProjectGenerationOrchestratorRequest,
                                            IProgress<OrchestratorEvent>?)
OptionsOrchestrator.DiscoverAsync(OptionsOrchestratorRequest, IProgress<OrchestratorEvent>?)
OptionsOrchestrator.SaveOptions(string, IReadOnlyDictionary<string,string>)
OptionsOrchestrator.AddManualOption(string, BuildOptionDeclaration)
OptionsOrchestrator.RemoveManualOption(string, string)
// Omen.Core.Rules (matrix panel only)
new RuleCompiler(cacheDir).CompileRulesAsync(projectRoot, ct)
CompiledRules.FindTargetType() / .GetSupportedTargets(Type)
// Omen.Platforms
PlatformFactory.GetAvailablePlatforms()
```

No orchestrator signature changes. If the rewrite appears to need one, that is a finding to
surface, not to design around.

Two view-type leaks must be re-mapped: `MainWindowViewModel.StatusDotBrush` returns
`Avalonia.Media.IBrush`, and `OutputLevelToBrushConverter` returns Avalonia brushes. Both
become `System.Windows.Media.Brushes` — all six named colors used (DodgerBlue, LimeGreen,
OrangeRed, Gray, Goldenrod, LightGray) exist in WPF, but per §3 they are replaced by semantic
theme brushes rather than ported as named colors.

## 3. Visual system

Derived from the supplied Rider 2026 (New UI) screenshots. Every value below lives in
`Themes/Colors.xaml`; no view file contains a hex literal.

### 3.1 Color tokens

| Token | Value | Use |
|---|---|---|
| `Surface` | `#1E1F22` | Window, tool windows, content — one near-uniform ground |
| `SurfaceRaised` | `#2B2D30` | Top bar, tool window headers, status bar |
| `SurfaceSelected` | `#2E3238` | Selected tree row, active tab |
| `SurfaceHover` | `#26282C` | Hover states |
| `Border` | `#393B40` | 1px hairline separators — the ONLY panel separation |
| `TextPrimary` | `#CED0D6` | Body text |
| `TextSecondary` | `#7A7D85` | Metadata, descriptions, muted counts |
| `TextDisabled` | `#5A5D63` | Disabled controls |
| `Accent` | `#3574F0` | Active tool window, focus, links, primary action |
| `AccentHover` | `#548AF7` | Primary hover |
| `Success` | `#499C54` | Run/Build glyph, success output |
| `Warning` | `#D6AE58` | Warning output, changed-option marker |
| `Error` | `#DB5C5C` | Error output, failed state |

The critical departure from the current app: **surfaces are near-uniform and separated by 1px
hairlines**, not by contrasting fills and 8px-rounded cards. The existing `Border.pane`
(`#181E27` + `CornerRadius=8` + one-sided `BorderThickness`) produced a rounded rectangle with
one straight edge (inventory finding #4) and is exactly the "generic flat dark theme" the user
rejected. It does not carry over.

### 3.2 Metrics

Top bar 40px · tool window header 30px · status bar 28px · icon rail 40px wide ·
rail icons 16px · tool window default widths: left 280px, right 320px, bottom 200px ·
corner radius 4px on controls only, 0 on panels · 1px borders throughout.

### 3.3 Typography

UI: Segoe UI Variable (fallback Segoe UI), 12px body / 11px secondary.
Monospace (build output only): Cascadia Code → Consolas fallback, 12px.
The current app registers the Inter font but never applies it (inventory finding #33) — dropped.

## 4. Layout

### 4.1 Top bar (single 40px row, replaces both menubar and toolbar)

Left → right: burger menu (File/Build/Project collapse into it) · project chip
(`▣ {ProjectName} ⌄`, opens/switches project) · platform chip · configuration chip ·
flexible spacer · action cluster (Build as green ▶, Rebuild, Clean, each icon+tooltip) ·
`⋮` overflow (Generate Project Files ▸ VS2022/VS2019/VS2026/VSCode/CMake/Rider) ·
settings gear · window controls.

Cancel replaces the Build glyph while a build is running, rather than sitting beside it
permanently disabled — this also fixes inventory findings #11/#12.

`IdeKind` already declares `VS2026` and `Rider`, which the current UI never surfaces
(finding #24). Both get menu items.

### 4.2 Docking

AvalonDock hosts four tool windows around a center content area:

- **Left:** Project (tree)
- **Bottom:** Build Output (full window width — the single biggest usability win, since
  compiler lines currently wrap in a squeezed middle column)
- **Right:** Options and Build Matrix, tabbed together
- **Center:** a Welcome/summary pane (project path, target file, platform/config, last build
  result). Omen has no editor; leaving AvalonDock's document area empty looks broken, so it
  gets a real, minimal pane rather than a blank void.

AvalonDock's stock themes (VS2013/Metro/Aero) look nothing like Rider. `Themes/Docking.xaml`
retemplates `LayoutDocumentPaneControl`, `LayoutAnchorablePaneControl`, their tab headers,
title bars, splitters, and floating windows against §3.1. **This is a substantial piece of
work, not a configuration switch** — it is called out here so it is planned as such.

### 4.3 Tool window rails

Rider's vertical icon strips are not an AvalonDock feature (its auto-hide strips are adjacent
but styled differently). `ToolWindowRail` is our own control: a 40px vertical strip on the left
and right edges, one 16px icon button per tool window, toggling that pane's visibility.
The active pane's icon shows the accent highlight plus a left accent bar, matching the
supplied screenshots.

### 4.4 Status bar

Left: project path breadcrumb (`GemSample › examples › …`, chevron-separated).
Right: build state dot + text, progress bar while building, action counts after completion.

## 5. Feature port checklist

Every item below exists today and must exist after the port. Nothing here is new.

**Menus (into burger):** Open Project, Close Project, Exit · Build, Rebuild, Clean, Cancel ·
Generate Project Files ▸ (5 IDE targets, plus the 2 previously unsurfaced).

**Top bar:** Open · Platform combo (`AvailablePlatforms`) · Config combo (Debug/Development/
Release/Shipping) · Build/Rebuild/Clean/Cancel. Platform and Config hide when
`MatrixPanel.IsActive`, as today.

**Project tool window:** `TreeView` over `ProjectTreeRoots`, hierarchical, file-type icons.

**Build Output tool window:** monospace list over `OutputLines`, per-level foreground,
auto-scroll to newest.

**Options tool window:** search box · Advanced + Grouped checkboxes · flat and grouped option
lists sharing one editor template · per-option editors for Bool/String/Int/Path/Enum ·
per-option Remove (manual entries only) · Add Entry (name/description/default + **type
picker**, see §7) · Configure · status text.

**Build Matrix tool window:** dimension groups with per-value checkboxes over
`MatrixPanel.DimensionGroups`.

**Status bar:** state dot, status text, progress.

## 6. ViewModel changes

ViewModels port near-verbatim. The deliberate exceptions:

- `StatusDotBrush` moves from returning a brush to returning `BuildState`; the view maps state
  to a themed brush via a converter. Removes the view-type leak from the ViewModel.
- `OptionsPanelViewModel` gains a `NewEntryType` property so Add Entry can create non-String
  options (§7).
- `MainWindowViewModel` gains a `ToolWindows` surface for the rails to bind pane visibility to.
- Target-file discovery (`Directory.GetFiles(ProjectPath, "*.target.cs", AllDirectories)`)
  is currently duplicated at three call sites (finding #10) and collapses to one method.

## 7. Defects fixed as part of the rewrite

Found during the inventory of the current app. Each is fixed in the port rather than filed:

1. **Unusable splitters** (finding #1) — the "buggy layout". Resolved structurally by
   AvalonDock, which owns its own splitter behaviour.
2. **Cancel is a lie during Clean/Generate** (#11, #12) — `_buildCts` is only created by
   `BuildAsync`, yet `CleanAsync`/`GenerateProjectFilesAsync` set `IsBuilding=true`, enabling a
   Cancel button whose handler is a no-op. Give all three operations a CTS; if an orchestrator
   takes no `CancellationToken`, Cancel is *not offered* for it rather than offered and inert.
3. **Four silent `catch {}` blocks** (#15) — `MainWindow.axaml.cs:57,123,139` and
   `MatrixPanelViewModel.cs:55`. Failed project-open, add-entry, remove-entry, and matrix
   resolution are completely silent. Three carry a stale comment claiming the output pane
   doesn't exist yet; it does. All four report to the output pane.
4. **`CloseProject` leaves stale state** (#17) — options, matrix, and output all remain, and
   `OptionsPanelViewModel._lastTargetFile` still points at the closed project, so Add/Remove
   Entry would write to it. Clear all of it.
5. **Eager synchronous tree build on the UI thread** (#28) — `ProjectTreeNode.BuildTree` walks
   the entire tree inside `LoadProject`, which itself runs from the constructor. On a
   NightFox-sized repo this hangs the window on open. Make it lazy (populate children on
   expand) and off the UI thread.
6. **Add Entry hardcodes `BuildOptionType.String`** (#13) — the backend supports all five
   types. Add a type picker.
7. **Add Entry fields never clear** after a successful add (#14).
8. **Options list is not virtualized** (#7) — `ScrollViewer > StackPanel > ItemsControl` kills
   virtualization; a project with hundreds of options renders every editor eagerly. Use a
   virtualizing list.
9. **Duplicated per-option template** (#9) — 13 lines copy-pasted for flat vs. grouped mode,
   so every edit must be made twice. One shared `DataTemplate`.
10. **Silent no-op returns** (#18) — Build/Clean/Generate return silently when no project is
    open. Report why.
11. **`ProgressValue` never resets** on completion (#19).
12. **Double event subscription** (#20) — `OutputLines.CollectionChanged` is subscribed on
    every `DataContextChanged` and never unsubscribed, leaking the old ViewModel.
13. **Deferred minors from the parity plan** — status count shows the unfiltered total while
    the list is filtered; Add/Remove not gated on `IsBuilding`; `GroupName` returns `""` for
    names starting with `_`.

Not fixed here (deliberate): the matrix panel's conflation of "nothing checked" with
"everything checked" (#22) is documented existing behaviour with a reasoned comment, and
changing it is a behavioural decision beyond a UI port.

## 8. Testing

`Omen.GUI` has no test project today and this spec does not add one — consistent with how the
existing GUI work was accepted, and because WPF view testing needs an STA harness that is
disproportionate here.

Verification is therefore:
- **Compile-time:** the solution builds clean. WPF compiled bindings via
  `x:DataType`/`d:DataContext` catch binding-path typos at build time where possible.
- **Testable logic stays in ViewModels**, which are plain classes reachable from
  `Omen.Core.Tests` if we later choose to cover them. The port must not push logic into
  code-behind. Specifically, `FilteredOptions`/`GroupedOptions`/`GroupName`/`IsChanged` and
  the new lazy-tree logic stay in ViewModels.
- **Manual acceptance** against a checklist derived from §5 and §7, run against
  `examples/GemSample` (the only sample with real declared options). The parity plan's final
  review flagged that no human had ever run the GUI; this time manual verification is the
  stated acceptance gate and must actually be performed before the plan is considered done.

Dock layout persistence is deferred: AvalonDock can serialize layout, but persisting a layout
whose panes are still changing shape during the port risks restoring broken states. Revisit
once the layout settles. `GuiSettings` continues to persist only `LastProjectPath`.

## 9. Risks / open questions

- **AvalonDock retheming is the schedule risk.** Retemplating a third-party docking library to
  match a specific design closely is open-ended work. If it proves intractable, the fallback is
  §4's layout implemented with fixed `Grid` columns and correct splitters (which alone fixes
  finding #1), losing float/rearrange but keeping the visual design. That fallback should be
  taken only if AvalonDock's theming genuinely blocks, and the decision surfaced, not made
  silently.
- **`NumericUpDown` and watermarked `TextBox` have no WPF equivalents.** Both are small custom
  controls (`NumericTextBox`, `PlaceholderTextBox`). Straightforward, but they are net-new code
  the Avalonia version got for free.
- **Folder picking** moves from `StorageProvider.OpenFolderPickerAsync` to
  `Microsoft.Win32.OpenFolderDialog` (.NET 8). During manual verification of the previous plan,
  the native folder dialog rendered awkwardly under screen capture; this is a capture artifact,
  not an app defect, but it means folder-picking is best verified by a human directly.
- **"1-to-1" is a visual target, not a functional one.** Rider is an IDE; Omen is a build tool.
  Panes that have no Omen analogue are not invented to fill space — the center document area
  gets a genuine Welcome pane (§4.2) rather than a fake editor.
