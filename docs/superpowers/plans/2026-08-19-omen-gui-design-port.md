# Omen GUI Design Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the Claude Design spec `Omen GUI.dc.html` onto the existing WPF Omen.GUI app — its exact palette, typography, two-row chrome, restyled panels, and a fully-functional Settings modal.

**Architecture:** Task 1 replaces the palette and typography foundation that every later task consumes, and adds the light palette plus density metrics that the Appearance settings page will switch at runtime. Tasks 2-6 rebuild the chrome (icon/assets, 30px menu bar, 36px toolbar, status bar, AvalonDock pane chrome) and are strictly sequential because they share `MainWindow.xaml`, `TopBar.xaml` and the `Themes/` dictionaries. Tasks 7-10 restyle the four panel `UserControl`s and are mutually independent. Task 11 makes the approved additive backend-seam change; Task 12 expands settings persistence; Tasks 13-17 build the Settings modal, with its four content pages (14-17) independent of each other. Task 18 is the human acceptance gate.

**Tech Stack:** C# / .NET 8 (`net8.0-windows`), WPF, Dirkster.AvalonDock 5.0.0, CommunityToolkit.Mvvm. Embedded SIL-OFL fonts (JetBrains Mono, Work Sans).

## Global Constraints

- **`Themes/Colors.xaml` is the ONLY file permitted to contain hex colour literals.** Everything else references a brush key. This survives the repalette — the token *values* change, the rule does not.
- Add no new NuGet packages. Embedded font files are content, not packages.
- **The backend seam is frozen except for the one change authorised in Task 11.** No signature changes to `CleanOrchestrator.CleanAsync`, `ProjectGenerationOrchestrator.GenerateAsync`, `OptionsOrchestrator.*`, `RuleCompiler`/`CompiledRules`, or `PlatformFactory.*`. Task 11's addition to `BuildOrchestratorRequest` must be additive, init-only, optional, and leave the CLI path behaviourally identical.
- **Preserve the existing custom `WindowChrome`** on `MainWindow` (`CaptionHeight`, `ResizeBorderThickness`, the `WM_GETMINMAXINFO` work-area clamp hook and its `handled = true`). Window controls move into the design's 30px menu bar; the chrome mechanism itself is not to be re-litigated.
- **Preserve AvalonDock.** Panels stay dockable and resizable. This plan restyles its chrome; it does not replace it with fixed panels.
- Load-bearing names that must survive every task: anchorables `ProjectAnchorable`, `OutputAnchorable`, `OptionsAnchorable`, `MatrixAnchorable`; `LeftRail`/`RightRail`; `BuildRails()` in `MainWindow.xaml.cs`; the `DockingManager` and its pane arrangement.
- Logic stays in ViewModels, not code-behind. Code-behind may wire events and open dialogs.
- Copyright header on every new `.cs` file: `// Omen Build System` / `// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.`
- Build verification: `dotnet build F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\Omen.sln -c Debug` — 0 compile errors. ~6 known benign warnings (3× NU1701 for AvalonDock, pre-existing nullable) are expected and are not yours.
- **A human may have `Omen.GUI.exe` running from this worktree.** Never kill a process you did not start. A locked-file copy failure (MSB3021/MSB3027) *after* successful compilation is not a build failure — say so and treat compilation as the evidence.
- If a stray `Omen.GUI_*_wpftmp.csproj` appears from the WPF build, delete it; never commit it.
- **No subagent can drive this GUI.** Verification is a clean build plus static reasoning. Every task reports `DONE_WITH_CONCERNS` naming precisely what it could not verify. Task 18 is the human gate.

## Design tokens (authoritative — Task 1 encodes these, later tasks reference by key)

| Key | Dark | Role |
|---|---|---|
| `Color.Bg` | `#1E1F22` | window/editor ground |
| `Color.BarRaised` | `#2F3136` | menu bar, status bar |
| `Color.Panel` | `#26282B` | toolbar, panels, rails, tab strip |
| `Color.Border` | `#3B3D42` | hairlines, dividers |
| `Color.InputBg` | `#1B1D1F` | text inputs, selects, progress track |
| `Color.PopupBg` | `#2B2D30` | menu popups |
| `Color.PopupBorder` | `#46484E` | popup borders, scrollbar thumb |
| `Color.TextPrimary` | `#D7D9DC` | body text |
| `Color.TextSecondary` | `#C7C9CC` | tree rows, settings labels |
| `Color.TextMuted` | `#9A9DA3` | panel headers, inactive rail icons |
| `Color.TextDim` | `#6E7075` | descriptions, disabled, greyed rows |
| `Color.TextDimmer` | `#5A5C62` | line numbers |
| `Color.TextMono` | `#8A8D94` | breadcrumb, tree file icons |
| `Color.Accent` | `#4C9AFF` | active tab underline, toggles, focus |
| `Color.AccentIcon` | `#8FC0FF` | active rail icon |
| `Color.PrimaryBg` | `#3D6FB8` | primary button fill |
| `Color.PrimaryHover` | `#4879C4` | primary button hover |
| `Color.SelectedBg` | `#31445E` | active rail button, selected settings row |
| `Color.Hover` | `#34363B` | toolbar button hover |
| `Color.HoverAlt` | `#3B3D42` | menu-bar item hover |
| `Color.MenuItemHover` | `#3A5F8F30` | popup menu item hover (has alpha) |
| `Color.Success` | `#57A663` | success output, Detected |
| `Color.Warning` | `#D9A441` | warning output |
| `Color.Error` | `#E0605A` | error output, Not found |
| `Color.RowDivider` | `#2F3136` | option/settings row separators |
| `Color.Separator` | `#4A4C52` | status bar `|` and `›` |

Metrics: menu bar 30 · toolbar 36 · status bar 26 · rails 34 · project panel 230 · options panel 280 · matrix panel 250 · tab strip 30 · panel header 28 · toolbar button 26 · control radius 4 · popup radius 6 · search pill radius 14.

Type: UI 12.5 / small 11.5 / tiny 10.5 · mono 12 (output), 11.5 (breadcrumb, tree files), 11 (path inputs).

---

### Task 1: Palette, typography and density foundation

**Files:**
- Rewrite: `src/Omen.GUI/Themes/Colors.xaml`
- Create: `src/Omen.GUI/Themes/Colors.Light.xaml`
- Modify: `src/Omen.GUI/Themes/Brushes.xaml`
- Create: `src/Omen.GUI/Themes/Metrics.xaml`
- Create: `src/Omen.GUI/Fonts/` (embedded font files)
- Modify: `src/Omen.GUI/Omen.GUI.csproj` (font `Resource` items)
- Modify: `src/Omen.GUI/Themes/Controls.xaml` (font family keys only)
- Modify: `src/Omen.GUI/App.xaml`

**Interfaces:**
- Produces: every `Color.*` key in the table above, in both `Colors.xaml` (dark) and `Colors.Light.xaml` (light); a `Brush.*` key per colour with the same suffix (`Brush.Bg`, `Brush.Panel`, …); `Font.Ui` and `Font.Mono` `FontFamily` keys; and density keys `Metric.RowPadding`, `Metric.PanelHeaderHeight`, `Metric.ToolbarButtonHeight`, `Metric.FontSizeBody`, `Metric.FontSizeMono` in `Metrics.xaml`.
- **Breaking rename:** the existing brush keys (`Brush.Surface`, `Brush.SurfaceRaised`, `Brush.SurfaceSelected`, `Brush.SurfaceHover`, `Brush.TextDisabled`, `Brush.AccentHover`) are replaced by the new token names. Every consuming file must be updated in this task or the build breaks. Consumers: `Controls.xaml`, `Menus.xaml`, `ScrollBars.xaml`, `Docking.xaml`, `TopBar.xaml`, `StatusBar.xaml`, `ToolWindowRail.xaml`, and the five `Views/*ToolWindow.xaml`/`WelcomePane.xaml`. Later tasks rewrite most of those anyway — this task's job is only to keep the build green with a mechanical key swap.

Mapping for the mechanical swap: `Brush.Surface`→`Brush.Bg`, `Brush.SurfaceRaised`→`Brush.Panel`, `Brush.SurfaceSelected`→`Brush.SelectedBg`, `Brush.SurfaceHover`→`Brush.Hover`, `Brush.TextDisabled`→`Brush.TextDim`, `Brush.AccentHover`→`Brush.PrimaryHover`.

- [ ] **Step 1: Fetch the fonts**

JetBrains Mono ships real static weights; Work Sans does not (verified — no `static/` directory in google/fonts and no TTFs on the fontsource CDN), so its variable font is the only option and WPF will render its default instance with synthetic bold for the 600/700 weights the design uses.

```bash
cd F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider
mkdir -p src/Omen.GUI/Fonts
curl -sfL -o src/Omen.GUI/Fonts/JetBrainsMono-Regular.ttf  "https://github.com/JetBrains/JetBrainsMono/raw/master/fonts/ttf/JetBrainsMono-Regular.ttf"
curl -sfL -o src/Omen.GUI/Fonts/JetBrainsMono-Medium.ttf   "https://github.com/JetBrains/JetBrainsMono/raw/master/fonts/ttf/JetBrainsMono-Medium.ttf"
curl -sfL -o src/Omen.GUI/Fonts/JetBrainsMono-SemiBold.ttf "https://github.com/JetBrains/JetBrainsMono/raw/master/fonts/ttf/JetBrainsMono-SemiBold.ttf"
curl -sfL -o "src/Omen.GUI/Fonts/WorkSans[wght].ttf"       "https://github.com/google/fonts/raw/main/ofl/worksans/WorkSans%5Bwght%5D.ttf"
curl -sfL -o src/Omen.GUI/Fonts/OFL-JetBrainsMono.txt      "https://github.com/JetBrains/JetBrainsMono/raw/master/OFL.txt"
curl -sfL -o src/Omen.GUI/Fonts/OFL-WorkSans.txt           "https://github.com/google/fonts/raw/main/ofl/worksans/OFL.txt"
ls -la src/Omen.GUI/Fonts/
```

All six must be non-zero. Both families are SIL Open Font License; the two `OFL-*.txt` files are the required attribution and must be committed alongside the fonts.

