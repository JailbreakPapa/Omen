# Omen.GUI cmake-gui Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the feature gap between Omen.GUI's Options panel and cmake-gui: search/filter, an Advanced toggle, a Grouped view, Add Entry/Remove Entry, and an Enum (dropdown) option type. Path browsing and a distinct Generate step already exist (`OnBrowsePathClick`, `OnGenerate*Click` in `MainWindow.axaml.cs`) — confirmed during design, not rebuilt here.

**Architecture:** Two independent `Omen.Core.Options` additions (Tasks 1-2, TDD, can run in parallel with each other and with the framework-library-conversion plan) feed five sequential `Omen.GUI` tasks (Tasks 3-7) that all touch the same two files (`OptionsPanelViewModel.cs`, `MainWindow.axaml`) — these are **not** good parallelization candidates against each other (concurrent edits to the same file conflict); run them in order, one worker.

**Tech Stack:** C# / .NET 8, Avalonia (Omen.GUI's existing UI framework), CommunityToolkit.Mvvm (`[ObservableProperty]`, already used throughout `ViewModels/`), xUnit + FluentAssertions (`Omen.Core.Tests`, existing `BuildOptionsTests.cs` pattern to follow exactly).

## Global Constraints

- No new NuGet dependencies — Avalonia's `StorageProvider` (already used for path browsing) and CommunityToolkit.Mvvm (already referenced) cover everything this plan needs.
- Every new `BuildOptions.Declare*` overload must be source-compatible with existing call sites (`GemSampleTarget.target.cs`'s `VERBOSE_GREETING`/`LOAD_WINDOW` declarations keep compiling unchanged) — add optional parameters, never change existing required-parameter signatures.
- `Omen.GUI` has no dedicated test project today (confirmed during design: only `Omen.Core.Tests`, `Omen.Core.Tests.Fixtures`, `Omen.Executors.Tests` exist under `tests/`) — GUI-side tasks (3-7) are manually verified by running `Omen.GUI`, consistent with how `MainWindowViewModel`/`BuildCommand.cs` are already treated in this codebase's plans. Do not scaffold a new test project as part of this plan — out of scope.
- Follow the existing test style in `tests/Omen.Core.Tests/BuildOptionsTests.cs`: one `[Fact]` per behavior, `Should()` (FluentAssertions), a private `CreateContext()` helper.

---

### Task 1: `IsAdvanced` on `BuildOptionDeclaration`

**Files:**
- Modify: `src/Omen.Core/Options/BuildOptions.cs`
- Modify: `tests/Omen.Core.Tests/BuildOptionsTests.cs`

**Interfaces:**
- Produces: `BuildOptionDeclaration.IsAdvanced` (`bool`, default `false`). New optional `advanced` parameter (default `false`) on `BuildOptions.Declare(BuildContext, string, string, bool, bool advanced = false)`, `Declare(..., string, bool advanced = false)` (the `string` default-value overload), `Declare(..., int, bool advanced = false)`, and `DeclarePath(..., bool advanced = false)`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Omen.Core.Tests/BuildOptionsTests.cs` (after `Declare_SameNameTwice_FirstDeclarationWins`):

```csharp
    [Fact]
    public void Declare_Bool_DefaultsToNotAdvanced()
    {
        var context = CreateContext();

        BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable feature X", false);

        context.DeclaredOptions.Single().IsAdvanced.Should().BeFalse();
    }

    [Fact]
    public void Declare_Bool_WithAdvancedTrue_RecordsAdvanced()
    {
        var context = CreateContext();

        BuildOptions.Declare(context, "INTERNAL_DEBUG_FLAG", "Internal debug flag", false, advanced: true);

        context.DeclaredOptions.Single().IsAdvanced.Should().BeTrue();
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter BuildOptionsTests`
Expected: FAIL with a compile error — `IsAdvanced` doesn't exist, and `Declare`'s 5th `advanced` argument doesn't exist yet.

- [ ] **Step 3: Implement**

In `src/Omen.Core/Options/BuildOptions.cs`, add to `BuildOptionDeclaration` (after `EffectiveValue`):

```csharp
    public bool IsAdvanced { get; init; }
```

Change `Declare`'s bool overload:

```csharp
    public static bool Declare(BuildContext context, string name, string description, bool defaultValue, bool advanced = false)
    {
        var effective = ResolveAndRecord(context, name, description, BuildOptionType.Bool, defaultValue ? "true" : "false", advanced);
        return effective.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
```

Change the `string`, `int`, and `DeclarePath` overloads the same way (add `bool advanced = false`, pass it through to `ResolveAndRecord`):

```csharp
    public static string Declare(BuildContext context, string name, string description, string defaultValue, bool advanced = false) =>
        ResolveAndRecord(context, name, description, BuildOptionType.String, defaultValue, advanced);

    public static int Declare(BuildContext context, string name, string description, int defaultValue, bool advanced = false)
    {
        var effective = ResolveAndRecord(context, name, description, BuildOptionType.Int, defaultValue.ToString(), advanced);
        return int.TryParse(effective, out var parsed) ? parsed : defaultValue;
    }

    public static string DeclarePath(BuildContext context, string name, string description, string defaultValue, bool advanced = false) =>
        ResolveAndRecord(context, name, description, BuildOptionType.Path, defaultValue, advanced);
```

Change `ResolveAndRecord`'s signature and body:

```csharp
    private static string ResolveAndRecord(BuildContext context, string name, string description, BuildOptionType type, string defaultValue, bool advanced)
    {
        var existing = context.DeclaredOptions.FirstOrDefault(o => o.Name == name);
        if (existing != null)
            return existing.EffectiveValue;

        var effectiveValue = context.CachedOptionValues.TryGetValue(name, out var cached) ? cached : defaultValue;

        context.DeclaredOptions.Add(new BuildOptionDeclaration
        {
            Name = name,
            Description = description,
            Type = type,
            DefaultValue = defaultValue,
            EffectiveValue = effectiveValue,
            IsAdvanced = advanced
        });

        return effectiveValue;
    }
```

- [ ] **Step 4: Run to verify it passes, then run the full suite**

Run: `dotnet test tests/Omen.Core.Tests --filter BuildOptionsTests`
Expected: PASS

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS (adding an optional trailing parameter with a default doesn't break any existing call site).

- [ ] **Step 5: Commit**

```bash
git add src/Omen.Core/Options/BuildOptions.cs tests/Omen.Core.Tests/BuildOptionsTests.cs
git commit -m "feat: add IsAdvanced to BuildOptionDeclaration, matching cmake-gui's Advanced toggle"
```

---

### Task 2: `Enum` `BuildOptionType` and `DeclareEnum`

**Files:**
- Modify: `src/Omen.Core/Options/BuildOptions.cs`
- Modify: `tests/Omen.Core.Tests/BuildOptionsTests.cs`

**Interfaces:**
- Produces: `BuildOptionType.Enum` (new enum member). `BuildOptionDeclaration.AllowedValues` (`IReadOnlyList<string>?`, `null` for every non-`Enum` type). `BuildOptions.DeclareEnum(BuildContext context, string name, string description, string[] allowedValues, string defaultValue, bool advanced = false)` returning the effective `string` value.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Omen.Core.Tests/BuildOptionsTests.cs`:

```csharp
    [Fact]
    public void DeclareEnum_NoCachedValue_ReturnsDefaultAndRecordsAllowedValues()
    {
        var context = CreateContext();

        var result = BuildOptions.DeclareEnum(context, "LOG_LEVEL", "Logging verbosity", ["Quiet", "Normal", "Verbose"], "Normal");

        result.Should().Be("Normal");
        var declared = context.DeclaredOptions.Single();
        declared.Type.Should().Be(BuildOptionType.Enum);
        declared.AllowedValues.Should().BeEquivalentTo(["Quiet", "Normal", "Verbose"]);
    }

    [Fact]
    public void DeclareEnum_WithCachedValue_ReturnsCachedOverride()
    {
        var context = CreateContext(new Dictionary<string, string> { ["LOG_LEVEL"] = "Verbose" });

        var result = BuildOptions.DeclareEnum(context, "LOG_LEVEL", "Logging verbosity", ["Quiet", "Normal", "Verbose"], "Normal");

        result.Should().Be("Verbose");
    }

    [Fact]
    public void DeclareEnum_CachedValueNotInAllowedValues_FallsBackToDefault()
    {
        var context = CreateContext(new Dictionary<string, string> { ["LOG_LEVEL"] = "Stale" });

        var result = BuildOptions.DeclareEnum(context, "LOG_LEVEL", "Logging verbosity", ["Quiet", "Normal", "Verbose"], "Normal");

        result.Should().Be("Normal");
    }

    [Fact]
    public void Declare_Bool_AllowedValuesIsNull()
    {
        var context = CreateContext();

        BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable feature X", false);

        context.DeclaredOptions.Single().AllowedValues.Should().BeNull();
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter BuildOptionsTests`
Expected: FAIL with a compile error — `BuildOptionType.Enum`, `AllowedValues`, and `DeclareEnum` don't exist yet.

- [ ] **Step 3: Implement**

In `src/Omen.Core/Options/BuildOptions.cs`, add `Enum` to `BuildOptionType`:

```csharp
public enum BuildOptionType
{
    Bool,
    String,
    Int,
    Path,
    Enum
}
```

Add to `BuildOptionDeclaration` (after `IsAdvanced` from Task 1):

```csharp
    public IReadOnlyList<string>? AllowedValues { get; init; }
```

Add `DeclareEnum`, following the same validate-cached-then-fall-back-to-default pattern as `Declare`'s `Int` overload:

```csharp
    public static string DeclareEnum(BuildContext context, string name, string description, string[] allowedValues, string defaultValue, bool advanced = false)
    {
        var existing = context.DeclaredOptions.FirstOrDefault(o => o.Name == name);
        if (existing != null)
            return existing.EffectiveValue;

        var cached = context.CachedOptionValues.TryGetValue(name, out var cachedValue) ? cachedValue : null;
        var effectiveValue = cached != null && allowedValues.Contains(cached) ? cached : defaultValue;

        context.DeclaredOptions.Add(new BuildOptionDeclaration
        {
            Name = name,
            Description = description,
            Type = BuildOptionType.Enum,
            DefaultValue = defaultValue,
            EffectiveValue = effectiveValue,
            IsAdvanced = advanced,
            AllowedValues = allowedValues
        });

        return effectiveValue;
    }
```

`DeclareEnum` doesn't reuse `ResolveAndRecord` (unlike Task 1's overloads) because it needs the allowed-values validation step `ResolveAndRecord` doesn't have — duplicating the declared-once-wins/cache-lookup shape here rather than adding an `allowedValues`-aware branch to the shared helper keeps `ResolveAndRecord` simple for the four callers that don't need it.

- [ ] **Step 4: Run to verify it passes, then run the full suite**

Run: `dotnet test tests/Omen.Core.Tests --filter BuildOptionsTests`
Expected: PASS

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Omen.Core/Options/BuildOptions.cs tests/Omen.Core.Tests/BuildOptionsTests.cs
git commit -m "feat: add BuildOptionType.Enum and DeclareEnum, matching cmake's STRINGS cache property"
```

---

### Task 3: Search filter in the Options panel

**Files:**
- Modify: `src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.axaml`

**Interfaces:**
- Consumes: `Options` (`ObservableCollection<BuildOptionViewModel>`, existing).
- Produces: `OptionsPanelViewModel.SearchText` (`string`, `[ObservableProperty]`), `OptionsPanelViewModel.FilteredOptions` (`IEnumerable<BuildOptionViewModel>`, recomputed whenever `SearchText` or `Options` changes).

No automated test (Global Constraints: no `Omen.GUI` test project) — manually verified in Step 3.

- [ ] **Step 1: Add `SearchText` and `FilteredOptions`**

In `src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs`, add after the `_statusText` field:

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredOptions))]
    private string _searchText = "";

    public IEnumerable<BuildOptionViewModel> FilteredOptions =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Options
            : Options.Where(o =>
                o.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                o.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
```

`Options` is an `ObservableCollection`, not itself observable-for-`FilteredOptions`-recomputation on `Add`/`Clear` — `ConfigureAsync` already fully repopulates `Options` (`Options.Clear()` then `Options.Add(...)` in a loop) on every call, and the view binds to `FilteredOptions` freshly each time `SearchText` changes; add one more line at the end of `ConfigureAsync` (right after the existing `StatusText = ...` line) to also notify `FilteredOptions` after a fresh `Options` repopulation:

```csharp
        OnPropertyChanged(nameof(FilteredOptions));
```

- [ ] **Step 2: Bind the search box and switch the list to `FilteredOptions`**

In `src/Omen.GUI/Views/MainWindow.axaml`, inside the `Options` pane's `DockPanel` (around line 128, `<StackPanel DockPanel.Dock="Bottom" Margin="8">`), add a search box between the header `Grid` and the bottom `StackPanel`:

```xml
                        <TextBox DockPanel.Dock="Top" Text="{Binding OptionsPanel.SearchText}" Watermark="Search..." Margin="8,4" />
```

Change the `ItemsControl`'s binding from `OptionsPanel.Options` to `OptionsPanel.FilteredOptions`:

```xml
                            <ItemsControl ItemsSource="{Binding OptionsPanel.FilteredOptions}">
```

- [ ] **Step 3: Manually verify**

Run `dotnet run --project src/Omen.GUI`, open a project with several declared options (e.g. `examples/GemSample`), type a substring of one option's name into the new search box. Expected: the list narrows to matching options live; clearing the search box restores the full list.

- [ ] **Step 4: Commit**

```bash
git add src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs src/Omen.GUI/Views/MainWindow.axaml
git commit -m "feat: add a search box to the Options panel, matching cmake-gui's Search field"
```

---

### Task 4: Advanced and Grouped toggles

**Files:**
- Modify: `src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs`
- Modify: `src/Omen.GUI/ViewModels/BuildOptionViewModel.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.axaml`

**Interfaces:**
- Consumes: `BuildOptionDeclaration.IsAdvanced` (Task 1).
- Produces: `OptionsPanelViewModel.ShowAdvanced` (`bool`, `[ObservableProperty]`, default `false`), `OptionsPanelViewModel.GroupByPrefix` (`bool`, `[ObservableProperty]`, default `false`), `BuildOptionViewModel.IsAdvanced` (`bool`, `required`), `BuildOptionViewModel.GroupName` (`string`, computed: the option name's substring before its first `_`, or the full name if there's no `_`). `FilteredOptions` (Task 3) additionally filters by `ShowAdvanced`.

No automated test — manually verified in Step 4.

- [ ] **Step 1: Carry `IsAdvanced` onto `BuildOptionViewModel`**

In `src/Omen.GUI/ViewModels/BuildOptionViewModel.cs`, add:

```csharp
    public required bool IsAdvanced { get; init; }

    /// <summary>
    /// The option name's prefix before its first underscore (or the full name if there is
    /// no underscore) - used by OptionsPanelViewModel.GroupByPrefix, matching cmake-gui's
    /// grouping convention.
    /// </summary>
    public string GroupName => Name.Contains('_') ? Name[..Name.IndexOf('_')] : Name;
```

In `src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs`'s `ConfigureAsync`, pass it through when constructing each `BuildOptionViewModel`:

```csharp
            Options.Add(new BuildOptionViewModel
            {
                Name = declaration.Name,
                Description = declaration.Description,
                Type = declaration.Type,
                Value = declaration.EffectiveValue,
                IsAdvanced = declaration.IsAdvanced
            });
```

- [ ] **Step 2: Add `ShowAdvanced`/`GroupByPrefix` and update `FilteredOptions`**

In `OptionsPanelViewModel.cs`, add next to `SearchText`:

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredOptions))]
    private bool _showAdvanced;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupedOptions))]
    private bool _groupByPrefix;
