# Rule-file editor in the Omen GUI

Opens, edits, validates, and gives IntelliSense for `*.gem.cs` / `*.module.cs` / `*.target.cs` /
`*.omenproject.cs`.

## Editor control: AvalonEdit (`ICSharpCode.AvalonEdit`, MIT), not RoslynPad

Evaluated both named options. RoslynPad.Editor.Windows bundles Roslyn completion already wired,
but it comes with its own `RoslynHost`/Workspace/MEF composition and its own idea of what
metadata references and compilation options to use. Reusing it correctly for this task would
mean fighting its own reference-resolution machinery into agreeing with `RuleCompiler`'s -
exactly the failure mode the task calls out ("a file that shows green in the editor and fails
the build"). It's also a much heavier dependency (pulls Workspaces + Features + MEF).

AvalonEdit is just a text-editing surface (with built-in C# syntax highlighting via
`HighlightingManager.Instance.GetDefinition("C#")`) and owns no opinion about compilation.
IntelliSense is then built directly on `Microsoft.CodeAnalysis.CSharp` - the package Omen.Core
*already* depends on - via `SemanticModel.LookupSymbols`/`GetTypeInfo`/`GetSymbolInfo` against
the exact `Compilation` object `RuleCompiler` builds for validation. Net result: **one new
package** (`AvalonEdit`, confirmed on `net8.0-windows` - restores and builds clean), and
completion/quick-info are guaranteed to see the same types/members a real build would, by
construction, not by convention.

## How validation shares RuleCompiler's reference set

`src/Omen.Core/Rules/RuleCompiler.cs` used to build its `CSharpParseOptions`,
`CSharpCompilationOptions`, and `List<MetadataReference>` inline inside `CompileFilesAsync`.
Refactored (behavior-preserving; `CompileRulesAsync`'s 271 pre-existing tests still pass) to:

- `RuleCompiler.ParseOptions` / `RuleCompiler.CompilationOptions` - `static readonly`, used by
  both the real compile path and validation.
- `RuleCompiler.References` - the same `List<MetadataReference>` instance the constructor built,
  exposed read-only.
- `RuleCompiler.DiscoverRuleFiles(projectRoot)` - the `*.module.cs`/`*.target.cs`/`*.gem.cs`
  file-discovery logic, factored out so nothing else re-implements "what counts as a project's
  rule files."
- `RuleCompiler.BuildCompilationAsync(projectRoot, filePath, currentText, ct)` - parses every
  rule file under `projectRoot` (the active file's *unsaved* text substituted in, everything
  else read from disk) against `ParseOptions`/`References`, returns the `Compilation` plus the
  active file's own `SyntaxTree`. The CPU-bound parse+compile runs inside `Task.Run` so a caller
  on a UI thread never blocks.
- `RuleCompiler.ValidateFileAsync(...)` now just calls `BuildCompilationAsync` and filters
  `compilation.GetDiagnostics()` down to the active file's tree.

`Omen.GUI/Services/RuleEditorService.cs` is the *only* place in the GUI that touches
`Microsoft.CodeAnalysis` types. It calls `ValidateFileAsync` (for diagnostics) and
`BuildCompilationAsync` (for completion/quick-info) and translates Roslyn's `Diagnostic`/
`ISymbol` into small display records (`RuleDiagnostic`, `RuleCompletionItem`, `RuleQuickInfo`).
It builds **no** `MetadataReference` list, parse options, or compilation options of its own - if
it needs a new one, that's a bug.

If a project has zero rule files (or the file is a standalone `*.omenproject.cs`, which
`RuleCompiler` doesn't scan for at all), `BuildCompilationAsync` falls back to compiling just the
one file against the same references - still meaningful for catching its own local errors.

## What's covered - editor + validation (scope item 1, done)

- `Views/RuleEditorView.xaml(.cs)` hosts one `AvalonEdit.TextEditor` per open document. All state
  (text, dirty, diagnostics) lives on `Models/RuleEditorDocument.cs`; the view only wires
  AvalonEdit events to it (per the "logic in ViewModels/services, not code-behind" rule).
- Validation runs 400ms after the last keystroke (debounced, cancels any in-flight validation),
  and immediately on Save. The CPU-bound compile is `Task.Run`'d inside `RuleCompiler` itself, so
  every caller - GUI included - gets this for free; the GUI never blocks its UI thread waiting on
  a compile.
- Diagnostics show up two ways: inline (a translucent red/gold highlight under the diagnostic's
  span, painted by `Controls/RuleDiagnosticRenderer.cs`, an `IBackgroundRenderer` - a flat fill
  rather than a hand-rolled squiggly-underline geometry, since AvalonEdit has no squiggle
  primitive and the highlight already reads clearly; upgrade if a human wants the classic
  wavy-line look) and in the existing Problems tab (`OutputLine` gained nullable
  `SourceFile`/`Line`/`Column` fields; `MainWindowViewModel.OnRuleDocumentDiagnosticsChanged`
  mirrors one document's diagnostics into `OutputLines`, replacing that file's previous lines on
  each revalidation rather than appending duplicates).
- Ctrl+S saves the active document (`MainWindow.xaml.cs`'s existing `OnWindowPreviewKeyDown`).

## Opening files (scope item 2, done)

- Project tree: double-clicking a rule file (`ProjectToolWindow.xaml.cs`,
  `OnTreeMouseDoubleClick`) opens it via `MainWindowViewModel.OpenRuleFile`.
- Search overlay: a rule file among the file results now also opens in the editor
  (`MainWindowViewModel.ActivateFileResult`), on top of the existing "reveal in tree" behavior.
- Both funnel through `RuleEditorPanelViewModel.OpenFile`, which dedupes by path (opening an
  already-open file just activates its tab, never a second copy).

## Unsaved-changes safety

`RuleEditorPanelViewModel.RequestClose(document)`: a clean document closes immediately; a dirty
one instead sets `PendingCloseDocument`, which drives `Views/UnsavedChangesDialog.xaml` (same
modal-overlay pattern as `SettingsDialog`/`SearchOverlay` - a `Grid` visible only while
`IsUnsavedPromptOpen`, no backdrop-click-to-dismiss since silently discarding a build-driving
file's edits should never be one careless click). Save / Don't Save / Cancel map to
`ConfirmCloseSave` / `ConfirmCloseDiscard` / `CancelClose`. The document's own tab-close button
(AvalonDock's X) is intercepted in `MainWindow.xaml.cs`'s `OnDocumentClosing` and always
cancelled+redirected through `RequestClose`, so there is exactly one close path, not two. Closing
the whole project (`MainWindowViewModel.CloseProject`) calls `RuleEditor.Reset()`, which disposes
every open document's debounce timer;
`RuleEditorPanelViewModel.HasUnsavedChanges` is exposed for a future "confirm before closing a
project with unsaved rule-file edits" gate, but nothing wires it up yet - not asked for by the
task, and `CloseProject`'s call sites did not previously prompt for anything either.

## IntelliSense (scope item 3)

- **Completion**: on typing `.` or Ctrl+Space, `RuleEditorService.GetCompletionsAsync` builds the
  shared `Compilation`, finds the token before the caret, and either resolves it to a
  type/namespace (static member access, e.g. `GemFlavorKind.`) or an expression type (instance
  access, e.g. `runtime.`) via `GetSymbolInfo`/`GetTypeInfo`, then calls
  `SemanticModel.LookupSymbols` scoped to that container - or, with no `.` context, scoped to the
  caret position for locals/types/keywords in scope. Real member/type completion against the
  actual Omen.Core API surface, covered by tests (`GemFlavorKind.` offers `Static`/`Runtime`;
  `runtime.` on a `GemFlavor` offers `PrivateDependencies`).
- **Quick info**: hovering a symbol (`RuleEditorView.OnEditorMouseHover`) shows its
  `ToDisplayString()` signature plus its XML-doc `<summary>` where one exists (a plain
  `<summary>` extraction from `symbol.GetDocumentationCommentXml()` - most Omen.Core types won't
  have this populated since `RuleCompiler.BuildMetadataReferences` doesn't attach a
  `DocumentationProvider` to its `MetadataReference.CreateFromFile` calls; the signature always
  shows regardless).
- **Signature help**: not implemented as a live parameter-position popup while typing inside
  `(...)`. Reduced scope, called out per the task's own priority ("solid editor + real
  diagnostics beats a half-working completion popup") - quick info on hovering a method name is
  the closest substitute today.

## What needs a human at the keyboard

This environment cannot drive the GUI (no way to launch `Omen.GUI.exe` and interact with it), so
none of the following were visually verified, only compiled/unit-tested:

- The editor actually rendering/highlighting/scrolling correctly inside an AvalonDock document
  pane, and that adding/removing `LayoutDocument`s at runtime doesn't visually glitch.
- The completion popup and quick-info tooltip actually appearing and being usable (positioning,
  dismiss behavior, keyboard navigation inside `CompletionWindow`).
- The unsaved-changes dialog's visual layout, and that Ctrl+S / double-click / search-overlay
  entry points feel right in practice.
- Whether 400ms is a good debounce value in practice, and whether the translucent diagnostic
  highlight reads well against the theme in both light/dark (this app appears dark-only today).
- General interaction with the rest of the app (rails, docking, theme density) while a rule file
  is open - no regression is expected (nothing existing was touched behaviorally beyond the
  document pane's static "Welcome" tab gaining a name and siblings), but only a human running the
  app can confirm it.

## Verification performed

- `dotnet build Omen.sln -c Debug` - succeeds, 0 errors (pre-existing `Dirkster.AvalonDock`
  NU1701 warnings only; no new ones from `AvalonEdit`).
- `Omen.Core.Tests`: 279/279 passing (271 pre-existing + 8 new: `RuleCompilerValidationTests`,
  covering `ValidateFileAsync`/`BuildCompilationAsync`/`References`).
- `Omen.Executors.Tests`: 34/34 passing (untouched).
- New `Omen.GUI.Tests` project (`tests/Omen.GUI.Tests`, added to `Omen.sln`): 20/20 passing,
  covering `RuleEditorService` (validation line/column mapping, clean-file/error-file, member and
  type completion, quick info), `RuleEditorDocument` (load/dirty/save/validate), and
  `RuleEditorPanelViewModel` (open-dedup, close-clean-vs-dirty, save/discard/cancel prompt
  resolution, tab-activation-after-close, `HasUnsavedChanges`). Only the non-UI layer is
  testable without a live window - nothing in `Views/` is covered.
- No stray `Omen.GUI_*_wpftmp.csproj` left behind by any of the above builds.