- [ ] **Step 2: Register the fonts as WPF resources**

In `src/Omen.GUI/Omen.GUI.csproj`, add inside a new `<ItemGroup>`:

```xml
  <ItemGroup>
    <Resource Include="Fonts\*.ttf" />
    <None Include="Fonts\OFL-*.txt" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Write `Themes/Colors.xaml` (dark)**

Replace the file entirely. This is the only file with hex literals.

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Dark palette. Values taken verbatim from the Omen GUI.dc.html design spec. -->
    <Color x:Key="Color.Bg">#FF1E1F22</Color>
    <Color x:Key="Color.BarRaised">#FF2F3136</Color>
    <Color x:Key="Color.Panel">#FF26282B</Color>
    <Color x:Key="Color.Border">#FF3B3D42</Color>
    <Color x:Key="Color.InputBg">#FF1B1D1F</Color>
    <Color x:Key="Color.PopupBg">#FF2B2D30</Color>
    <Color x:Key="Color.PopupBorder">#FF46484E</Color>

    <Color x:Key="Color.TextPrimary">#FFD7D9DC</Color>
    <Color x:Key="Color.TextSecondary">#FFC7C9CC</Color>
    <Color x:Key="Color.TextMuted">#FF9A9DA3</Color>
    <Color x:Key="Color.TextDim">#FF6E7075</Color>
    <Color x:Key="Color.TextDimmer">#FF5A5C62</Color>
    <Color x:Key="Color.TextMono">#FF8A8D94</Color>

    <Color x:Key="Color.Accent">#FF4C9AFF</Color>
    <Color x:Key="Color.AccentIcon">#FF8FC0FF</Color>
    <Color x:Key="Color.PrimaryBg">#FF3D6FB8</Color>
    <Color x:Key="Color.PrimaryHover">#FF4879C4</Color>
    <Color x:Key="Color.SelectedBg">#FF31445E</Color>
    <Color x:Key="Color.Hover">#FF34363B</Color>
    <Color x:Key="Color.HoverAlt">#FF3B3D42</Color>
    <!-- Design uses #3a5f8f30 - 8-digit CSS is #RRGGBBAA, WPF is #AARRGGBB. -->
    <Color x:Key="Color.MenuItemHover">#303A5F8F</Color>

    <Color x:Key="Color.Success">#FF57A663</Color>
    <Color x:Key="Color.Warning">#FFD9A441</Color>
    <Color x:Key="Color.Error">#FFE0605A</Color>

    <Color x:Key="Color.RowDivider">#FF2F3136</Color>
    <Color x:Key="Color.Separator">#FF4A4C52</Color>

</ResourceDictionary>
```

The `MenuItemHover` conversion is the one real trap here: the design's `#3a5f8f30` is CSS `#RRGGBBAA` (alpha last), WPF is `#AARRGGBB` (alpha first). `30` → `#30` alpha, `3a5f8f` → RGB.

- [ ] **Step 4: Write `Themes/Colors.Light.xaml`**

The design defines no light palette; this is a derived counterpart approved for later user review. It preserves each token's *role* and inverts the surface ramp while keeping accent/semantic hues (they read acceptably on light).

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- DERIVED light palette. Not part of the design spec - the design defines dark only.
         Surface ramp inverted, text ramp inverted, accent/semantic hues darkened just enough
         to hold contrast on light ground. Flagged for design review. -->
    <Color x:Key="Color.Bg">#FFFFFFFF</Color>
    <Color x:Key="Color.BarRaised">#FFF0F1F3</Color>
    <Color x:Key="Color.Panel">#FFF7F8F9</Color>
    <Color x:Key="Color.Border">#FFD8DADE</Color>
    <Color x:Key="Color.InputBg">#FFFFFFFF</Color>
    <Color x:Key="Color.PopupBg">#FFFFFFFF</Color>
    <Color x:Key="Color.PopupBorder">#FFC5C8CE</Color>

    <Color x:Key="Color.TextPrimary">#FF1E1F22</Color>
    <Color x:Key="Color.TextSecondary">#FF3B3D42</Color>
    <Color x:Key="Color.TextMuted">#FF6E7075</Color>
    <Color x:Key="Color.TextDim">#FF8A8D94</Color>
    <Color x:Key="Color.TextDimmer">#FFA0A3A9</Color>
    <Color x:Key="Color.TextMono">#FF5A5C62</Color>

    <Color x:Key="Color.Accent">#FF2A6FD6</Color>
    <Color x:Key="Color.AccentIcon">#FF1F5DB8</Color>
    <Color x:Key="Color.PrimaryBg">#FF3D6FB8</Color>
    <Color x:Key="Color.PrimaryHover">#FF34619F</Color>
    <Color x:Key="Color.SelectedBg">#FFD6E4F7</Color>
    <Color x:Key="Color.Hover">#FFE8EAED</Color>
    <Color x:Key="Color.HoverAlt">#FFDDE0E4</Color>
    <Color x:Key="Color.MenuItemHover">#302A6FD6</Color>

    <Color x:Key="Color.Success">#FF2E7D3A</Color>
    <Color x:Key="Color.Warning">#FF9A6B0F</Color>
    <Color x:Key="Color.Error">#FFC0392F</Color>

    <Color x:Key="Color.RowDivider">#FFE8EAED</Color>
    <Color x:Key="Color.Separator">#FFC5C8CE</Color>

</ResourceDictionary>
```

- [ ] **Step 5: Rewrite `Themes/Brushes.xaml`**

One `SolidColorBrush` per colour key, same suffix. Task 17 swaps palettes at runtime by replacing the merged `Colors*.xaml` dictionary, so these brushes must use `DynamicResource` on their `Color` — a `StaticResource` would bake the dark value in permanently and the theme switch would silently do nothing.

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Colors.xaml" />
    </ResourceDictionary.MergedDictionaries>

    <SolidColorBrush x:Key="Brush.Bg"            Color="{DynamicResource Color.Bg}" />
    <SolidColorBrush x:Key="Brush.BarRaised"     Color="{DynamicResource Color.BarRaised}" />
    <SolidColorBrush x:Key="Brush.Panel"         Color="{DynamicResource Color.Panel}" />
    <SolidColorBrush x:Key="Brush.Border"        Color="{DynamicResource Color.Border}" />
    <SolidColorBrush x:Key="Brush.InputBg"       Color="{DynamicResource Color.InputBg}" />
    <SolidColorBrush x:Key="Brush.PopupBg"       Color="{DynamicResource Color.PopupBg}" />
    <SolidColorBrush x:Key="Brush.PopupBorder"   Color="{DynamicResource Color.PopupBorder}" />
    <SolidColorBrush x:Key="Brush.TextPrimary"   Color="{DynamicResource Color.TextPrimary}" />
    <SolidColorBrush x:Key="Brush.TextSecondary" Color="{DynamicResource Color.TextSecondary}" />
    <SolidColorBrush x:Key="Brush.TextMuted"     Color="{DynamicResource Color.TextMuted}" />
    <SolidColorBrush x:Key="Brush.TextDim"       Color="{DynamicResource Color.TextDim}" />
    <SolidColorBrush x:Key="Brush.TextDimmer"    Color="{DynamicResource Color.TextDimmer}" />
    <SolidColorBrush x:Key="Brush.TextMono"      Color="{DynamicResource Color.TextMono}" />
    <SolidColorBrush x:Key="Brush.Accent"        Color="{DynamicResource Color.Accent}" />
    <SolidColorBrush x:Key="Brush.AccentIcon"    Color="{DynamicResource Color.AccentIcon}" />
    <SolidColorBrush x:Key="Brush.PrimaryBg"     Color="{DynamicResource Color.PrimaryBg}" />
    <SolidColorBrush x:Key="Brush.PrimaryHover"  Color="{DynamicResource Color.PrimaryHover}" />
    <SolidColorBrush x:Key="Brush.SelectedBg"    Color="{DynamicResource Color.SelectedBg}" />
    <SolidColorBrush x:Key="Brush.Hover"         Color="{DynamicResource Color.Hover}" />
    <SolidColorBrush x:Key="Brush.HoverAlt"      Color="{DynamicResource Color.HoverAlt}" />
    <SolidColorBrush x:Key="Brush.MenuItemHover" Color="{DynamicResource Color.MenuItemHover}" />
    <SolidColorBrush x:Key="Brush.Success"       Color="{DynamicResource Color.Success}" />
    <SolidColorBrush x:Key="Brush.Warning"       Color="{DynamicResource Color.Warning}" />
    <SolidColorBrush x:Key="Brush.Error"         Color="{DynamicResource Color.Error}" />
    <SolidColorBrush x:Key="Brush.RowDivider"    Color="{DynamicResource Color.RowDivider}" />
    <SolidColorBrush x:Key="Brush.Separator"     Color="{DynamicResource Color.Separator}" />

</ResourceDictionary>
```

- [ ] **Step 6: Create `Themes/Metrics.xaml`**

Standard density. Task 17 swaps this dictionary for Compact/Roomy variants, so consumers must reference these with `DynamicResource`.

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">

    <!-- Standard density. Compact/Roomy variants live in Metrics.Compact.xaml / Metrics.Roomy.xaml. -->
    <Thickness x:Key="Metric.RowPadding">10,6,10,6</Thickness>
    <sys:Double x:Key="Metric.PanelHeaderHeight">28</sys:Double>
    <sys:Double x:Key="Metric.ToolbarButtonHeight">26</sys:Double>
    <sys:Double x:Key="Metric.FontSizeBody">12.5</sys:Double>
    <sys:Double x:Key="Metric.FontSizeMono">12</sys:Double>

</ResourceDictionary>
```

- [ ] **Step 7: Point the font keys at the embedded families**

In `Themes/Controls.xaml`, replace the two existing `FontFamily` declarations:

```xml
    <FontFamily x:Key="Font.Ui">pack://application:,,,/Fonts/#Work Sans, Segoe UI Variable Text, Segoe UI</FontFamily>
    <FontFamily x:Key="Font.Mono">pack://application:,,,/Fonts/#JetBrains Mono, Cascadia Code, Consolas</FontFamily>
