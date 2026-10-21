# Omen GUI: Gum-Inspired Layout Polish

## Context

The Omen GUI (Avalonia, "Signal" theme — blue `#3FA9F5` accent on dark slate,
built across two prior plans this session) currently has three panes
(Project Tree | Build Output | side panel), where the side panel holds
Options and Matrix as two independent collapsible panes side by side.

The user asked for the GUI to look more like Gum (FlatRedBall's WYSIWYG UI
editor), sharing a screenshot of Gum's own layout. Gum is a full visual
editor (edits X/Y/Width/alignment on individual widgets) — a functionally
different tool from Omen's build-shell GUI, so this spec adopts Gum's
*layout and component conventions* where they map onto Omen, not its exact
color palette or widget-property concepts that have no Omen equivalent.

Confirmed via the visual companion (three approved mockup rounds):
overall layout, collapsible section headers, and segmented toggle buttons
replacing the Matrix panel's checkboxes.

## Non-goals

- **Categorizing build options by type** (Bool/Int/String/Path) into
  separate sections. The two-section split shown in the mockup illustrated
  the *section-header pattern*, not a request to add new grouping to
  `BuildOptionViewModel`/`OptionsPanelViewModel`. One "Build Options"
  section covers the panel as it exists today.
- **Changing the Platform/Configuration dropdowns** (shown for a target
  without a declared matrix) to segmented buttons. Platform's value set is
  open-ended (`PlatformFactory` is pluggable), which doesn't fit a
  fixed-width segmented control the way a matrix dimension's small,
  closed value set does.
- **Any canvas/zoom UI.** Gum's canvas and zoom controls exist because it's
  a visual editor; Omen has no equivalent surface for them to apply to.
- **Gum's exact color palette.** Signal's blue-on-slate identity stays;
  only layout/component conventions are borrowed.

## Architecture

### Tree search box

A `TextBox` above the Project Tree, filtering `ProjectTreeNode`s by
case-insensitive substring match against their display name. Filtering is
client-side over the tree already built by `ProjectTreeNode.BuildTree` —
no new backend concept, no orchestrator involvement. An empty search box
shows the full tree, unchanged from today.

### Options/Matrix as tabs

The two existing side-panel panes (`OptionsPanelViewModel`,
`MatrixPanelViewModel`, both already built) merge into one collapsible
panel with a two-tab header ("Options" | "Matrix"). Only one tab's content
is visible at a time. This is a **View-layer-only** change: no new
ViewModel wrapper. `MainWindow.axaml` gains two tab-header buttons whose
click handlers flip a `SelectedSidePanelTab` enum on `MainWindowViewModel`
(`Options` | `Matrix`), and each tab's content panel's `IsVisible` binds to
that selection — the same `IsVisible`-toggling pattern
`MatrixPanel.IsActive` already uses for hiding the Platform/Configuration
`ComboBox`es. The panel's own collapse/expand toggle (built in the prior
Options+Polish plan) is unaffected — collapsing hides whichever tab is
currently selected, exactly as it hides the single panel today.

### Collapsible section headers

A reusable visual pattern, not a new control type: a `Border`+`TextBlock`
combination styled as a colored bar (`#232B36` background, chevron
▾/▸ prefix, semibold `#A8C2D9` text) whose click toggles an adjacent
content panel's `IsVisible`. Implemented as an Avalonia `Styles` selector
(`Classes="sectionHeader"`) plus a small `bool IsExpanded` toggle in the
consuming View's code-behind or a lightweight ViewModel property —
whichever the implementer finds requires less new state, since this is
purely a display concern with no data-model implications either way.

Applied to:
- Each pane's own header ("Project", "Build Output") — restyled from plain
  `TextBlock.paneHeader` text to the colored-bar treatment. These stay
  non-collapsible (no chevron, no click handler) — panes already collapse
  as a whole via the existing pane-level toggle strip; a second, nested
  collapse control on the header itself would be redundant.
- The Options tab's content, wrapped in exactly one collapsible
  "Build Options" section (see Non-goals — no further subdivision).

### Segmented toggle buttons

The Matrix panel's per-`DimensionValueViewModel` `CheckBox` becomes a
`ToggleButton` bound to the same `IsChecked` property — multi-select
semantics are unchanged, only the visual control and its styling change.
Buttons within one dimension's row are rendered edge-to-edge (no gap),
with corner-rounding only on the first and last button in the row
(`Classes="segmentedFirst"`/`"segmentedMiddle"`/`"segmentedLast"`,
assigned by the View based on each value's position in its parent group's
`Values` collection). Checked = `#3FA9F5` background / dark text (matches
the Build button's existing `primary` style); unchecked = `#0F1319`
background / `#7C93A8` text.

### Status bar error count

`MainWindowViewModel` gains a computed `ErrorCount` property (count of
`OutputLevel.Error` entries in the current `OutputLines`), rendered next to
the existing status-dot/status-text as `"{ErrorCount} Errors"` — mirrors
Gum's own bottom-bar "0 Errors" readout.

## Components

- **Modify** `src/Omen.GUI/Styles/Signal.axaml` — extend `paneHeader` with
  a background bar; add `sectionHeader`, `segmented`/`segmentedFirst`/
  `segmentedMiddle`/`segmentedLast` style classes.
- **Modify** `src/Omen.GUI/ViewModels/MainWindowViewModel.cs` — add a
  search-filter string property, `SelectedSidePanelTab` enum property,
  `ErrorCount` computed property.
- **Modify** `src/Omen.GUI/Models/ProjectTreeNode.cs` (or wherever the
  tree's binding/filtering logic best lives — read the current file before
  deciding) — apply the search filter.
- **Modify** `src/Omen.GUI/ViewModels/MatrixPanelViewModel.cs` /
  `DimensionValueViewModel` — no property changes needed (`IsChecked`
  already exists); only the View's control type changes.
- **Modify** `src/Omen.GUI/Views/MainWindow.axaml`/`.axaml.cs` — tree
  search box, tab headers replacing the side-by-side Options/Matrix panes,
  collapsible-section wrapper around Options content, restyled pane
  headers, segmented `ToggleButton`s in the Matrix panel, status-bar error
  count.

## Data flow

Entirely View/ViewModel-local — no orchestrator, CLI, or Core changes.
Search filtering, tab selection, and section-collapse state are all
UI-only state that resets on project reload (matching how the GUI already
treats e.g. `OutputLines`, which clears per build) — no persistence beyond
the session, consistent with the Options+Polish plan's existing choice not
to persist the Options/Matrix panel's collapsed state either.

## Error handling

None beyond what already exists — this is a pure visual/interaction pass
over already-working data paths (Options/Matrix panels, project tree,
build output). No new failure modes are introduced.

## Testing

Same bar the two prior GUI-polish tasks in this session used: no dedicated
`Omen.GUI.Tests` project exists, so verification is `dotnet build`
(Avalonia's compiled-binding XAML compiler validates every `{Binding}`
path at build time) plus a timed headless `dotnet run` confirming no
startup exception, plus manual reasoning about each binding path against
the actual ViewModel members it targets. No new automated tests are
expected from this spec.