```

Update `FilteredOptions` to also filter on `ShowAdvanced`:

```csharp
    public IEnumerable<BuildOptionViewModel> FilteredOptions =>
        Options
            .Where(o => ShowAdvanced || !o.IsAdvanced)
            .Where(o => string.IsNullOrWhiteSpace(SearchText) ||
                        o.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        o.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
```

Add `GroupedOptions`, grouping `FilteredOptions` by `GroupName`:

```csharp
    public IEnumerable<IGrouping<string, BuildOptionViewModel>> GroupedOptions =>
        FilteredOptions.GroupBy(o => o.GroupName).OrderBy(g => g.Key);
```

`GroupedOptions` depends on `FilteredOptions`, which depends on `SearchText`/`ShowAdvanced` — add `[NotifyPropertyChangedFor(nameof(GroupedOptions))]` alongside the existing `[NotifyPropertyChangedFor(nameof(FilteredOptions))]` on both `_searchText` and `_showAdvanced` so `GroupedOptions` stays in sync too. Also add `OnPropertyChanged(nameof(GroupedOptions));` next to the `OnPropertyChanged(nameof(FilteredOptions));` line added in Task 3 Step 1.

- [ ] **Step 3: Wire the checkboxes and grouped/flat view switch into the XAML**

In `MainWindow.axaml`, add two checkboxes next to the search box (Task 3 Step 2's `TextBox`):

```xml
                        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="8,0,8,4" Spacing="8">
                            <CheckBox Content="Advanced" IsChecked="{Binding OptionsPanel.ShowAdvanced}" />
                            <CheckBox Content="Grouped" IsChecked="{Binding OptionsPanel.GroupByPrefix}" />
                        </StackPanel>
```

Replace the single `ItemsControl` (bound to `FilteredOptions`) with two, toggled by `GroupByPrefix` — the existing flat one stays for the non-grouped case, gated `IsVisible="{Binding !OptionsPanel.GroupByPrefix}"`; add a second, gated `IsVisible="{Binding OptionsPanel.GroupByPrefix}"`, bound to `GroupedOptions`, with a two-level `DataTemplate` (group header, then the same per-option template nested):

```xml
                            <ItemsControl ItemsSource="{Binding OptionsPanel.FilteredOptions}" IsVisible="{Binding !OptionsPanel.GroupByPrefix}">
                                <!-- existing DataTemplate from Task 2/3, unchanged -->
                            </ItemsControl>
                            <ItemsControl ItemsSource="{Binding OptionsPanel.GroupedOptions}" IsVisible="{Binding OptionsPanel.GroupByPrefix}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <StackPanel Margin="0,4">
                                            <TextBlock Text="{Binding Key}" FontWeight="SemiBold" Margin="8,4,8,0" Opacity="0.8" />
                                            <ItemsControl ItemsSource="{Binding}">
                                                <ItemsControl.ItemTemplate>
                                                    <!-- same per-option DataTemplate as the flat ItemsControl above -->
                                                </ItemsControl.ItemTemplate>
                                            </ItemsControl>
                                        </StackPanel>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
```

Both `ItemsControl`s must share the exact same per-option `DataTemplate` (`Name`/`Description`/`CheckBox`/`TextBox`/`NumericUpDown`/path `Grid` from the existing template) — duplicate the template markup rather than trying to factor it into a shared resource in this task (an `<ItemsControl.ItemTemplate>` resource extraction is a reasonable follow-up, not required for this plan's scope).

- [ ] **Step 4: Manually verify**

Run `dotnet run --project src/Omen.GUI`. Confirm: an option declared with `advanced: true` (temporarily add one to `GemSampleTarget.target.cs` if none exists, e.g. `BuildOptions.Declare(context, "INTERNAL_DEBUG_FLAG", "Internal debug flag", false, advanced: true);`, revert after testing) is hidden until "Advanced" is checked; toggling "Grouped" switches between the flat list and prefix-grouped headers.

- [ ] **Step 5: Commit**

```bash
git add src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs src/Omen.GUI/ViewModels/BuildOptionViewModel.cs src/Omen.GUI/Views/MainWindow.axaml
git commit -m "feat: add Advanced and Grouped toggles to the Options panel"
```

---

### Task 5: Enum dropdown widget

**Files:**
- Modify: `src/Omen.GUI/ViewModels/BuildOptionViewModel.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.axaml` (both `ItemsControl` templates from Task 4)

**Interfaces:**
- Consumes: `BuildOptionType.Enum`, `BuildOptionDeclaration.AllowedValues` (Task 2).
- Produces: `BuildOptionViewModel.IsEnum` (`bool`), `BuildOptionViewModel.AllowedValues` (`IReadOnlyList<string>`, empty list for non-`Enum` options).

No automated test — manually verified in Step 3.

- [ ] **Step 1: Add `IsEnum`/`AllowedValues` to `BuildOptionViewModel`**

```csharp
    public bool IsEnum => Type == BuildOptionType.Enum;
    public IReadOnlyList<string> AllowedValues { get; init; } = [];
```

In `OptionsPanelViewModel.ConfigureAsync`, pass it through:

```csharp
                AllowedValues = declaration.AllowedValues ?? []
```

- [ ] **Step 2: Add the dropdown to both per-option `DataTemplate`s from Task 4**

Add, alongside the existing `CheckBox`/`TextBox`/`NumericUpDown`/path `Grid`:

```xml
                                            <ComboBox ItemsSource="{Binding AllowedValues}" SelectedItem="{Binding Value}" IsVisible="{Binding IsEnum}" HorizontalAlignment="Stretch" />
```

- [ ] **Step 3: Manually verify**

Temporarily add `BuildOptions.DeclareEnum(context, "LOG_LEVEL", "Logging verbosity", ["Quiet", "Normal", "Verbose"], "Normal");` to `GemSampleTarget.target.cs`, run `Omen.GUI`, confirm the option renders as a dropdown with the three values and its selection persists through a Configure re-run (`SaveOptions`/`CachedOptionValues` round-trip, same mechanism every other option type already uses). Revert the temporary `GemSampleTarget.target.cs` change after verifying.

- [ ] **Step 4: Commit**

```bash
git add src/Omen.GUI/ViewModels/BuildOptionViewModel.cs src/Omen.GUI/Views/MainWindow.axaml
git commit -m "feat: render Enum-type build options as a dropdown"
```

---

### Task 6: Add Entry / Remove Entry

**Files:**
- Create: `src/Omen.Executors/Orchestration/ManualOptionsStore.cs`
- Modify: `src/Omen.Executors/Orchestration/OptionsOrchestrator.cs`
- Modify: `src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.axaml`
- Test: `tests/Omen.Core.Tests/ManualOptionsStoreTests.cs` (follows `BuildOptionsTests.cs`'s style; lives in `Omen.Core.Tests` even though `ManualOptionsStore` is in `Omen.Executors` — confirm `Omen.Core.Tests` already references `Omen.Executors` before assuming; if not, add the `ProjectReference` the same way Phase 1's Task 5 added one for `Omen.Platforms`)

**Interfaces:**
- Consumes: `BuildOptionDeclaration` (existing), `OptionsOrchestrator.DiscoverAsync`/`SaveOptions` (existing).
- Produces: `ManualOptionsStore` (load/add/remove/save over a JSON sidecar file at `Intermediate/omen-manual-options.json`, mirroring `OptionCacheStore`'s existing pattern). `OptionsOrchestrator.DiscoverAsync`'s returned list now includes manually-added entries (declarations with no backing `BuildOptions.Declare` call) merged in after real discovery. `OptionsPanelViewModel.AddEntryAsync(string name, string description, BuildOptionType type, string defaultValue)` and `RemoveEntryAsync(BuildOptionViewModel option)`.

**Design note carried from the spec's risk section, resolved here:** a manually-added option has no `BuildOptions.Declare` call anywhere, so `OptionsOrchestrator.DiscoverAsync` (which only returns `context.DeclaredOptions`, populated exclusively by real `Declare` calls during rule instantiation) would silently drop it on the next Configure. `ManualOptionsStore` closes that gap by persisting manually-added entries **separately** from the per-value cache (`OptionCacheStore`) and re-merging them into every `DiscoverAsync` result — this is what makes Add Entry actually survive a Configure re-run, matching cmake-gui, rather than only lasting until the next Configure click.

- [ ] **Step 1: Write the failing test for `ManualOptionsStore`**

Create `tests/Omen.Core.Tests/ManualOptionsStoreTests.cs`:

```csharp
// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Options;
using Omen.Executors.Orchestration;

namespace Omen.Core.Tests;

public class ManualOptionsStoreTests : IDisposable
{
    private readonly string _path;

    public ManualOptionsStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ManualOptionsStoreTests), Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void AddThenLoad_RoundTripsTheEntry()
    {
        var store = new ManualOptionsStore(_path);
        store.Add(new BuildOptionDeclaration
        {
            Name = "CUSTOM_FLAG",
            Description = "A manually added flag",
            Type = BuildOptionType.Bool,
            DefaultValue = "false",
            EffectiveValue = "false"
        });
        store.Save();

        var reloaded = new ManualOptionsStore(_path);

        reloaded.All.Should().ContainSingle(d => d.Name == "CUSTOM_FLAG");
    }

    [Fact]
    public void Remove_DropsTheEntry()
    {
        var store = new ManualOptionsStore(_path);
        store.Add(new BuildOptionDeclaration
        {
            Name = "CUSTOM_FLAG",
            Description = "A manually added flag",
            Type = BuildOptionType.Bool,
            DefaultValue = "false",
            EffectiveValue = "false"
        });

        store.Remove("CUSTOM_FLAG");

        store.All.Should().BeEmpty();
    }

    [Fact]
    public void Load_NoFileYet_ReturnsEmpty()
    {
        var store = new ManualOptionsStore(_path);
        store.All.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Omen.Core.Tests --filter ManualOptionsStoreTests`
Expected: FAIL with a compile error — `ManualOptionsStore` doesn't exist yet.

- [ ] **Step 3: Implement `ManualOptionsStore`**

Create `src/Omen.Executors/Orchestration/ManualOptionsStore.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;
using Omen.Core.Options;

namespace Omen.Executors.Orchestration;

/// <summary>
/// Persists build options added manually through the GUI's Add Entry button (no backing
/// BuildOptions.Declare call in any rules file). OptionsOrchestrator.DiscoverAsync merges
/// these into its result after real discovery, so an added entry survives a Configure
/// re-run - unlike a plain in-memory addition, which real discovery would silently drop
/// since it only returns what rules files actually declared.
/// </summary>
public sealed class ManualOptionsStore
{
    private readonly string _path;
    private readonly List<BuildOptionDeclaration> _entries;

    public ManualOptionsStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public IReadOnlyList<BuildOptionDeclaration> All => _entries;

    public void Add(BuildOptionDeclaration declaration)
    {
        _entries.RemoveAll(d => d.Name == declaration.Name);
        _entries.Add(declaration);
    }

    public void Remove(string name) => _entries.RemoveAll(d => d.Name == name);

    public void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
    }

    private static List<BuildOptionDeclaration> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<BuildOptionDeclaration>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Omen.Core.Tests --filter ManualOptionsStoreTests`
Expected: PASS

- [ ] **Step 5: Wire `ManualOptionsStore` into `OptionsOrchestrator`**

In `src/Omen.Executors/Orchestration/OptionsOrchestrator.cs`, add a `ManualOptionsPath` helper next to the existing `CachePath`:

```csharp
    private static string ManualOptionsPath(string workingDir) =>
        Path.Combine(workingDir, "Intermediate", "omen-manual-options.json");
```

In `DiscoverAsync`, right before `return context.DeclaredOptions;`, merge in manually-added entries not already present in real discovery (a manual entry whose `Name` a rules file has since started declaring for real defers to the real one):

```csharp
        var manualStore = new ManualOptionsStore(ManualOptionsPath(workingDir));
        var declaredNames = context.DeclaredOptions.Select(d => d.Name).ToHashSet();
        var merged = context.DeclaredOptions.Concat(manualStore.All.Where(d => !declaredNames.Contains(d.Name))).ToList();

        events?.Report(new OrchestratorEvent($"Found {merged.Count} option(s)", OrchestratorEventLevel.Info));

        return merged;
```

Remove the now-redundant `events?.Report(new OrchestratorEvent($"Found {context.DeclaredOptions.Count} option(s)", ...))` line that preceded it.

Add two new methods for the GUI to call:

```csharp
    public void AddManualOption(string targetFile, BuildOptionDeclaration declaration)
    {
        var workingDir = ResolveWorkingDir(targetFile);
        var store = new ManualOptionsStore(ManualOptionsPath(workingDir));
        store.Add(declaration);
        store.Save();
    }

    public void RemoveManualOption(string targetFile, string name)
    {
        var workingDir = ResolveWorkingDir(targetFile);
        var store = new ManualOptionsStore(ManualOptionsPath(workingDir));
        store.Remove(name);
        store.Save();
    }
```

- [ ] **Step 6: Add `AddEntryAsync`/`RemoveEntry` to `OptionsPanelViewModel`**

`OptionsPanelViewModel` needs to remember the current `targetFile` (it already receives one as a parameter to `ConfigureAsync` but doesn't store it) to call the new orchestrator methods. Add a field set at the top of `ConfigureAsync`:

```csharp
    private string? _lastTargetFile;
```

At the start of `ConfigureAsync`, add `_lastTargetFile = targetFile;`. Then add:

```csharp
    public async Task AddEntryAsync(string name, string description, BuildOptionType type, string defaultValue)
    {
        if (_lastTargetFile == null) return;

        new OptionsOrchestrator().AddManualOption(_lastTargetFile, new BuildOptionDeclaration
        {
            Name = name,
            Description = description,
            Type = type,
            DefaultValue = defaultValue,
            EffectiveValue = defaultValue
        });

        await ConfigureAsync(_lastTargetFile, null);
    }

    public async Task RemoveEntryAsync(BuildOptionViewModel option)
    {
        if (_lastTargetFile == null) return;

        new OptionsOrchestrator().RemoveManualOption(_lastTargetFile, option.Name);

        await ConfigureAsync(_lastTargetFile, null);
    }
```

Both re-run `ConfigureAsync` afterward so the visible `Options` list reflects the change immediately, the same way a real Configure click would.

- [ ] **Step 7: Add Add Entry / Remove Entry to the XAML**

A minimal Add Entry flow (no dialog — this plan keeps it to the same complexity level as the rest of the Options panel, which has no modal dialogs today) — add three input fields and a button to the bottom `StackPanel` (`DockPanel.Dock="Bottom"`, alongside the existing Configure button):

```xml
                        <StackPanel DockPanel.Dock="Bottom" Margin="8">
                            <TextBlock Text="{Binding OptionsPanel.StatusText}" Margin="0,0,0,6" FontSize="11" Opacity="0.8" />
                            <TextBox x:Name="NewEntryName" Watermark="New entry name" Margin="0,0,0,4" />
                            <TextBox x:Name="NewEntryDescription" Watermark="Description" Margin="0,0,0,4" />
                            <TextBox x:Name="NewEntryDefaultValue" Watermark="Default value" Margin="0,0,0,4" />
                            <Button Content="Add Entry" Click="OnAddEntryClick" HorizontalAlignment="Stretch" Margin="0,0,0,4" />
                            <Button Content="Configure" Click="OnConfigureOptionsClick" HorizontalAlignment="Stretch" Classes="primary" IsEnabled="{Binding !IsBuilding}" />
                        </StackPanel>
```

Add a Remove button to both per-option `DataTemplate`s from Task 4/5, next to the existing widgets:

```xml
                                            <Button Content="Remove" Click="OnRemoveEntryClick" Tag="{Binding}" HorizontalAlignment="Left" FontSize="11" />
```

In `MainWindow.axaml.cs`, add the two handlers (matching `OnBrowsePathClick`'s existing `Tag`-carries-the-view-model pattern):

```csharp
    private async void OnAddEntryClick(object? sender, RoutedEventArgs e)
    {
        var name = this.FindControl<TextBox>("NewEntryName")?.Text;
        var description = this.FindControl<TextBox>("NewEntryDescription")?.Text ?? "";
        var defaultValue = this.FindControl<TextBox>("NewEntryDefaultValue")?.Text ?? "";
        if (string.IsNullOrWhiteSpace(name)) return;

        await ViewModel.OptionsPanel.AddEntryAsync(name, description, BuildOptionType.String, defaultValue);
    }

    private async void OnRemoveEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BuildOptionViewModel option }) return;
        await ViewModel.OptionsPanel.RemoveEntryAsync(option);
    }