```

The fallback stack after the `#Family Name` matters: if the embedded font fails to resolve, WPF walks to the next name rather than silently rendering a default. Verify in Step 9 that Work Sans actually resolves.

- [ ] **Step 8: Merge the new dictionaries and swap the old brush keys**

In `App.xaml`, merge `Themes/Metrics.xaml` before `Themes/Controls.xaml` (Controls consumes metrics).

Then apply the mechanical key rename across all consuming files. From the worktree root:

```bash
cd F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider/src/Omen.GUI
grep -rln "Brush\.Surface\|Brush\.TextDisabled\|Brush\.AccentHover" --include=*.xaml --include=*.cs .
```

For each file, apply: `Brush.SurfaceRaised`→`Brush.Panel`, `Brush.SurfaceSelected`→`Brush.SelectedBg`, `Brush.SurfaceHover`→`Brush.Hover`, `Brush.Surface`→`Brush.Bg`, `Brush.TextDisabled`→`Brush.TextDim`, `Brush.AccentHover`→`Brush.PrimaryHover`.

Order matters: replace `Brush.SurfaceRaised`, `Brush.SurfaceSelected` and `Brush.SurfaceHover` **before** `Brush.Surface`, or the shorter key will corrupt the longer ones.

Note `Converters/OutputLevelToBrushConverter.cs` and `Converters/BuildStateToBrushConverter.cs` look up brush keys by string — check those too.

- [ ] **Step 9: Build and verify the fonts resolve**

Run: `dotnet build F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\Omen.sln -c Debug`
Expected: 0 errors.

Then launch the app and confirm it starts without a XAML parse exception. **State explicitly in your report whether you could confirm Work Sans actually rendered** — if you cannot see the window, say so; the human's acceptance pass checks it. If a `pack://` font URI throws at load, the likely cause is a missing `<Resource Include>` or a wrong `#Family Name` (the name after `#` is the font's *internal* family name, not the filename).

- [ ] **Step 10: Commit**

```bash
git add src/Omen.GUI/Themes src/Omen.GUI/Fonts src/Omen.GUI/App.xaml src/Omen.GUI/Omen.GUI.csproj src/Omen.GUI/Controls src/Omen.GUI/Views src/Omen.GUI/Converters
git commit -m "feat(gui): adopt the design's palette, embedded OFL fonts, light palette and density metrics"
```

---

### Task 2: Application icon and design assets

**Files:**
- Create: `src/Omen.GUI/Assets/omen-icon-{16,20,24,32,40,48,64,96,128,256}.png`, `src/Omen.GUI/Assets/omen.ico`
- Modify: `src/Omen.GUI/Omen.GUI.csproj`

**Interfaces:**
- Produces: `ApplicationIcon` set on the executable; a `Resource`-registered `Assets/omen-icon-64.png` that Task 3's menu bar displays at 18×18.

The repo currently has no application icon at all. The design project ships a full set.

- [ ] **Step 1: Fetch the icon set from the design project**

Use the `DesignSync` MCP tool, `method: "get_file"`, `projectId: "f0801e93-7bf2-4d4b-a811-d624338d8165"`, once per path below. Each returns base64 (`isBase64: true`); decode and write to `src/Omen.GUI/Assets/` under the same basename.

`assets/icons/omen.ico`, and `assets/icons/omen-icon-{16,20,24,32,40,48,64,96,128,256}.png`.

Skip the 512 and the banner images — nothing in this plan displays them and they are large.

- [ ] **Step 2: Register them in the csproj**

```xml
  <PropertyGroup>
    <ApplicationIcon>Assets\omen.ico</ApplicationIcon>
  </PropertyGroup>

  <ItemGroup>
    <Resource Include="Assets\*.png" />
  </ItemGroup>
```

- [ ] **Step 3: Build and commit**

Run: `dotnet build F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\Omen.sln -c Debug`
Expected: 0 errors. Confirm `Omen.GUI.exe` in `bin/Debug/net8.0-windows/` now carries the icon (check via `ls -la` that the exe rebuilt; you cannot visually confirm the icon).

```bash
git add src/Omen.GUI/Assets src/Omen.GUI/Omen.GUI.csproj
git commit -m "feat(gui): add the Omen application icon and design icon assets"
```

---

### Task 3: Menu bar (30px) with inline menus and window controls

**Files:**
- Create: `src/Omen.GUI/Controls/MenuBar.xaml` + `.xaml.cs`
- Modify: `src/Omen.GUI/Themes/Menus.xaml`
- Modify: `src/Omen.GUI/Views/MainWindow.xaml`
- Modify: `src/Omen.GUI/Views/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.{LoadProject, CloseProject, BuildAsync, RebuildAsync, CleanAsync, CancelBuild, GenerateProjectFilesAsync, IsBuilding, IsCancellable}`; `Brush.*` and `Font.Ui` from Task 1; `Assets/omen-icon-64.png` from Task 2.
- Produces: `MenuBar` UserControl, 30px tall, occupying `MainWindow`'s row 0. Window minimise/restore/close move here from `TopBar`.

**This task and Tasks 4-6 all edit `MainWindow.xaml` and must run in order.**

The existing `TopBar.xaml` is a single 40px bar carrying a burger `ContextMenu`, the project chip, platform/config combos, the action cluster and the window controls. This task takes the *menu* and *window control* halves; Task 4 takes the rest and rebuilds it as the 36px toolbar. `TopBar.xaml` is deleted at the end of Task 4.

- [ ] **Step 1: Restyle `Menu` and `MenuItem` for a real menu bar**

`Themes/Menus.xaml` currently styles `ContextMenu` and the submenu-role `MenuItem` only (an earlier task confirmed no `<Menu>` bar existed, so the top-level roles were deliberately skipped). A `<Menu>` now exists, so add:

- `<Style TargetType="Menu">`: `Background` = `Brush.BarRaised`, `BorderThickness` 0, `Padding` 0.
- A `MenuItem` template for **`Role=TopLevelHeader`** — the File/Build/Project/Tools/Help buttons: padding `10,5`, `CornerRadius` 4, `Foreground` `Brush.TextPrimary`, hover background `Brush.HoverAlt`, and its `Popup` styled as: `Background` `Brush.PopupBg`, 1px `Brush.PopupBorder`, `CornerRadius` 6, `MinWidth` 210, `Padding` 4, plus a drop shadow (`BlurRadius` 24, `ShadowDepth` 8, `Opacity` 0.45, `Color` Black).
- A `MenuItem` template for **`Role=SubmenuItem`**: padding `10,6`, `CornerRadius` 4, `FontSize` 12, hover background `Brush.MenuItemHover`.

Keep the existing `{x:Static MenuItem.SeparatorStyleKey}` separator style; retarget its brush to `Brush.Border` and its margin to `6,4`.

`Role` is a `MenuItemRole` enum on `MenuItem`; select on it with a `Style.Triggers` `Trigger` on the `Role` property, or supply the four role-specific templates via `ItemContainerStyle`. Whichever you choose, **all four roles must be covered** — an unstyled role renders stock Aero.

- [ ] **Step 2: Build `MenuBar.xaml`**

Structure, left to right in a `DockPanel`:

1. Brand block: `<Image Source="pack://application:,,,/Assets/omen-icon-64.png" Width="18" Height="18"/>` + `TextBlock` "Omen", `FontWeight` SemiBold, `FontSize` 12, right margin 10.
2. `<Menu>` with five `MenuItem` headers and these exact children:
   - **File**: `Open Project...`, `Close Project`, `<Separator/>`, `Exit`
   - **Build**: `Build`, `Rebuild`, `Clean`, `<Separator/>`, `Cancel`
   - **Project**: `Generate Visual Studio 2022`, `Generate Visual Studio 2019`, `<Separator/>`, `Generate Visual Studio Code`, `Generate CMake`
   - **Tools**: `OmenNet Coordinator...`, `OmenNet Agent...`, `<Separator/>`, `Clear Rule Cache`
   - **Help**: `Documentation`, `About Omen`
3. A spacer.
4. Window controls docked right: minimise, maximise/restore, close — 46×30 each, flat, no rounding, glyph `Brush.TextMuted`, hover background `Brush.Hover` with glyph `Brush.TextPrimary`; close hover background `Brush.Error` with a white glyph. **Each needs `WindowChrome.IsHitTestVisibleInChrome="True"`**, as does the `<Menu>` and the brand block — anything clickable inside the caption height is otherwise swallowed by the drag region.

Gate `Build`/`Rebuild`/`Clean` on `IsBuilding` being false via `Conv.InverseBool`, and `Cancel` on `IsCancellable` — matching the existing behaviour that an earlier task established (Cancel is deliberately *not* offered for Clean/Generate, which take no `CancellationToken`).

The four **Tools** and **Help** items are new and have no backing implementation. Wire each to a handler that writes an explicit "not implemented" line to the output pane via the ViewModel rather than silently doing nothing — the plan's Global Constraints forbid silent no-ops, and an inert menu item is exactly that. Name them precisely, e.g. `OmenNet Coordinator is not wired up yet.`

- [ ] **Step 3: Move the window controls out of `TopBar`**

Delete the three window-control buttons and their handlers from `TopBar.xaml`/`.xaml.cs`. Move `OnMinimizeClick`/`OnMaximizeRestoreClick`/`OnCloseClick` and the maximise-glyph state logic into `MenuBar.xaml.cs` unchanged.

`MainWindow.xaml.cs` currently updates the maximise glyph on window state change (added when the chrome was built). Repoint that at `MenuBar`; do not change the `WM_GETMINMAXINFO` hook, the `HwndSource` wiring, or `BuildRails()`.

- [ ] **Step 4: Restructure `MainWindow.xaml`'s rows**

The grid currently has 3 rows (40 / * / 28). Change to 4: `30` (menu bar), `36` (toolbar — Task 4 fills it), `*` (body), `26` (status bar). Put `<controls:MenuBar Grid.Row="0"/>` in row 0 and leave row 1 as an empty `<ContentControl x:Name="ToolbarSlot" Grid.Row="1"/>` for Task 4. Move the existing `TopBar` reference out of row 0 — Task 4 deletes it.

Update `WindowChrome.CaptionHeight` from 40 to **30** to match the new menu bar.

Every anchorable, rail and the `DockingManager` stay exactly as they are.

- [ ] **Step 5: Build, verify, commit**

Run: `dotnet build F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\Omen.sln -c Debug` → 0 errors, then launch to confirm no XAML parse exception.

Report explicitly that you could not verify: menus opening, window-control clicks, or window dragging.

```bash
git add src/Omen.GUI/Controls src/Omen.GUI/Themes/Menus.xaml src/Omen.GUI/Views/MainWindow.xaml src/Omen.GUI/Views/MainWindow.xaml.cs
git commit -m "feat(gui): add the design's 30px menu bar with inline menus and window controls"
```

---

### Task 4: Toolbar (36px)

**Files:**
- Create: `src/Omen.GUI/Controls/Toolbar.xaml` + `.xaml.cs`
- Delete: `src/Omen.GUI/Controls/TopBar.xaml` + `.xaml.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.xaml`
- Modify: `src/Omen.GUI/Themes/Controls.xaml`
- Modify: `src/Omen.GUI/Themes/Icons.xaml`

**Interfaces:**
- Consumes: `MainWindowViewModel.{AvailablePlatforms, SelectedPlatform, Configurations, SelectedConfiguration, IsBuilding, IsCancellable, LoadProject, BuildAsync, RebuildAsync, CleanAsync, CancelBuild}`; `MatrixPanelViewModel.IsActive`.
- Produces: `Toolbar` UserControl in `MainWindow`'s row 1; new style keys `Style.Button.Toolbar` and `Style.Button.ToolbarPrimary`; a `QuickSearch` string property on `MainWindowViewModel`.

**Sequential with Tasks 3, 5, 6 — shares `MainWindow.xaml`.**

- [ ] **Step 1: Add the toolbar icon geometries**

Add to `Themes/Icons.xaml`, converted from the design's SVG paths (they are already valid WPF geometry mini-language):

- `Icon.Folder`: `M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z`
- `Icon.Lightning`: `M14.5 3.5 6 12l3 3-8 4 4-8 3 3 8.5-8.5z`
- `Icon.Refresh`: `M3 12a9 9 0 1 0 2.6-6.3M3 4v5h5`
- `Icon.Trash`: `M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3`
- `Icon.Stop`: `M5 5h14v14H5z`
- `Icon.Search`: `M18 18l-4.3-4.3M4 11a7 7 0 1 0 14 0 7 7 0 0 0-14 0z`

These are *stroke* paths in the design (`fill="none" stroke="currentColor" stroke-width="1.75"`). In WPF, render them with `<Path Data="..." Stroke="..." StrokeThickness="1.75" Fill="{x:Null}" Stretch="Uniform" Width="14" Height="14"/>` — using `Fill` instead of `Stroke` on an open path produces a filled blob, which is the most common way to get these visibly wrong.

- [ ] **Step 2: Add the two toolbar button styles**

In `Themes/Controls.xaml`:

- `Style.Button.Toolbar`: `Height` 26, `Padding` `8,0`, `CornerRadius` 4, transparent background and border, `Foreground` `Brush.TextPrimary`, `FontSize` 12, hover background `Brush.Hover`; disabled `Foreground` `Brush.TextDim` with `Cursor` `No`.
- `Style.Button.ToolbarPrimary`: same metrics but `Padding` `10,0`, background `Brush.PrimaryBg`, border `Brush.Accent`, `Foreground` White, `FontWeight` SemiBold, hover background `Brush.PrimaryHover`.

- [ ] **Step 3: Build `Toolbar.xaml`**

Left to right in a `DockPanel`, 36px tall, background `Brush.Panel`, 1px bottom border `Brush.Border`, 8px horizontal padding, 4px gap:

1. **Open** — `Style.Button.Toolbar`, `Icon.Folder` + "Open", opens the folder picker via `Microsoft.Win32.OpenFolderDialog` and calls `LoadProject`.
2. 1px × 18px divider, `Brush.Border`, 4px margin.
3. **Build** — `Style.Button.ToolbarPrimary`, `Icon.Lightning`.
4. **Rebuild** — `Icon.Refresh`. **Clean** — `Icon.Trash`. Both gated on `IsBuilding` false.
5. **Cancel** — `Icon.Stop`, `IsEnabled` bound to `IsCancellable`.
6. Divider.
7. "Platform" label (`Brush.TextDim`, 11px) + `ComboBox` bound to `AvailablePlatforms`/`SelectedPlatform`; "Config" label + `ComboBox` bound to `Configurations`/`SelectedConfiguration`. Both combos hide when `MatrixPanel.IsActive` (via `Conv.BoolToVisibility` with `ConverterParameter=Invert`), preserving existing behaviour.
8. Flexible spacer.
9. **Search pill** — `Border` 220×26, `CornerRadius` 14, `Background` `Brush.InputBg`, 1px `Brush.Border`; inside: `Icon.Search` 13px, a `PlaceholderTextBox` (`Placeholder="Search everywhere"`, transparent, no border) bound two-way to `QuickSearch`, and a trailing `Border` badge with `⇧⇧` (10px, `Brush.TextDimmer`, 1px `Brush.Border`, `CornerRadius` 3, padding `4,1`).
10. **Settings gear** — 26×26, `Icon.Settings` (already exists in `Icons.xaml`, reserved for exactly this). Click handler calls `MainWindowViewModel.OpenSettings()` — **that method does not exist yet; Task 13 adds it.** For this task, wire the handler to a no-op that writes `Settings is not wired up yet.` to the output pane, and leave a `// Task 13 replaces this` comment. Do not invent a partial modal here.

The search box is presentational for now — nothing consumes `QuickSearch`. Add the property to `MainWindowViewModel` so the binding resolves, and note in your report that it is deliberately inert.

- [ ] **Step 4: Swap it in and delete `TopBar`**

Replace `MainWindow.xaml`'s `ToolbarSlot` with `<controls:Toolbar Grid.Row="1"/>`. Delete `TopBar.xaml` and `TopBar.xaml.cs` (`git rm`) and remove any remaining reference.

- [ ] **Step 5: Build, verify, commit**

Run the build → 0 errors, launch → no XAML exception. Report what you could not verify (button clicks, the folder picker, combo population, pill layout).

```bash
git add -A src/Omen.GUI
git commit -m "feat(gui): add the design's 36px toolbar and retire the unified TopBar"
```

---

### Task 5: Status bar

**Files:**
- Modify: `src/Omen.GUI/Controls/StatusBar.xaml`
- Modify: `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.{CurrentBuildState, StatusText, ProjectName, SelectedPlatform, SelectedConfiguration, IsBuilding, ProgressValue}`; `Conv.BuildStateToBrush`, `Conv.BoolToVisibility`.
- Produces: `MainWindowViewModel.ProgressLabel` (a `string`, e.g. `"64%"`).

**Sequential with Tasks 3, 4, 6 — shares `MainWindow.xaml`'s row assignment (row 3 now).**

- [ ] **Step 1: Add `ProgressLabel`**

In `MainWindowViewModel`, next to `ProgressValue`:

```csharp
    /// <summary>Whole-percent label shown at the right of the status bar, e.g. "64%".</summary>
    public string ProgressLabel => $"{(int)Math.Round(ProgressValue)}%";
```

Add `[NotifyPropertyChangedFor(nameof(ProgressLabel))]` to the `_progressValue` field.

- [ ] **Step 2: Rebuild the status bar**

26px, background `Brush.BarRaised`, 1px top border `Brush.Border`, 10px horizontal padding, 10px gap, `FontSize` 11.5, `Foreground` `Brush.TextMuted`. Left to right:

1. 7px `Ellipse`, `Fill` bound to `CurrentBuildState` through `Conv.BuildStateToBrush`.
2. `StatusText` in `Brush.TextPrimary`.
3. `|` in `Brush.Separator`.
4. Breadcrumb — three `TextBlock`s in `Font.Mono`, `Brush.TextMono`, bound to `ProjectName`, `SelectedPlatform`, `SelectedConfiguration`, separated by `›` in `Brush.Separator`.
5. Flexible spacer.
6. Progress track: `Border` 160×5, `CornerRadius` 3, `Background` `Brush.InputBg`, containing a `ProgressBar` (or a clipped `Rectangle`) with `Foreground` `Brush.Accent`, bound to `ProgressValue`, visible only when `IsBuilding`.
7. `ProgressLabel` in `Font.Mono`, `Brush.TextMono`, `Width` 32.

Set `Grid.Row="3"` on the `StatusBar` instance in `MainWindow.xaml` (it was row 2 before Task 3 added a row).

- [ ] **Step 3: Build and commit**

```bash
git add src/Omen.GUI/Controls/StatusBar.xaml src/Omen.GUI/ViewModels/MainWindowViewModel.cs src/Omen.GUI/Views/MainWindow.xaml
git commit -m "feat(gui): restyle the status bar with the design's breadcrumb and progress readout"
```

---

### Task 6: AvalonDock pane chrome

**Files:**
- Modify: `src/Omen.GUI/Themes/Docking.xaml`

**Interfaces:**
- Consumes: Task 1's tokens.
- Produces: nothing new — this is pure restyling of existing keyed styles (`Style.AnchorablePaneControl`, `Style.DocumentPaneControl`, the `AnchorablePaneTitle` style, and the tab-item `ItemContainerStyle`s).

**Sequential with Tasks 3-5 (shares Themes files with Task 4).** Tasks 7-10 depend on this landing first.

Two things about this file learned the hard way in earlier work, both still true:

1. `DockingManager` assigns `AnchorablePaneControlStyle`/`DocumentPaneControlStyle` as **local values**, which beat implicit `TargetType` styles. The current file therefore uses `x:Key`'d styles assigned directly on the `DockingManager` element in `MainWindow.xaml`. Keep that structure — reverting to implicit styles silently does nothing.
2. The auto-hide pin was deliberately removed from the title template (the rails own show/hide). Do not reinstate it.

- [ ] **Step 1: Repoint every brush and adjust metrics**