```

New entries default to `BuildOptionType.String` (the simplest, always-valid type for free-form Add Entry) — a type picker is a reasonable follow-up, out of scope here to keep this task's XAML changes proportional to the rest of the panel.

Add `using Omen.Core.Options;` to `MainWindow.axaml.cs` if not already present.

- [ ] **Step 8: Run the full `Omen.Core.Tests` suite, then manually verify**

Run: `dotnet test tests/Omen.Core.Tests`
Expected: all PASS.

Run `Omen.GUI`, open `examples/GemSample`, type a name/description/default into the new Add Entry fields, click Add Entry — confirm the new option appears in the list. Click Configure again (or reopen the project) — confirm the manually-added option is still present (this is the actual test of `ManualOptionsStore` working, not just the in-memory add). Click its Remove button, Configure again, confirm it's gone.

- [ ] **Step 9: Commit**

```bash
git add src/Omen.Executors/Orchestration/ManualOptionsStore.cs src/Omen.Executors/Orchestration/OptionsOrchestrator.cs src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs src/Omen.GUI/Views/MainWindow.axaml src/Omen.GUI/Views/MainWindow.axaml.cs tests/Omen.Core.Tests/ManualOptionsStoreTests.cs
git commit -m "feat: add Add Entry/Remove Entry to the Options panel, persisted across Configure"
```

---

### Task 7: Changed-since-Configure highlighting

**Files:**
- Modify: `src/Omen.GUI/ViewModels/BuildOptionViewModel.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.axaml`
- Modify: `src/Omen.GUI/Styles/Signal.axaml`

**Interfaces:**
- Produces: `BuildOptionViewModel.OriginalValue` (`string`, set once at construction, never mutated afterward), `BuildOptionViewModel.IsChanged` (`bool`, computed: `Value != OriginalValue`).

No automated test — manually verified in Step 3.

- [ ] **Step 1: Track the original value**

In `BuildOptionViewModel.cs`, add:

```csharp
    public required string OriginalValue { get; init; }
    public bool IsChanged => Value != OriginalValue;