- Pane backgrounds → `Brush.Bg` for the document pane, `Brush.Panel` for anchorable panes.
- Pane borders → 1px `Brush.Border`.
- **Pane title** (`AnchorablePaneTitle`): height 28, background `Brush.Panel`, bottom border 1px `Brush.Border`; title text `FontSize` 11, `FontWeight` SemiBold, `Foreground` `Brush.TextMuted`, and **uppercase** — WPF has no `text-transform`, so bind the title through a converter or set the text uppercase at source. Add a small `Converters/UpperCaseConverter.cs` (`IValueConverter` returning `value?.ToString()?.ToUpperInvariant()`) and register it as `Conv.UpperCase` in `Controls.xaml`.
- Title-bar close glyph → `✕`, `Brush.TextMuted`, hover `Brush.TextPrimary`.
- **Tab items** (both anchorable and document): background transparent, inactive foreground `Brush.TextMono`; active foreground `Brush.TextPrimary` with a 2px bottom border in `Brush.Accent` and no background change. Height 30, padding `14,0`, `FontSize` 12.
- Splitter (`LayoutGridResizerControl`) → `Brush.Border`.
- Floating window chrome (`LayoutFloatingWindowControl`) → background `Brush.Panel`, 1px `Brush.PopupBorder`. A prior review flagged floating windows as entirely unthemed; give them at least these two so a torn-off pane is not stock white.

- [ ] **Step 2: Build and commit**

```bash
git add src/Omen.GUI/Themes/Docking.xaml src/Omen.GUI/Converters src/Omen.GUI/Themes/Controls.xaml
git commit -m "feat(gui): restyle AvalonDock pane chrome to the design"
```

---

### Task 7: Project panel

**Files:**
- Modify: `src/Omen.GUI/Views/ProjectToolWindow.xaml`

**Interfaces:**
- Consumes: `MainWindowViewModel.ProjectTreeRoots` (`ObservableCollection<ProjectTreeNode>`); `ProjectTreeNode.{Name, FullPath, IsDirectory, Children, IsExpanded}`.

**Independent of Tasks 8-10.** All four require Task 6.

Do not change the lazy-loading behaviour — `ProjectTreeNode` populates children on expand via the `TreeViewItem.IsExpanded` two-way binding, and that mechanism is load-bearing.

- [ ] **Step 1: Restyle the tree**

Background `Brush.Panel`. `HierarchicalDataTemplate` per row: a 13px folder or file `Path` icon plus the name, 6px gap, 3px vertical padding.

Colour by role — express these as `DataTrigger`s on the node, not hardcoded per row:
- Directory nodes: `Brush.TextSecondary` text, `Brush.TextMono` icon.
- Files: `Font.Mono`, `FontSize` 11.5, `Brush.TextMuted` text, `Brush.TextDim` icon.
- Nodes named `Binaries` or `Intermediate`: `Brush.TextDim` text and icon (the design greys these).
- Root node: `FontWeight` SemiBold, `Brush.TextPrimary`, icon in `Brush.Accent`.

The design's per-depth indents (10/26/42/58px) come free from `TreeView`'s own indentation; do not hand-roll padding per level.

- [ ] **Step 2: Build and commit**

```bash
git add src/Omen.GUI/Views/ProjectToolWindow.xaml
git commit -m "feat(gui): restyle the project panel to the design"
```

---

### Task 8: Build output with tab strip and line numbers

**Files:**
- Modify: `src/Omen.GUI/Views/BuildOutputToolWindow.xaml` + `.xaml.cs`
- Modify: `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.OutputLines` (`ObservableCollection<OutputLine>`), `OutputLine.{Text, Level}`, `Conv.OutputLevelToBrush`.
- Produces: `MainWindowViewModel.{ActiveOutputTab (string), ProblemLines (IEnumerable<OutputLine>), ProblemCount (int), VisibleOutputLines (IEnumerable<OutputLine>)}`.

**Independent of Tasks 7, 9, 10.**

The existing auto-scroll is deliberate and fragile: it dispatches `ScrollIntoView` at `DispatcherPriority.Background` with a `_scrollPending` flag, precisely to avoid an `ItemsControl` reentrancy crash, and it re-attaches on `Loaded` so it survives the rails hiding the pane. **Preserve all of that** — bind the list to `VisibleOutputLines` instead of `OutputLines`, and leave the subscription, guards and dispatch mechanism untouched.

- [ ] **Step 1: Add the tab state**

In `MainWindowViewModel`:

```csharp
    /// <summary>Which output tab is showing: "output" or "problems".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleOutputLines))]
    private string _activeOutputTab = "output";

    /// <summary>Warnings and errors only - the Problems tab's contents.</summary>
    public IEnumerable<OutputLine> ProblemLines =>
        OutputLines.Where(l => l.Level is OrchestratorEventLevel.Warning or OrchestratorEventLevel.Error);

    public int ProblemCount => ProblemLines.Count();

    public IEnumerable<OutputLine> VisibleOutputLines =>
        ActiveOutputTab == "problems" ? ProblemLines : OutputLines;
```

`OutputLines` is an `ObservableCollection`, so these computed views do not refresh themselves. In the same place `AppendLine` adds a line, raise `OnPropertyChanged(nameof(VisibleOutputLines))`, `nameof(ProblemLines)` and `nameof(ProblemCount)`; do the same wherever `OutputLines.Clear()` happens (`CloseProject`, and the start of each build).

- [ ] **Step 2: Build the view**

A `DockPanel`: 30px tab strip docked top, output list filling.

Tab strip — background `Brush.Panel`, 1px bottom border `Brush.Border`, two clickable items:
- "Build Output" — sets `ActiveOutputTab = "output"`.
- "Problems" — sets `"problems"`, and carries a count badge: `Border` `CornerRadius` 8, `Background` `Brush.Border`, `Foreground` `Brush.TextMuted`, `FontSize` 10.5, padding `6,0`, bound to `ProblemCount`, collapsed when the count is 0.

Active tab: `Brush.TextPrimary` with a 2px bottom border `Brush.Accent`. Inactive: `Brush.TextMono`, transparent border. Padding `14,0`, `FontSize` 12.

Output list — keep the virtualizing `ListBox`, `Font.Mono`, `FontSize` 12, `LineHeight` 20 (1.7 × 12 ≈ 20). Each row is a two-column `Grid`: a 34px right-aligned line number (`Brush.TextDimmer`, 12px right margin, `IsHitTestVisible="False"`) plus the text, `TextWrapping` `NoWrap`, `Foreground` via `Conv.OutputLevelToBrush`.

For the line number, use `AlternationCount` on the `ListBox` with `(ItemsControl.AlternationIndex)` bound via `RelativeSource` to the `ListBoxItem` — `AlternationCount` must exceed the largest expected line count or the index wraps; set it to `int.MaxValue`. Note in your report that this is the mechanism, since a wrapped index would be a subtle wrong-numbers bug.

- [ ] **Step 3: Build and commit**

```bash
git add src/Omen.GUI/Views/BuildOutputToolWindow.xaml src/Omen.GUI/Views/BuildOutputToolWindow.xaml.cs src/Omen.GUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat(gui): add the Build Output/Problems tab strip and line numbers"
```

---

### Task 9: Options panel with toggle switches

**Files:**
- Modify: `src/Omen.GUI/Views/OptionsToolWindow.xaml`
- Modify: `src/Omen.GUI/Themes/Controls.xaml`

**Interfaces:**
- Consumes: `OptionsPanelViewModel.{FilteredOptions, GroupedOptions, SearchText, ShowAdvanced, GroupByPrefix, StatusText, NewEntry*, AvailableOptionTypes, AddCurrentEntryAsync, RemoveEntryAsync}`; `BuildOptionViewModel.{Name, Description, Value, IsChanged, IsManual, IsBool, IsString, IsInt, IsPath, IsEnum, IsChecked, NumericValue, AllowedValues}`.
- Produces: a `Style.ToggleSwitch` keyed style for `CheckBox`.

**Independent of Tasks 7, 8, 10.**

Preserve, do not regress: the single shared `OptionEditor` `DataTemplate` used by both the flat and grouped lists; the flat list's virtualization; the `IsBuilding` gating on Configure/Add/Remove; and the fill-slot `Grid` that wraps both lists as the `DockPanel`'s last child.

- [ ] **Step 1: Add the toggle switch style**

A `CheckBox` retemplated as the design's switch: 30×16 track, `CornerRadius` 8, `Background` `Brush.PopupBorder` unchecked / `Brush.Accent` checked; a 12px white `Ellipse` knob, `Margin` `2,0,0,0` unchecked and `16,0,0,0` checked, with a 120ms `ThicknessAnimation` on the `IsChecked` triggers. Register as `Style.ToggleSwitch`.

- [ ] **Step 2: Restyle the panel**

Background `Brush.Panel`.

Top block (8px padding, 1px bottom border `Brush.Border`): the search `PlaceholderTextBox` (24px, `Brush.InputBg`, 1px `Brush.Border`, `CornerRadius` 4, `Placeholder="Search options..."`), then a row with the "Advanced" and "Grouped" `CheckBox`es (11.5px, `Brush.TextMuted`).

Group headers: 10.5px, `FontWeight` Bold, `Brush.TextDim`, uppercased via `Conv.UpperCase`, padding `10,8,10,2`.

`OptionEditor` template — padding `10,6`, 1px bottom border `Brush.RowDivider`:
- Header row: name (12px SemiBold, `Brush.TextPrimary`, or `Brush.Warning` when `IsChanged`) and, for bools, the `Style.ToggleSwitch` `CheckBox` bound to `IsChecked`, right-aligned.
- Description: 11px, `Brush.TextDim`, `TextWrapping` Wrap, top margin 2.
- Enum: full-width 24px `ComboBox` bound to `AllowedValues`/`Value`.
- Int: the existing `NumericTextBox`.
- String: `TextBox`.
- Path: a `Grid` with a readonly `TextBox` (`Font.Mono`, 11px, `Brush.TextMuted`) plus a 24×24 `…` button.
- Remove button, visible only when `IsManual`.

- [ ] **Step 3: Build and commit**

```bash
git add src/Omen.GUI/Views/OptionsToolWindow.xaml src/Omen.GUI/Themes/Controls.xaml
git commit -m "feat(gui): restyle the options panel with the design's toggle switches"
```

---

### Task 10: Build Matrix panel

**Files:**
- Modify: `src/Omen.GUI/Views/MatrixToolWindow.xaml`

**Interfaces:**
- Consumes: `MatrixPanelViewModel.DimensionGroups`; `DimensionGroupViewModel.{DisplayName, Values}`; `DimensionValueViewModel.{DisplayName, IsChecked}`.

**Independent of Tasks 7-9.**

- [ ] **Step 1: Restyle**

Background `Brush.Panel`. Group name: 11.5px, `FontWeight` Bold, `Brush.TextSecondary`, padding `10,8,10,4`. Value rows: `CheckBox`, 12px, `Brush.TextSecondary`, padding `20,3,10,3`, accent-coloured via the existing themed `CheckBox` style.

- [ ] **Step 2: Build and commit**

```bash
git add src/Omen.GUI/Views/MatrixToolWindow.xaml
git commit -m "feat(gui): restyle the build matrix panel to the design"
```

---

### Task 11: Backend seam — build override pass-through

**Files:**
- Modify: `src/Omen.Executors/Orchestration/BuildOrchestrator.cs`

**Interfaces:**
- Produces: four optional init-only properties on `BuildOrchestratorRequest`: `UseUnityBuild` (`bool?`), `UsePrecompiledHeaders` (`bool?`), `EnableLinkTimeOptimization` (`bool?`), `CoordinatorAddress` (`string?`). Task 12's settings and Task 16's Distributed page feed these.

This is the **one** authorised change to the frozen seam. It must be additive and non-breaking: every property optional, every existing call site (notably the CLI's `BuildCommand`) behaviourally identical when they are unset.

- [ ] **Step 1: Extend the request**

Add to `BuildOrchestratorRequest`, after `Jobs`:

```csharp
    /// <summary>GUI override for unity builds. Null leaves the target's own rules in charge.</summary>
    public bool? UseUnityBuild { get; init; }

    /// <summary>GUI override for precompiled headers. Null leaves the target's own rules in charge.</summary>
    public bool? UsePrecompiledHeaders { get; init; }

    /// <summary>GUI override for link-time optimization. Null leaves the target's own rules in charge.</summary>
    public bool? EnableLinkTimeOptimization { get; init; }

    /// <summary>OmenNet coordinator address. Null disables distributed builds for this request.</summary>
    public string? CoordinatorAddress { get; init; }
```

- [ ] **Step 2: Honour them when building the context**

In `BuildAsync`, the `new BuildContext { ... }` initializer currently sets `ParallelJobs = request.Jobs ?? Environment.ProcessorCount`. Add, in the same initializer, using the same null-coalescing shape so an unset override preserves today's behaviour exactly:

```csharp
                UseUnityBuild = request.UseUnityBuild ?? true,
                UsePCH = request.UsePrecompiledHeaders ?? true,
                UseDistributedBuild = !string.IsNullOrWhiteSpace(request.CoordinatorAddress),
                CoordinatorAddress = request.CoordinatorAddress,
```

**Before writing this, read `BuildContext`'s declaration and confirm the property names and their existing defaults.** If `UseUnityBuild`/`UsePCH` already default to `true` on `BuildContext`, the `?? true` above preserves that. If they default to something else, match the existing default instead — the requirement is that an unset override changes nothing, not that it forces `true`. State in your report what the actual defaults were and what you used.

`EnableLinkTimeOptimization` has no `BuildContext` counterpart — LTO lives on `TargetRules.EnableLTO`, which is declared by the project's own rules file and is not reachable from the request. **Do not invent a path to it.** Accept the property on the request (so the GUI can carry the user's preference) and report plainly that it is not yet honoured, so Task 16 can surface that state to the user rather than pretending the toggle works.

- [ ] **Step 3: Confirm the CLI is unaffected**

```bash
grep -rn "new BuildOrchestratorRequest" F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider/src --include=*.cs
```

Every existing construction site must still compile untouched (all four properties are optional). Confirm none of them set the new properties.

- [ ] **Step 4: Build, run the backend tests, commit**

```bash
dotnet build F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\Omen.sln -c Debug
dotnet test F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\tests\Omen.Executors.Tests\Omen.Executors.Tests.csproj -c Debug
dotnet test F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\tests\Omen.Core.Tests\Omen.Core.Tests.csproj -c Debug
```

These two suites cover the orchestrator seam and **must stay green** — they are the guard that this change is genuinely non-breaking.

```bash
git add src/Omen.Executors/Orchestration/BuildOrchestrator.cs
git commit -m "feat(executors): accept optional GUI build overrides on BuildOrchestratorRequest"
```

---

### Task 12: Settings persistence and view model

**Files:**
- Modify: `src/Omen.GUI/Services/GuiSettings.cs`
- Create: `src/Omen.GUI/ViewModels/SettingsViewModel.cs`
- Modify: `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`

**Interfaces:**
- Produces: `GuiSettings` gains `CheckForUpdatesOnStartup` (`bool`, default `true`), `ConfirmBeforeClean` (`bool`, default `true`), `DefaultProjectFolder` (`string?`), `DefaultConfiguration` (`string`, default `"Development"`), `ParallelJobs` (`int`, default `Environment.ProcessorCount`), `UseUnityBuilds` (`bool`, default `true`), `UsePrecompiledHeaders` (`bool`, default `true`), `EnableLto` (`bool`, default `false`), `EnableDistributedBuilds` (`bool`, default `false`), `CoordinatorAddress` (`string`, default `""`), `LocalAgentJobs` (`int`, default `Environment.ProcessorCount`), `Theme` (`string`, default `"Dark"`), `UiDensity` (`string`, default `"Standard"`), `ConsoleFontSize` (`int`, default `12`).
- Produces: `SettingsViewModel` with an `[ObservableProperty]` per setting, a `SelectedCategory` (`string`, default `"General"`), `Load()`, `Save()`, and `Toolchains` (`IReadOnlyList<ToolchainStatus>`).
- Produces: `record ToolchainStatus(string Name, string Status, ToolchainState State)` and `enum ToolchainState { Detected, NotFound, Unavailable }`.
- Produces: `MainWindowViewModel.{Settings (SettingsViewModel), IsSettingsOpen (bool), OpenSettings(), CloseSettings()}`.

- [ ] **Step 1: Extend `GuiSettings`**

Add the fourteen properties above with the stated defaults, keeping the existing `LastProjectPath`, `Load()` and `Save()` intact. `Load()` already swallows `JsonException`/`IOException`/`UnauthorizedAccessException` and returns defaults — preserve that, and note that adding properties is backward-compatible because `System.Text.Json` leaves absent members at their default.

- [ ] **Step 2: Create `SettingsViewModel`**

One `[ObservableProperty]` per setting, `SelectedCategory`, plus:

```csharp
    /// <summary>
    /// Live toolchain detection. PlatformFactory.GetAvailablePlatforms() yields one tuple per
    /// known platform with a null SDKInfo when that platform's SDK is not installed, so this
    /// needs no detection logic of its own.
    /// </summary>
    public IReadOnlyList<ToolchainStatus> Toolchains =>
        PlatformFactory.GetAvailablePlatforms()
            .Select(p => new ToolchainStatus(
                Name: $"{p.Platform} ({p.Sdk.Name})",
                Status: p.Info is not null ? "Detected" : "Not found",
                State: p.Info is not null ? ToolchainState.Detected : ToolchainState.NotFound))
            .ToList();
```

**Read `IPlatformSDK` and `SDKInfo` before writing this** — confirm `Sdk.Name` exists and what `SDKInfo` actually carries (a version string would make a better status than the literal "Detected"). Use what is really there and say what you found.

`Load()` copies from `GuiSettings.Load()`; `Save()` copies back and calls `GuiSettings.Save()`.

- [ ] **Step 3: Hook it into `MainWindowViewModel`**

Add `public SettingsViewModel Settings { get; } = new();`, an `[ObservableProperty] private bool _isSettingsOpen;`, and `OpenSettings()`/`CloseSettings()` (open calls `Settings.Load()` first so the dialog always reflects what is on disk).

Then apply the persisted settings where they matter: in `BuildAsync`, populate the new `BuildOrchestratorRequest` properties from `Settings` (`Jobs = Settings.ParallelJobs`, `UseUnityBuild = Settings.UseUnityBuilds`, `UsePrecompiledHeaders = Settings.UsePrecompiledHeaders`, `EnableLinkTimeOptimization = Settings.EnableLto`, `CoordinatorAddress = Settings.EnableDistributedBuilds ? Settings.CoordinatorAddress : null`).

- [ ] **Step 4: Build and commit**

```bash
git add src/Omen.GUI/Services/GuiSettings.cs src/Omen.GUI/ViewModels
git commit -m "feat(gui): add settings persistence, view model and live toolchain detection"
```

---

### Task 13: Settings modal shell

**Files:**
- Create: `src/Omen.GUI/Views/SettingsDialog.xaml` + `.xaml.cs`
- Create: `src/Omen.GUI/Views/Settings/SettingsGeneralPage.xaml` + `.xaml.cs`
- Create: `src/Omen.GUI/Views/Settings/SettingsBuildPage.xaml` + `.xaml.cs`
- Create: `src/Omen.GUI/Views/Settings/SettingsToolchainsPage.xaml` + `.xaml.cs`
- Create: `src/Omen.GUI/Views/Settings/SettingsDistributedPage.xaml` + `.xaml.cs`
- Create: `src/Omen.GUI/Views/Settings/SettingsAppearancePage.xaml` + `.xaml.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.xaml`
- Modify: `src/Omen.GUI/Controls/Toolbar.xaml.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.{Settings, IsSettingsOpen, CloseSettings}` from Task 12.
- Produces: the modal shell plus **five empty page `UserControl`s**, each hosted in the dialog and visible only for its category. Tasks 14-17 fill their own page files and therefore never touch a shared file — that is the point of creating all five here.

**Tasks 14-17 are independent of each other and may run in parallel, because each owns its own page file.** All four require this task.

Each page `UserControl` is created empty in this task, e.g.:

```xml
<UserControl x:Class="Omen.GUI.Views.Settings.SettingsGeneralPage"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StackPanel />
</UserControl>
```