```

`Value`'s existing `[ObservableProperty]` already has `[NotifyPropertyChangedFor(nameof(IsChecked))]`/`[NotifyPropertyChangedFor(nameof(NumericValue))]` — add `[NotifyPropertyChangedFor(nameof(IsChanged))]` alongside them so editing a value updates the highlight live.

In `OptionsPanelViewModel.ConfigureAsync`, set `OriginalValue = declaration.EffectiveValue` (the value as of this Configure, before any further edits) when constructing each `BuildOptionViewModel`.

- [ ] **Step 2: Add the highlight style and bind it**

In `src/Omen.GUI/Styles/Signal.axaml`, add:

```xml
    <Style Selector="TextBlock.optionChanged">
        <Setter Property="Foreground" Value="#F5A93F" />
    </Style>
```

(Amber, not red — this codebase's existing palette (`#3FA9F5` primary blue, `#A8C2D9` pane headers) doesn't use red for anything; amber reads as "changed, review before Configure" without borrowing cmake-gui's literal red, consistent with the rest of `Signal.axaml`'s existing hue choices.)

In `MainWindow.axaml`, add `Classes.optionChanged="{Binding IsChanged}"` to each `TextBlock Text="{Binding Name}"` in both per-option `DataTemplate`s (flat and grouped, from Task 4):