with the matching code-behind carrying the copyright header and calling `InitializeComponent()`. Repeat for all five, changing only the class name.

Each page's `DataContext` is the `SettingsViewModel` — set it once where the dialog hosts the page (`DataContext="{Binding Settings}"`), so the pages bind directly to setting properties without walking ancestors.

- [ ] **Step 1: Build the shell**

An overlay `Grid` layered over the whole window inside `MainWindow.xaml`'s root (after the main content, so it paints on top), visible only when `IsSettingsOpen`:

- Backdrop: `#8C000000` (that is `rgba(0,0,0,.55)` in WPF `#AARRGGBB`) — this is a hex literal, so put it in `Colors.xaml` as `Color.ModalScrim` and reference `Brush.ModalScrim`.
- Dialog: 860×560, centred, `Brush.Panel`, 1px `Brush.PopupBorder`, `CornerRadius` 8, drop shadow (`BlurRadius` 60, `ShadowDepth` 20, `Opacity` 0.6).
- Header 40px: "Settings" 13px SemiBold + `✕` closing the dialog.
- Body: 210px sidebar + content area.
  - Sidebar: a "Find setting..." `PlaceholderTextBox`, then five category rows (`General`, `Build`, `Toolchains`, `Distributed builds`, `Appearance`) — padding `14,7`, 12.5px; selected row has `Foreground` White, `Background` `Brush.SelectedBg`, and a 2px left border `Brush.Accent`. Bind selection to `Settings.SelectedCategory`.
  - Content: a `ContentControl` whose content switches on `SelectedCategory` — five `<ContentControl x:Name="GeneralPage"/>` … style slots, each visible only for its category. Tasks 14-17 fill them.
- Footer 48px, right-aligned: **Cancel** (transparent, 1px `Brush.Border`) which calls `CloseSettings()` without saving, and **OK** (`Style.Button.ToolbarPrimary`) which calls `Settings.Save()` then `CloseSettings()`. Both 28px tall, `14,0` padding.

Clicking the backdrop closes; clicking the dialog must not (handle the dialog's `MouseDown` and set `e.Handled = true`).

The sidebar's "Find setting..." box is presentational — nothing filters on it yet. Say so in your report rather than leaving it looking functional.

- [ ] **Step 2: Wire the gear button**

Replace Task 4's placeholder handler in `Toolbar.xaml.cs` so it calls `OpenSettings()` on the `MainWindowViewModel` (reach it via `Window.GetWindow(this)?.DataContext`, the same pattern the Options pane's Configure button uses). Delete the `// Task 13 replaces this` comment.

- [ ] **Step 3: Build and commit**

```bash
git add src/Omen.GUI/Views/SettingsDialog.xaml src/Omen.GUI/Views/SettingsDialog.xaml.cs src/Omen.GUI/Views/MainWindow.xaml src/Omen.GUI/Controls/Toolbar.xaml.cs src/Omen.GUI/Themes/Colors.xaml src/Omen.GUI/Themes/Colors.Light.xaml src/Omen.GUI/Themes/Brushes.xaml
git commit -m "feat(gui): add the Settings modal shell with category navigation"
```

---

### Task 14: Settings — General and Build pages

**Files:**
- Modify: `src/Omen.GUI/Views/SettingsDialog.xaml`

**Interfaces:**
- Consumes: `SettingsViewModel.{CheckForUpdatesOnStartup, ConfirmBeforeClean, DefaultProjectFolder, DefaultConfiguration, ParallelJobs, UseUnityBuilds, UsePrecompiledHeaders, EnableLto}`.

**Independent of Tasks 15-17.** All four require Task 13.

Row convention for every settings page: label left (12.5px, `Brush.TextSecondary`), control right, 14px vertical gap, `MaxWidth` 480.

- [ ] **Step 1: General page**

- "Check for updates on startup" → `CheckBox` bound to `CheckForUpdatesOnStartup`.
- "Show confirmation before Clean" → `CheckBox` bound to `ConfirmBeforeClean`.
- "Default project folder" → readonly `TextBox`, 220px, `Font.Mono` 11.5px, `Brush.TextMuted`, bound to `DefaultProjectFolder`, plus a `…` browse button opening `OpenFolderDialog`.

Page title "General", 14px SemiBold, 14px bottom margin.

- [ ] **Step 2: Build page**

- "Default configuration" → `ComboBox` 180px, items `Development`, `Debug`, `Shipping`, bound to `DefaultConfiguration`.
- "Parallel jobs" → `NumericTextBox` 80px bound to `ParallelJobs`.
- "Use Unity builds" → `CheckBox` bound to `UseUnityBuilds`.
- "Use precompiled headers" → `CheckBox` bound to `UsePrecompiledHeaders`.
- "Enable link-time optimization" → `CheckBox` bound to `EnableLto`.

Task 11 established that LTO is **not** honoured by the orchestrator (it lives on `TargetRules`, unreachable from the request). Add a short `Brush.TextDim` 11px note under that checkbox saying the preference is stored but not yet applied to builds, so the control does not silently lie. Confirm Task 11's report before wording it — if LTO did turn out reachable, drop the note.

- [ ] **Step 3: Build and commit**

```bash
git add src/Omen.GUI/Views/SettingsDialog.xaml
git commit -m "feat(gui): add the General and Build settings pages"
```

---

### Task 15: Settings — Toolchains page

**Files:**
- Modify: `src/Omen.GUI/Views/SettingsDialog.xaml`
- Modify: `src/Omen.GUI/Themes/Controls.xaml`

**Interfaces:**
- Consumes: `SettingsViewModel.Toolchains` (`IReadOnlyList<ToolchainStatus>`), `ToolchainStatus.{Name, Status, State}`, `ToolchainState.{Detected, NotFound, Unavailable}`.
- Produces: `Conv.ToolchainStateToBrush` — an `IValueConverter` mapping `Detected`→`Brush.Success`, `NotFound`→`Brush.Error`, `Unavailable`→`Brush.TextDim`.

**Independent of Tasks 14, 16, 17.**

This page is genuinely live — it reflects the SDKs actually installed on the machine, not the design's illustrative list.

- [ ] **Step 1: Add the converter**

Create `src/Omen.GUI/Converters/ToolchainStateToBrushConverter.cs` following the existing `BuildStateToBrushConverter` pattern (resource lookup by key via `Application.Current.TryFindResource`), and register it as `Conv.ToolchainStateToBrush` in `Controls.xaml`.

- [ ] **Step 2: Build the page**

Title "Toolchains", then an `ItemsControl` over `Toolchains`. Each row: name left (12.5px, `Brush.TextSecondary`), status right (`Font.Mono`, coloured via `Conv.ToolchainStateToBrush`), 8px vertical padding, 1px bottom border `Brush.RowDivider`, `MaxWidth` 520.

If the list comes back empty, show a `Brush.TextDim` "No platform SDKs detected." row rather than a blank page.

- [ ] **Step 3: Build and commit**

```bash
git add src/Omen.GUI/Views/SettingsDialog.xaml src/Omen.GUI/Converters src/Omen.GUI/Themes/Controls.xaml
git commit -m "feat(gui): add the Toolchains settings page with live SDK detection"
```

---

### Task 16: Settings — Distributed builds page

**Files:**
- Modify: `src/Omen.GUI/Views/SettingsDialog.xaml`

**Interfaces:**
- Consumes: `SettingsViewModel.{EnableDistributedBuilds, CoordinatorAddress, LocalAgentJobs}`.

**Independent of Tasks 14, 15, 17.**

- [ ] **Step 1: Build the page**

Title "Distributed builds (OmenNet)", then:
- "Enable distributed builds" → `CheckBox` bound to `EnableDistributedBuilds`.
- "Coordinator address" → `TextBox` 180px, `Font.Mono` 11.5px, bound to `CoordinatorAddress`, `IsEnabled` bound to `EnableDistributedBuilds`.
- "Local agent jobs" → `NumericTextBox` 80px bound to `LocalAgentJobs`, `IsEnabled` bound to `EnableDistributedBuilds`.

Task 12 wires `CoordinatorAddress` into the build request (gated on `EnableDistributedBuilds`), so the first two are genuinely live. `LocalAgentJobs` has no consumer — the request carries no agent-side job count. Add the same `Brush.TextDim` 11px "stored but not yet applied" note beneath it as Task 14 uses for LTO.

- [ ] **Step 2: Build and commit**

```bash
git add src/Omen.GUI/Views/SettingsDialog.xaml
git commit -m "feat(gui): add the Distributed builds settings page"
```

---

### Task 17: Settings — Appearance page with live theme and density

**Files:**
- Modify: `src/Omen.GUI/Views/SettingsDialog.xaml`
- Create: `src/Omen.GUI/Themes/Metrics.Compact.xaml`, `src/Omen.GUI/Themes/Metrics.Roomy.xaml`
- Create: `src/Omen.GUI/Services/ThemeService.cs`
- Modify: `src/Omen.GUI/App.xaml.cs`
- Modify: `src/Omen.GUI/ViewModels/SettingsViewModel.cs`

**Interfaces:**
- Consumes: `SettingsViewModel.{Theme, UiDensity, ConsoleFontSize}`.
- Produces: `ThemeService.{ApplyTheme(string), ApplyDensity(string)}` — static methods that swap the merged palette/metrics dictionaries at runtime.

**Independent of Tasks 14-16.** This is the task that makes Task 1's `DynamicResource` discipline pay off; if any brush was bound with `StaticResource`, the theme switch will partially fail and this task will surface it.

- [ ] **Step 1: Density metric variants**

`Metrics.Compact.xaml` and `Metrics.Roomy.xaml` — same five keys as `Metrics.xaml`, scaled:

| Key | Compact | Standard | Roomy |
|---|---|---|---|
| `Metric.RowPadding` | `8,3,8,3` | `10,6,10,6` | `12,9,12,9` |
| `Metric.PanelHeaderHeight` | `24` | `28` | `32` |
| `Metric.ToolbarButtonHeight` | `22` | `26` | `30` |
| `Metric.FontSizeBody` | `11.5` | `12.5` | `13.5` |
| `Metric.FontSizeMono` | `11` | `12` | `13` |