```xml
                                            <TextBlock Text="{Binding Name}" FontWeight="Bold" Classes.optionChanged="{Binding IsChanged}" />
```

- [ ] **Step 3: Manually verify**

Run `Omen.GUI`, open a project, edit one option's value without clicking Configure — confirm its name turns amber. Click Configure — confirm it returns to the normal color (a fresh `ConfigureAsync` call sets a new `OriginalValue` equal to the just-saved value).

- [ ] **Step 4: Commit**

```bash
git add src/Omen.GUI/ViewModels/BuildOptionViewModel.cs src/Omen.GUI/Views/MainWindow.axaml src/Omen.GUI/Styles/Signal.axaml
git commit -m "feat: highlight options changed since the last Configure"
```

---

## Self-review notes

- **Spec coverage:** search (Task 3), `IsAdvanced`/Advanced toggle (Tasks 1, 4), Grouped toggle (Task 4), Enum type/dropdown (Tasks 2, 5), Add Entry/Remove Entry (Task 6), changed-highlighting (Task 7) are each covered. The spec's confirmed-already-done items (path browse, distinct Generate step) have no task here, matching the spec's "Out of scope" note.
- **Placeholder scan:** Task 6 initially considered a modal Add Entry dialog (as the spec's prose suggested, "opens a small dialog") — on checking, `MainWindow.axaml` has no existing dialog/window-spawning pattern to follow, and adding one is disproportionate to this task's scope, so Task 6 uses inline fields instead, called out explicitly as a deliberate simplification (`Add Entry` defaults new options to `String` type, no type picker) rather than left as an unstated gap.
- **Type consistency:** `BuildOptionDeclaration.IsAdvanced`/`AllowedValues` (Tasks 1-2) are consumed with matching names/types in Tasks 4-6. `BuildOptionViewModel.IsAdvanced`/`IsEnum`/`AllowedValues`/`GroupName`/`OriginalValue`/`IsChanged` are each defined once (Tasks 4, 5, 7 respectively) and referenced identically in every later task's XAML.