- [ ] **Step 2: Create `ThemeService`**

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;

namespace Omen.GUI.Services;

/// <summary>
/// Swaps the merged palette and metric dictionaries at runtime. Brushes and metrics are
/// consumed via DynamicResource throughout, so replacing the dictionary they resolve
/// against re-themes the live window without a restart.
/// </summary>
public static class ThemeService
{
    public static void ApplyTheme(string theme) =>
        Swap("Themes/Colors", theme == "Light" ? "Themes/Colors.Light.xaml" : "Themes/Colors.xaml");

    public static void ApplyDensity(string density) =>
        Swap("Themes/Metrics", density switch
        {
            "Compact" => "Themes/Metrics.Compact.xaml",
            "Roomy" => "Themes/Metrics.Roomy.xaml",
            _ => "Themes/Metrics.xaml"
        });

    private static void Swap(string prefix, string newSource)
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null) return;

        for (var i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.OriginalString;
            if (src is null || !src.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            merged[i] = new ResourceDictionary { Source = new Uri(newSource, UriKind.Relative) };
            return;
        }
    }
}
```

The `prefix` match is deliberately loose so `Themes/Colors.xaml` and `Themes/Colors.Light.xaml` both match `"Themes/Colors"`. **Verify the actual `Source.OriginalString` values at runtime** — if `App.xaml` merges them with a different string form than you expect, the loop silently matches nothing and the switch does nothing. Say in your report how you confirmed the match.

`Brushes.xaml` merges `Colors.xaml` itself, so swapping the *top-level* merged entry may not be enough — the brushes resolve against their own merged child. Check whether `App.xaml` merges `Colors.xaml` directly; if it does not, add it there (before `Brushes.xaml`) so there is a top-level entry to swap, and confirm the `DynamicResource` colour lookups then resolve against it.

- [ ] **Step 3: Apply saved theme at startup**

In `App.xaml.cs`, override `OnStartup`, load `GuiSettings`, and call `ThemeService.ApplyTheme`/`ApplyDensity` before the main window shows.

- [ ] **Step 4: Apply on change**

In `SettingsViewModel`, add generated-partial hooks so the change is immediate rather than waiting for OK:

```csharp
    partial void OnThemeChanged(string value) => ThemeService.ApplyTheme(value);
    partial void OnUiDensityChanged(string value) => ThemeService.ApplyDensity(value);
```

Note this means Cancel will not revert a theme/density preview. Either re-apply the loaded values in `CloseSettings()` when the user cancels, or state plainly in your report that preview-on-change is intentional and Cancel does not undo it. Pick one and be explicit — a Cancel button that silently keeps some changes is the kind of half-lie this project has been rejecting.

- [ ] **Step 5: Build the page**

Title "Appearance":
- "Theme" → `ComboBox` 150px (`Dark`, `Light`) bound to `Theme`. Add a `Brush.TextDim` 11px note that the light palette is derived and pending design review.
- "UI density" → `ComboBox` 150px (`Compact`, `Standard`, `Roomy`) bound to `UiDensity`.
- "Console font size" → `NumericTextBox` 80px bound to `ConsoleFontSize`.

Bind the build-output list's `FontSize` to `Settings.ConsoleFontSize` so it is genuinely live.

- [ ] **Step 6: Build and commit**

```bash
git add src/Omen.GUI/Views/SettingsDialog.xaml src/Omen.GUI/Themes src/Omen.GUI/Services/ThemeService.cs src/Omen.GUI/App.xaml.cs src/Omen.GUI/ViewModels/SettingsViewModel.cs
git commit -m "feat(gui): add the Appearance settings page with live theme and density switching"
```

---

### Task 18: Manual acceptance

**Files:**
- Create: `docs/superpowers/plans/2026-08-19-omen-gui-design-port-acceptance.md`

No subagent can run this. It is the human gate, and it is the only verification this plan has beyond a clean build.

- [ ] **Step 1: Full clean build and backend test run**

```bash
dotnet clean F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\Omen.sln -c Debug
dotnet build F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\Omen.sln -c Debug
dotnet test F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\tests\Omen.Core.Tests\Omen.Core.Tests.csproj -c Debug
dotnet test F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\tests\Omen.Executors.Tests\Omen.Executors.Tests.csproj -c Debug
```

- [ ] **Step 2: Write the checklist**

Create the acceptance document with these items, ordered by risk — highest first, because the runtime-only failures in this codebase have consistently been invisible to both compilation and static review.

```markdown
# Omen GUI design port — manual acceptance

Run: `dotnet run --project F:\Omen-githubpublic\.worktrees\omen-gui-wpf-rider\src\Omen.GUI -c Debug`
Project under test: `F:\Omen-githubpublic\examples\GemSample`

## Highest risk first
- [ ] Fonts: UI text renders in Work Sans (not Segoe fallback); output/breadcrumb render in JetBrains Mono. Check a SemiBold label — Work Sans is variable-only, so bold may be synthesised and look wrong.
- [ ] Settings → Appearance → Theme → Light: the WHOLE window re-themes. Any element still dark means a StaticResource brush that should be DynamicResource.
- [ ] Settings → Appearance → UI density: Compact/Roomy visibly change row heights and font sizes.
- [ ] Settings → Toolchains: shows the SDKs actually on this machine, with plausible Detected/Not found states.
- [ ] Settings OK persists; reopen the dialog and the values survive. Restart the app — they still survive.
- [ ] Build with Parallel jobs set to 2 vs 16 in Settings — the build actually respects it.
- [ ] Float a tool window (drag its tab out) — chrome is themed, content still renders, Configure still works.
- [ ] Window: drag the menu bar to move; double-click to maximise; maximise on the secondary monitor and confirm the taskbar is not covered; resize from every edge.

## Chrome
- [ ] Menu bar: icon + "Omen" wordmark; all five menus open; separators are dark not light; Tools/Help items report "not wired up yet" rather than doing nothing.
- [ ] Window controls sit at the right of the 30px menu bar; close hover is red.
- [ ] Toolbar: Build is the blue primary button; Cancel is disabled when not building; Platform/Config combos populate and hide when a build matrix is active.
- [ ] Search pill renders with the ⇧⇧ badge (inert by design).
- [ ] Status bar: dot colour tracks build state; breadcrumb reads `project › platform › config`; progress bar and % move during a build.

## Panels
- [ ] Project tree: root bold with blue icon; .module.cs leaves in mono; Binaries/Intermediate greyed; expanding a large folder does not freeze the window.
- [ ] Build Output / Problems tabs switch; Problems shows only warnings+errors with a matching count badge; line numbers are correct and do not wrap or restart.
- [ ] Options: bool options render as toggle switches that animate; enum/int/path/string editors all work; search filters; Grouped toggles headers; changed options highlight.
- [ ] Build Matrix panel opens from the right rail and its checkboxes hold state.
- [ ] Rails hide/show each pane and the active icon highlights.

## Regressions to confirm are absent
- [ ] Splitters still resize every pane.
- [ ] Configure/Add/Remove are disabled during a build.
- [ ] Close Project clears tree, options, matrix and output.
- [ ] Output auto-scrolls during a long build, including after hiding and re-showing the pane.
```

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/plans/2026-08-19-omen-gui-design-port-acceptance.md
git commit -m "docs: add the manual acceptance checklist for the design port"
```

---

## Self-review notes

- **Spec coverage.** Palette → Task 1. Fonts → Task 1. Icon set → Task 2. Menu bar with five menus + window controls → Task 3. Toolbar with all buttons, combos, search pill, gear → Task 4. Status bar breadcrumb/progress → Task 5. Pane chrome (uppercase headers, ✕, tab underline) → Task 6. Project tree colour roles → Task 7. Tab strip + Problems badge + line numbers → Task 8. Toggle switches + option editors → Task 9. Matrix → Task 10. Settings modal shell → Task 13; its five pages → Tasks 14 (General, Build), 15 (Toolchains), 16 (Distributed), 17 (Appearance). Fully-functional requirement → Tasks 11 (seam), 12 (persistence + live detection), 17 (live theme/density).
- **Two things the design draws that cannot be fully honoured, both surfaced rather than faked.** LTO lives on `TargetRules`, unreachable from `BuildOrchestratorRequest`, so its toggle stores a preference the build does not read (Tasks 11, 14). `LocalAgentJobs` has no consumer on the request either (Task 16). Both get an explicit "stored but not yet applied" note in the UI rather than a control that silently lies.
- **Work Sans is variable-only.** Verified: no `static/` directory in google/fonts and no TTFs on the fontsource CDN. WPF renders one instance and synthesises the design's 600/700 weights. Task 1 embeds it with a real fallback stack and Task 18 makes checking it the first acceptance item.
- **The light palette is derived, not designed.** Flagged in Task 1's comment, in the Appearance page's own UI note, and in the acceptance checklist.
- **Type consistency.** `SettingsViewModel` property names in Task 12 match their bindings in Tasks 14-17. `ToolchainStatus`/`ToolchainState` are defined once (Task 12) and consumed once (Task 15). `BuildOrchestratorRequest`'s four new properties are named identically in Tasks 11 and 12. `ThemeService.ApplyTheme`/`ApplyDensity` are defined and called in Task 17 only. `Conv.UpperCase` is created in Task 6 and reused in Task 9.
- **Sequencing.** Tasks 1→2→3→4→5→6 are strictly sequential (shared `MainWindow.xaml`, `TopBar.xaml`, `Themes/`). Tasks 7, 8, 9, 10 are mutually independent after Task 6. Task 11 is independent of all GUI work. Task 12 needs Task 11. Task 13 needs Task 12. Tasks 14, 15, 16, 17 are mutually independent after Task 13 — but all four edit `SettingsDialog.xaml`, so if they run in parallel they will conflict; run them sequentially unless each page is first extracted into its own `UserControl`.
- **Known risk carried from Task 1 into Task 17.** Every brush must use `DynamicResource` for its `Color`, and `App.xaml` must merge `Colors.xaml` at top level for `ThemeService` to have something to swap. If Task 1 gets this wrong the failure is invisible until Task 17, which is why Task 17's step 2 tells the implementer to verify the match rather than assume it.
