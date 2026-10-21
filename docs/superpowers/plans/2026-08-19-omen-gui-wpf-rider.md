# Omen.GUI WPF / Rider-Design Rebuild Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Omen.GUI's Avalonia UI with a WPF application built in JetBrains Rider's New UI design language, using AvalonDock for real dockable tool windows, porting every existing feature and fixing the 13 defects found while inventorying the old app.

**Architecture:** Task 1 converts the existing `src/Omen.GUI` project in place to WPF with a minimal running shell, porting the ViewModels/Models/Services (which are ~90% framework-agnostic) and deleting all Avalonia XAML in one move — so the app builds and runs at every subsequent step. Tasks 2-4 lay the foundation later tasks consume (theme tokens → custom controls → AvalonDock shell with placeholder panes). Tasks 5-7 fill the chrome slots. Tasks 8-11 fill the four tool windows and are mutually independent (separate `UserControl` files). Task 12 sweeps the ViewModel-level defects. Task 13 removes the last Avalonia traces and runs the manual acceptance gate.

**Tech Stack:** C# / .NET 8 (`net8.0-windows`), WPF, Dirkster.AvalonDock 5.0.0, CommunityToolkit.Mvvm 8.2.2 (already referenced, framework-agnostic, ports untouched).

## Global Constraints

- **Target framework:** `net8.0-windows` with `<UseWPF>true</UseWPF>`. The repo's `Directory.Build.props` sets `<TargetFramework>net8.0</TargetFramework>` globally — the GUI csproj **must** override it in its own `PropertyGroup` (MSBuild imports `Directory.Build.props` first, so a csproj property wins).
- **Central package management is on** (`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`). New packages need a `<PackageVersion>` entry there and a versionless `<PackageReference>` in the csproj. Never put `Version=` in the csproj.
- **Only one new dependency is authorized:** `Dirkster.AvalonDock` version `5.0.0`. No other new packages.
- **The backend seam is frozen.** No signature of `BuildOrchestrator.BuildAsync`, `CleanOrchestrator.CleanAsync`, `ProjectGenerationOrchestrator.GenerateAsync`, `OptionsOrchestrator.{DiscoverAsync,SaveOptions,AddManualOption,RemoveManualOption}`, `RuleCompiler`/`CompiledRules`, or `PlatformFactory.GetAvailablePlatforms` may change. If a task appears to need one, stop and report it.
- **No hex color literals outside `Themes/Colors.xaml`.** Every other file references a brush key.
- **No new test project.** `Omen.GUI` has no automated tests and the spec deliberately does not add any (spec §8). Do not scaffold one. Verification is: the solution builds clean, and Task 13's manual checklist passes.
- **Logic stays in ViewModels, not code-behind.** Code-behind may wire events and open dialogs; filtering, grouping, state, and orchestrator calls live in ViewModels.
- Copyright header on every new `.cs` file: `// Omen Build System` / `// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.`
- Build verification command used throughout: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`. "Builds clean" means 0 errors (pre-existing warnings in other projects are acceptable).

---

### Task 1: Convert the project to WPF with a minimal running shell

**Files:**
- Modify: `Directory.Packages.props` (add AvalonDock, remove Avalonia entries)
- Modify: `src/Omen.GUI/Omen.GUI.csproj` (full rewrite)
- Delete: `src/Omen.GUI/App.axaml`, `App.axaml.cs`, `Views/MainWindow.axaml`, `Views/MainWindow.axaml.cs`, `Styles/Signal.axaml`, `Styles/Icons.axaml`
- Create: `src/Omen.GUI/App.xaml`, `src/Omen.GUI/App.xaml.cs`, `src/Omen.GUI/Views/MainWindow.xaml`, `src/Omen.GUI/Views/MainWindow.xaml.cs`
- Modify: `src/Omen.GUI/Program.cs` (delete — WPF uses `App.xaml`'s generated entry point)
- Modify: `src/Omen.GUI/Converters/OutputLevelToBrushConverter.cs`
- Modify: `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`

**Interfaces:**
- Produces: a building, launchable WPF app. `MainWindowViewModel` no longer exposes `StatusDotBrush` (an `Avalonia.Media.IBrush`); it exposes the existing `CurrentBuildState` (`MainWindowViewModel.BuildState`) and the view maps state→brush. All other ViewModel members keep their current names and signatures for later tasks: `ProjectPath`, `StatusText`, `IsBuilding`, `ProgressValue`, `SelectedPlatform`, `SelectedConfiguration`, `AvailablePlatforms`, `Configurations`, `ProjectTreeRoots`, `OutputLines`, `OptionsPanel`, `MatrixPanel`, `LoadProject(string)`, `CloseProject()`, `BuildAsync()`, `RebuildAsync()`, `CleanAsync()`, `CancelBuild()`, `GenerateProjectFilesAsync(IdeKind)`.

`ViewModels/OptionsPanelViewModel.cs`, `ViewModels/BuildOptionViewModel.cs`, `ViewModels/MatrixPanelViewModel.cs`, `ViewModels/ViewModelBase.cs`, `Models/OutputLine.cs`, `Models/ProjectTreeNode.cs`, and `Services/GuiSettings.cs` are **framework-agnostic and must not be edited in this task** — they compile as-is under WPF.

- [ ] **Step 1: Swap the package entries**

In `Directory.Packages.props`, replace the five Avalonia lines under `<!-- UI -->` so the block reads:

```xml
    <!-- UI -->
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.2.2" />
    <PackageVersion Include="Dirkster.AvalonDock" Version="5.0.0" />
```

- [ ] **Step 2: Rewrite the csproj**

Replace `src/Omen.GUI/Omen.GUI.csproj` entirely:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <!-- Overrides Directory.Build.props' net8.0 - WPF requires a Windows-targeted TFM. -->
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <RootNamespace>Omen.GUI</RootNamespace>
    <AssemblyName>Omen.GUI</AssemblyName>
    <Description>Omen Build System - Desktop GUI</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" />
    <PackageReference Include="Dirkster.AvalonDock" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Omen.Core\Omen.Core.csproj" />
    <ProjectReference Include="..\Omen.Platforms\Omen.Platforms.csproj" />
    <ProjectReference Include="..\Omen.Executors\Omen.Executors.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Delete the Avalonia files**

```bash
cd F:\Omen-githubpublic
git rm src/Omen.GUI/App.axaml src/Omen.GUI/App.axaml.cs src/Omen.GUI/Program.cs
git rm src/Omen.GUI/Views/MainWindow.axaml src/Omen.GUI/Views/MainWindow.axaml.cs
git rm src/Omen.GUI/Styles/Signal.axaml src/Omen.GUI/Styles/Icons.axaml
```

- [ ] **Step 4: Create `App.xaml` and `App.xaml.cs`**

`src/Omen.GUI/App.xaml`:

```xml
<Application x:Class="Omen.GUI.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="Views/MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary />
    </Application.Resources>
</Application>
```

`src/Omen.GUI/App.xaml.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;

namespace Omen.GUI;

public partial class App : Application
{
}
```

- [ ] **Step 5: Create the minimal `MainWindow`**

`src/Omen.GUI/Views/MainWindow.xaml`:

```xml
<Window x:Class="Omen.GUI.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:Omen.GUI.ViewModels"
        Title="Omen Build System"
        Width="1400" Height="900"
        MinWidth="900" MinHeight="600"
        Background="#1E1F22">
    <Window.DataContext>
        <vm:MainWindowViewModel />
    </Window.DataContext>
    <Grid />
</Window>
```

(The literal `#1E1F22` here is temporary scaffolding; Task 2 replaces it with a themed brush and this is the only place it is permitted.)

`src/Omen.GUI/Views/MainWindow.xaml.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;

namespace Omen.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 6: Port the converter to WPF**

Replace `src/Omen.GUI/Converters/OutputLevelToBrushConverter.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Omen.Executors.Orchestration;

namespace Omen.GUI.Converters;

/// <summary>
/// Maps an orchestrator event level to the themed brush the build-output list draws it in.
/// Brushes are resolved from the application resource dictionary by key so that all colour
/// values stay in Themes/Colors.xaml.
/// </summary>
public sealed class OutputLevelToBrushConverter : IValueConverter
{
    public static readonly OutputLevelToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            OrchestratorEventLevel.Error => "Brush.Error",
            OrchestratorEventLevel.Warning => "Brush.Warning",
            OrchestratorEventLevel.Success => "Brush.Success",
            _ => "Brush.TextPrimary"
        };

        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

The brush keys it references are created in Task 2. Until then the converter returns `UnsetValue` and text falls back to the inherited foreground — harmless, and the app still builds and runs.

- [ ] **Step 7: Remove the Avalonia brush leak from `MainWindowViewModel`**

In `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`:

1. Delete the `using Avalonia.Media;` directive.
2. Delete the entire `StatusDotBrush` computed property (the `IBrush`-returning switch).
3. On the `CurrentBuildState` field, delete the `[NotifyPropertyChangedFor(nameof(StatusDotBrush))]` attribute, leaving the `[ObservableProperty]` attribute in place.

Leave every other member untouched. The `BuildState` enum stays; Task 6 maps it to a brush in the view.

- [ ] **Step 8: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors. If `MainWindowViewModel` still fails to compile, the likely cause is another `Avalonia.*` reference the inventory did not catch — remove it the same way and note it in the task report.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`
Expected: an empty dark 1400×900 window titled "Omen Build System" appears. Close it.

- [ ] **Step 9: Commit**

```bash
git add -A src/Omen.GUI Directory.Packages.props
git commit -m "feat(gui): convert Omen.GUI from Avalonia to WPF with a minimal shell"
```

---

### Task 2: Theme foundation — colour tokens, brushes, base control styles, icons

**Files:**
- Create: `src/Omen.GUI/Themes/Colors.xaml`
- Create: `src/Omen.GUI/Themes/Brushes.xaml`
- Create: `src/Omen.GUI/Themes/Controls.xaml`
- Create: `src/Omen.GUI/Themes/Icons.xaml`
- Modify: `src/Omen.GUI/App.xaml`
- Modify: `src/Omen.GUI/Views/MainWindow.xaml`

**Interfaces:**
- Produces: brush keys every later task consumes — `Brush.Surface`, `Brush.SurfaceRaised`, `Brush.SurfaceSelected`, `Brush.SurfaceHover`, `Brush.Border`, `Brush.TextPrimary`, `Brush.TextSecondary`, `Brush.TextDisabled`, `Brush.Accent`, `Brush.AccentHover`, `Brush.Success`, `Brush.Warning`, `Brush.Error`. Geometry keys `Icon.Open`, `Icon.Build`, `Icon.Rebuild`, `Icon.Clean`, `Icon.Cancel`. Implicit styles for `Button`, `TextBox`, `CheckBox`, `ComboBox`, `TreeView`/`TreeViewItem`, `ListBox`/`ListBoxItem`, `TextBlock`, plus keyed styles `Style.Button.Primary`, `Style.Button.Icon`, `Style.Text.Secondary`, `Style.Text.PaneHeader`.

- [ ] **Step 1: Create `Themes/Colors.xaml`**

This is the only file in the project permitted to contain hex literals. Values are taken from the Rider 2026 New UI reference screenshots (spec §3.1).

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Surfaces: near-uniform ground, separated by hairlines rather than contrasting fills. -->
    <Color x:Key="Color.Surface">#FF1E1F22</Color>
    <Color x:Key="Color.SurfaceRaised">#FF2B2D30</Color>
    <Color x:Key="Color.SurfaceSelected">#FF2E3238</Color>
    <Color x:Key="Color.SurfaceHover">#FF26282C</Color>
    <Color x:Key="Color.Border">#FF393B40</Color>

    <!-- Text -->
    <Color x:Key="Color.TextPrimary">#FFCED0D6</Color>
    <Color x:Key="Color.TextSecondary">#FF7A7D85</Color>
    <Color x:Key="Color.TextDisabled">#FF5A5D63</Color>

    <!-- Semantic -->
    <Color x:Key="Color.Accent">#FF3574F0</Color>
    <Color x:Key="Color.AccentHover">#FF548AF7</Color>
    <Color x:Key="Color.Success">#FF499C54</Color>
    <Color x:Key="Color.Warning">#FFD6AE58</Color>
    <Color x:Key="Color.Error">#FFDB5C5C</Color>

</ResourceDictionary>
```

- [ ] **Step 2: Create `Themes/Brushes.xaml`**

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Colors.xaml" />
    </ResourceDictionary.MergedDictionaries>

    <SolidColorBrush x:Key="Brush.Surface" Color="{StaticResource Color.Surface}" />
    <SolidColorBrush x:Key="Brush.SurfaceRaised" Color="{StaticResource Color.SurfaceRaised}" />
    <SolidColorBrush x:Key="Brush.SurfaceSelected" Color="{StaticResource Color.SurfaceSelected}" />
    <SolidColorBrush x:Key="Brush.SurfaceHover" Color="{StaticResource Color.SurfaceHover}" />
    <SolidColorBrush x:Key="Brush.Border" Color="{StaticResource Color.Border}" />
    <SolidColorBrush x:Key="Brush.TextPrimary" Color="{StaticResource Color.TextPrimary}" />
    <SolidColorBrush x:Key="Brush.TextSecondary" Color="{StaticResource Color.TextSecondary}" />
    <SolidColorBrush x:Key="Brush.TextDisabled" Color="{StaticResource Color.TextDisabled}" />
    <SolidColorBrush x:Key="Brush.Accent" Color="{StaticResource Color.Accent}" />
    <SolidColorBrush x:Key="Brush.AccentHover" Color="{StaticResource Color.AccentHover}" />
    <SolidColorBrush x:Key="Brush.Success" Color="{StaticResource Color.Success}" />
    <SolidColorBrush x:Key="Brush.Warning" Color="{StaticResource Color.Warning}" />
    <SolidColorBrush x:Key="Brush.Error" Color="{StaticResource Color.Error}" />

</ResourceDictionary>
```

- [ ] **Step 3: Create `Themes/Icons.xaml`**

The five path-data strings are carried over verbatim from the deleted `Styles/Icons.axaml` (Material Design, Apache-2.0) and are valid WPF geometry mini-language as-is.

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Geometry x:Key="Icon.Open">M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z</Geometry>
    <Geometry x:Key="Icon.Build">M8,5.14V19.14L19,12.14L8,5.14Z</Geometry>
    <Geometry x:Key="Icon.Rebuild">M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z</Geometry>
    <Geometry x:Key="Icon.Clean">M19,3H14.82C14.4,1.84 13.3,1 12,1C10.7,1 9.6,1.84 9.18,3H5V5H19V3M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z</Geometry>
    <Geometry x:Key="Icon.Cancel">M12,2C6.47,2 2,6.47 2,12C2,17.53 6.47,22 12,22C17.53,22 22,17.53 22,12C22,6.47 17.53,2 12,2M17,15.59L15.59,17L12,13.41L8.41,17L7,15.59L10.59,12L7,8.41L8.41,7L12,10.59L15.59,7L17,8.41L13.41,12L17,15.59Z</Geometry>

    <!-- Drawn for Omen (Rider's own icon artwork is deliberately not copied). -->
    <Geometry x:Key="Icon.Menu">M3,6H21V8H3V6M3,11H21V13H3V11M3,16H21V18H3V16Z</Geometry>
    <Geometry x:Key="Icon.Settings">M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z</Geometry>
    <Geometry x:Key="Icon.Project">M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z</Geometry>
    <Geometry x:Key="Icon.Output">M20,19V7H4V19H20M20,3A2,2 0 0,1 22,5V19A2,2 0 0,1 20,21H4A2,2 0 0,1 2,19V5C2,3.89 2.9,3 4,3H20M13,11H18V13H13V11M9.58,12L6.05,8.47L7.46,7.05L12.41,12L7.46,16.95L6.05,15.54L9.58,12Z</Geometry>
    <Geometry x:Key="Icon.Options">M3,17V19H9V17H3M3,5V7H13V5H3M13,21V19H21V17H13V15H11V21H13M7,9V11H3V13H7V15H9V9H7M21,13V11H11V13H21M15,9H17V7H21V5H17V3H15V9Z</Geometry>
    <Geometry x:Key="Icon.Matrix">M3,3H11V11H3V3M13,3H21V11H13V3M3,13H11V21H3V13M13,13H21V21H13V13Z</Geometry>
    <Geometry x:Key="Icon.More">M12,16A2,2 0 0,1 14,18A2,2 0 0,1 12,20A2,2 0 0,1 10,18A2,2 0 0,1 12,16M12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12A2,2 0 0,1 12,10M12,4A2,2 0 0,1 14,6A2,2 0 0,1 12,8A2,2 0 0,1 10,6A2,2 0 0,1 12,4Z</Geometry>

</ResourceDictionary>
```

- [ ] **Step 4: Create `Themes/Controls.xaml`**

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Brushes.xaml" />
    </ResourceDictionary.MergedDictionaries>

    <FontFamily x:Key="Font.Ui">Segoe UI Variable Text, Segoe UI</FontFamily>
    <FontFamily x:Key="Font.Mono">Cascadia Code, Consolas, Courier New</FontFamily>

    <Style TargetType="TextBlock">
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="12" />
        <Setter Property="TextOptions.TextFormattingMode" Value="Display" />
    </Style>

    <Style x:Key="Style.Text.Secondary" TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
        <Setter Property="Foreground" Value="{StaticResource Brush.TextSecondary}" />
        <Setter Property="FontSize" Value="11" />
    </Style>

    <Style x:Key="Style.Text.PaneHeader" TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
        <Setter Property="Foreground" Value="{StaticResource Brush.TextSecondary}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Margin" Value="10,0,0,0" />
    </Style>

    <Style TargetType="Button">
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="10,4" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="12" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Root"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="4"
                            SnapsToDevicePixels="True">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"
                                          Margin="{TemplateBinding Padding}" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Root" Property="Background" Value="{StaticResource Brush.SurfaceHover}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Foreground" Value="{StaticResource Brush.TextDisabled}" />
                            <Setter TargetName="Root" Property="Opacity" Value="0.6" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="Style.Button.Primary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
        <Setter Property="Background" Value="{StaticResource Brush.Accent}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Accent}" />
        <Setter Property="Foreground" Value="White" />
        <Setter Property="FontWeight" Value="SemiBold" />
    </Style>

    <!-- Borderless square button used by the top bar and the tool-window rails. -->
    <Style x:Key="Style.Button.Icon" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Padding" Value="6" />
        <Setter Property="Width" Value="30" />
        <Setter Property="Height" Value="30" />
    </Style>

    <Style TargetType="TextBox">
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="Background" Value="{StaticResource Brush.Surface}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="6,3" />
        <Setter Property="CaretBrush" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="12" />
    </Style>

    <Style TargetType="CheckBox">
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="12" />
        <Setter Property="VerticalContentAlignment" Value="Center" />
    </Style>

    <Style TargetType="ComboBox">
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="Background" Value="{StaticResource Brush.SurfaceRaised}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="12" />
        <Setter Property="Padding" Value="6,3" />
    </Style>

    <Style TargetType="TreeView">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
    </Style>

    <Style TargetType="TreeViewItem">
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="12" />
        <Setter Property="Padding" Value="2,1" />
    </Style>

    <Style TargetType="ListBox">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}" />
    </Style>

</ResourceDictionary>
```

- [ ] **Step 5: Merge the theme into `App.xaml`**

Replace the `Application.Resources` element:

```xml
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Themes/Controls.xaml" />
                <ResourceDictionary Source="Themes/Icons.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
```

(`Controls.xaml` transitively merges `Brushes.xaml`, which merges `Colors.xaml`, so all three keys sets are available application-wide.)

- [ ] **Step 6: Use the themed brush in `MainWindow`**

In `src/Omen.GUI/Views/MainWindow.xaml`, replace `Background="#1E1F22"` with:

```xml
        Background="{StaticResource Brush.Surface}"
```

- [ ] **Step 7: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`
Expected: the window still opens, now filled with the themed `#1E1F22` surface. Close it.

- [ ] **Step 8: Commit**

```bash
git add src/Omen.GUI/Themes src/Omen.GUI/App.xaml src/Omen.GUI/Views/MainWindow.xaml
git commit -m "feat(gui): add Rider-derived theme tokens, brushes, control styles and icons"
```

---

### Task 3: Custom controls — placeholder text box and numeric text box

**Files:**
- Create: `src/Omen.GUI/Controls/PlaceholderTextBox.cs`
- Create: `src/Omen.GUI/Controls/NumericTextBox.cs`
- Modify: `src/Omen.GUI/Themes/Controls.xaml` (append the two control templates)

**Interfaces:**
- Produces: `Omen.GUI.Controls.PlaceholderTextBox` with a `Placeholder` dependency property (`string`), used by Task 10's search box and Add Entry fields. `Omen.GUI.Controls.NumericTextBox` with a `Value` dependency property (`long`, two-way, defaults to 0), used by Task 10's Int option editor.

WPF has no equivalent of Avalonia's `TextBox.Watermark` or its `NumericUpDown`; both were free in Avalonia and are net-new here (spec §9).

This task touches `Controls.xaml`, which Task 2 created — it must run after Task 2 and is otherwise independent of Tasks 4-11.

- [ ] **Step 1: Create `PlaceholderTextBox`**

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;

namespace Omen.GUI.Controls;

/// <summary>
/// A TextBox that shows grey placeholder text while empty. WPF has no built-in equivalent to
/// Avalonia's TextBox.Watermark, so the hint is drawn by the control template (see
/// Themes/Controls.xaml) and toggled by the text being empty.
/// </summary>
public class PlaceholderTextBox : TextBox
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(PlaceholderTextBox),
            new PropertyMetadata(string.Empty));

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly DependencyProperty HasTextProperty =
        DependencyProperty.Register(nameof(HasText), typeof(bool), typeof(PlaceholderTextBox),
            new PropertyMetadata(false));

    public bool HasText
    {
        get => (bool)GetValue(HasTextProperty);
        private set => SetValue(HasTextProperty, value);
    }

    static PlaceholderTextBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PlaceholderTextBox),
            new FrameworkPropertyMetadata(typeof(PlaceholderTextBox)));
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        HasText = Text.Length > 0;
    }
}
```

- [ ] **Step 2: Create `NumericTextBox`**

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Omen.GUI.Controls;

/// <summary>
/// An integer-only TextBox. WPF has no NumericUpDown; Omen only ever edits whole-number build
/// options, so this deliberately accepts integers rather than reproducing a full spinner.
/// Non-numeric input is rejected at the keystroke, which also removes the old Avalonia
/// behaviour where a NumericUpDown accepted fractions that were then silently truncated.
/// </summary>
public class NumericTextBox : TextBox
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(long), typeof(NumericTextBox),
            new FrameworkPropertyMetadata(0L,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public long Value
    {
        get => (long)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    static NumericTextBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NumericTextBox),
            new FrameworkPropertyMetadata(typeof(NumericTextBox)));
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (NumericTextBox)d;
        var text = ((long)e.NewValue).ToString(CultureInfo.InvariantCulture);
        if (box.Text != text)
            box.Text = text;
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        e.Handled = !IsNumeric(e.Text);
        base.OnPreviewTextInput(e);
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        Value = long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0L;
    }

    private static bool IsNumeric(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsDigit(c) && c != '-')
                return false;
        }
        return text.Length > 0;
    }
}
```

- [ ] **Step 3: Append both templates to `Themes/Controls.xaml`**

Add the `controls` namespace to the root element:

```xml
                    xmlns:controls="clr-namespace:Omen.GUI.Controls"
```

Then append before the closing `</ResourceDictionary>`:

```xml
    <Style TargetType="{x:Type controls:PlaceholderTextBox}" BasedOn="{StaticResource {x:Type TextBox}}">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type controls:PlaceholderTextBox}">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="4"
                            SnapsToDevicePixels="True">
                        <Grid>
                            <TextBlock x:Name="PlaceholderText"
                                       Text="{TemplateBinding Placeholder}"
                                       Foreground="{StaticResource Brush.TextSecondary}"
                                       Margin="{TemplateBinding Padding}"
                                       VerticalAlignment="Center"
                                       IsHitTestVisible="False"
                                       Visibility="Collapsed" />
                            <ScrollViewer x:Name="PART_ContentHost"
                                          Margin="{TemplateBinding Padding}"
                                          VerticalAlignment="Center" />
                        </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="HasText" Value="False">
                            <Setter TargetName="PlaceholderText" Property="Visibility" Value="Visible" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="{x:Type controls:NumericTextBox}" BasedOn="{StaticResource {x:Type TextBox}}" />
```

- [ ] **Step 4: Build**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors. (Neither control is placed in a window yet; Task 10 consumes them.)

- [ ] **Step 5: Commit**

```bash
git add src/Omen.GUI/Controls src/Omen.GUI/Themes/Controls.xaml
git commit -m "feat(gui): add PlaceholderTextBox and NumericTextBox (no WPF equivalents)"
```

---

### Task 4: AvalonDock shell — MainWindow structure, docking theme, placeholder panes

**Files:**
- Create: `src/Omen.GUI/Themes/Docking.xaml`
- Modify: `src/Omen.GUI/Views/MainWindow.xaml`
- Modify: `src/Omen.GUI/App.xaml`
- Create: `src/Omen.GUI/Views/ProjectToolWindow.xaml` + `.xaml.cs` (placeholder)
- Create: `src/Omen.GUI/Views/BuildOutputToolWindow.xaml` + `.xaml.cs` (placeholder)
- Create: `src/Omen.GUI/Views/OptionsToolWindow.xaml` + `.xaml.cs` (placeholder)
- Create: `src/Omen.GUI/Views/MatrixToolWindow.xaml` + `.xaml.cs` (placeholder)
- Create: `src/Omen.GUI/Views/WelcomePane.xaml` + `.xaml.cs`

**Interfaces:**
- Produces: `MainWindow.xaml`'s docking layout with named anchorables `ProjectAnchorable`, `OutputAnchorable`, `OptionsAnchorable`, `MatrixAnchorable` (Task 7's rails toggle their `IsVisible`), and five `UserControl`s whose bodies Tasks 8-11 fill in. `MainWindow.xaml` also gains named slots `TopBarSlot` (row 0) and `StatusBarSlot` (row 2) for Tasks 5 and 6.

This is the highest-risk task in the plan (spec §9). AvalonDock's stock themes look nothing like Rider, so `Docking.xaml` retemplates its panes. **If retheming proves intractable, do not silently substitute a different layout** — report it and the spec's documented fallback (fixed `Grid` columns with correct splitters) is decided by a human.

Tasks 5, 6 and 7 all modify `MainWindow.xaml`, so they must run sequentially after this task. Tasks 8-11 touch only their own `UserControl` files and can run in parallel.

- [ ] **Step 1: Create the five placeholder `UserControl`s**

Each is the same shape. `src/Omen.GUI/Views/ProjectToolWindow.xaml`:

```xml
<UserControl x:Class="Omen.GUI.Views.ProjectToolWindow"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{StaticResource Brush.Surface}">
    <Grid />
</UserControl>
```

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows.Controls;

namespace Omen.GUI.Views;

public partial class ProjectToolWindow : UserControl
{
    public ProjectToolWindow() => InitializeComponent();
}
```

Repeat verbatim for `BuildOutputToolWindow`, `OptionsToolWindow`, `MatrixToolWindow`, and `WelcomePane`, changing only the class name in both files.

- [ ] **Step 2: Create `Themes/Docking.xaml`**

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:avalonDock="https://github.com/Dirkster99/AvalonDock">
    <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Brushes.xaml" />
    </ResourceDictionary.MergedDictionaries>

    <!-- Tool-window tab header: flat, hairline-separated, accent underline when selected. -->
    <Style TargetType="{x:Type avalonDock:LayoutAnchorableTabItem}">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type avalonDock:LayoutAnchorableTabItem}">
                    <Border x:Name="Root" Background="{TemplateBinding Background}"
                            BorderBrush="Transparent" BorderThickness="0,0,0,2"
                            Padding="10,5">
                        <ContentPresenter Content="{Binding Model.Title,
                                          RelativeSource={RelativeSource TemplatedParent}}"
                                          VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Root" Property="Background"
                                    Value="{StaticResource Brush.SurfaceHover}" />
                        </Trigger>
                        <DataTrigger Binding="{Binding Model.IsActive,
                                     RelativeSource={RelativeSource TemplatedParent}}" Value="True">
                            <Setter TargetName="Root" Property="BorderBrush"
                                    Value="{StaticResource Brush.Accent}" />
                            <Setter TargetName="Root" Property="Background"
                                    Value="{StaticResource Brush.SurfaceSelected}" />
                        </DataTrigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Pane chrome and splitters. -->
    <Style TargetType="{x:Type avalonDock:LayoutAnchorablePaneControl}">
        <Setter Property="Background" Value="{StaticResource Brush.Surface}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="BorderThickness" Value="1" />
    </Style>

    <Style TargetType="{x:Type avalonDock:LayoutDocumentPaneControl}">
        <Setter Property="Background" Value="{StaticResource Brush.Surface}" />
        <Setter Property="BorderBrush" Value="{StaticResource Brush.Border}" />
        <Setter Property="BorderThickness" Value="1" />
    </Style>

    <Style TargetType="{x:Type avalonDock:LayoutGridResizerControl}">
        <Setter Property="Background" Value="{StaticResource Brush.Border}" />
    </Style>

</ResourceDictionary>
```

- [ ] **Step 3: Merge `Docking.xaml` into `App.xaml`**

Add after the `Controls.xaml` entry in the merged dictionaries:

```xml
                <ResourceDictionary Source="Themes/Docking.xaml" />
```

- [ ] **Step 4: Build the `MainWindow` layout**

Replace `src/Omen.GUI/Views/MainWindow.xaml`'s `<Grid />` body, and add the namespaces:

```xml
<Window x:Class="Omen.GUI.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:Omen.GUI.ViewModels"
        xmlns:views="clr-namespace:Omen.GUI.Views"
        xmlns:avalonDock="https://github.com/Dirkster99/AvalonDock"
        Title="Omen Build System"
        Width="1400" Height="900"
        MinWidth="900" MinHeight="600"
        Background="{StaticResource Brush.Surface}">
    <Window.DataContext>
        <vm:MainWindowViewModel />
    </Window.DataContext>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="40" />
            <RowDefinition Height="*" />
            <RowDefinition Height="28" />
        </Grid.RowDefinitions>

        <!-- Row 0: top bar (Task 5 fills this slot) -->
        <ContentControl x:Name="TopBarSlot" Grid.Row="0" />

        <!-- Row 1: rails + docking -->
        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>

            <ContentControl x:Name="LeftRailSlot" Grid.Column="0" />

            <avalonDock:DockingManager x:Name="Docking" Grid.Column="1"
                                       Background="{StaticResource Brush.Surface}">
                <avalonDock:LayoutRoot>
                    <avalonDock:LayoutPanel Orientation="Horizontal">

                        <avalonDock:LayoutAnchorablePane DockWidth="280">
                            <avalonDock:LayoutAnchorable x:Name="ProjectAnchorable"
                                                         Title="Project" CanClose="False">
                                <views:ProjectToolWindow />
                            </avalonDock:LayoutAnchorable>
                        </avalonDock:LayoutAnchorablePane>

                        <avalonDock:LayoutPanel Orientation="Vertical">
                            <avalonDock:LayoutDocumentPane>
                                <avalonDock:LayoutDocument Title="Welcome" CanClose="False">
                                    <views:WelcomePane />
                                </avalonDock:LayoutDocument>
                            </avalonDock:LayoutDocumentPane>

                            <avalonDock:LayoutAnchorablePane DockHeight="200">
                                <avalonDock:LayoutAnchorable x:Name="OutputAnchorable"
                                                             Title="Build Output" CanClose="False">
                                    <views:BuildOutputToolWindow />
                                </avalonDock:LayoutAnchorable>
                            </avalonDock:LayoutAnchorablePane>
                        </avalonDock:LayoutPanel>

                        <avalonDock:LayoutAnchorablePane DockWidth="320">
                            <avalonDock:LayoutAnchorable x:Name="OptionsAnchorable"
                                                         Title="Options" CanClose="False">
                                <views:OptionsToolWindow />
                            </avalonDock:LayoutAnchorable>
                            <avalonDock:LayoutAnchorable x:Name="MatrixAnchorable"
                                                         Title="Build Matrix" CanClose="False">
                                <views:MatrixToolWindow />
                            </avalonDock:LayoutAnchorable>
                        </avalonDock:LayoutAnchorablePane>

                    </avalonDock:LayoutPanel>
                </avalonDock:LayoutRoot>
            </avalonDock:DockingManager>

            <ContentControl x:Name="RightRailSlot" Grid.Column="2" />
        </Grid>

        <!-- Row 2: status bar (Task 6 fills this slot) -->
        <ContentControl x:Name="StatusBarSlot" Grid.Row="2" />
    </Grid>
</Window>
```

- [ ] **Step 5: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`
Expected: a window showing four empty docked panes titled Project (left, 280px), Welcome (centre), Build Output (bottom, 200px), and Options/Build Matrix tabbed (right, 320px). Drag a splitter between panes and confirm it **actually resizes** — this is the direct fix for the old app's unusable splitters. Drag a pane's tab out and confirm it floats, then re-dock it. Close the window.

If any of these interactions fail, that is the §9 risk materialising: stop, report `BLOCKED`, and describe exactly what AvalonDock did.

- [ ] **Step 6: Commit**

```bash
git add src/Omen.GUI/Themes/Docking.xaml src/Omen.GUI/Views src/Omen.GUI/App.xaml
git commit -m "feat(gui): add AvalonDock shell with Rider-styled panes and placeholder tool windows"
```

---

### Task 5: Top bar

**Files:**
- Create: `src/Omen.GUI/Controls/TopBar.xaml` + `TopBar.xaml.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.xaml` (fill `TopBarSlot`)
- Create: `src/Omen.GUI/Converters/BoolToVisibilityConverter.cs`
- Create: `src/Omen.GUI/Converters/InverseBoolConverter.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.{ProjectPath, IsBuilding, SelectedPlatform, SelectedConfiguration, AvailablePlatforms, Configurations, BuildAsync(), RebuildAsync(), CleanAsync(), CancelBuild(), LoadProject(string), CloseProject(), GenerateProjectFilesAsync(IdeKind)}`; `MatrixPanelViewModel.IsActive`; icon keys from Task 2.
- Produces: converter keys `Conv.BoolToVisibility` and `Conv.InverseBool`, used by Tasks 6 and 10.

Replaces both the old menu bar and the old toolbar with Rider's single 40px header (spec §4.1).

- [ ] **Step 1: Create the two converters**

`src/Omen.GUI/Converters/BoolToVisibilityConverter.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Omen.GUI.Converters;

/// <summary>
/// true -> Visible, false -> Collapsed. Pass "Invert" as the parameter to reverse it.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter as string == "Invert")
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

`src/Omen.GUI/Converters/InverseBoolConverter.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows.Data;

namespace Omen.GUI.Converters;

/// <summary>
/// Replaces Avalonia's "!" binding operator, which WPF has no equivalent for.
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}
```

Register both in `Themes/Controls.xaml` (add `xmlns:conv="clr-namespace:Omen.GUI.Converters"` to the root element, then append before the closing tag):

```xml
    <conv:BoolToVisibilityConverter x:Key="Conv.BoolToVisibility" />
    <conv:InverseBoolConverter x:Key="Conv.InverseBool" />
```

- [ ] **Step 2: Create `TopBar.xaml`**

```xml
<UserControl x:Class="Omen.GUI.Controls.TopBar"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Height="40">
    <Border Background="{StaticResource Brush.SurfaceRaised}"
            BorderBrush="{StaticResource Brush.Border}"
            BorderThickness="0,0,0,1">
        <Grid Margin="6,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>

            <!-- Burger menu -->
            <Button Grid.Column="0" Style="{StaticResource Style.Button.Icon}"
                    Click="OnMenuClick" ToolTip="Menu">
                <Path Data="{StaticResource Icon.Menu}" Fill="{StaticResource Brush.TextPrimary}"
                      Stretch="Uniform" Width="14" Height="14" />
                <Button.ContextMenu>
                    <ContextMenu x:Name="MainMenu">
                        <MenuItem Header="_Open Project..." Click="OnOpenProjectClick" />
                        <MenuItem Header="_Close Project" Click="OnCloseProjectClick" />
                        <Separator />
                        <MenuItem Header="_Build" Click="OnBuildClick"
                                  IsEnabled="{Binding IsBuilding, Converter={StaticResource Conv.InverseBool}}" />
                        <MenuItem Header="_Rebuild" Click="OnRebuildClick"
                                  IsEnabled="{Binding IsBuilding, Converter={StaticResource Conv.InverseBool}}" />
                        <MenuItem Header="C_lean" Click="OnCleanClick"
                                  IsEnabled="{Binding IsBuilding, Converter={StaticResource Conv.InverseBool}}" />
                        <MenuItem Header="_Cancel" Click="OnCancelClick"
                                  IsEnabled="{Binding IsBuilding}" />
                        <Separator />
                        <MenuItem Header="_Generate Project Files">
                            <MenuItem Header="Visual Studio 2026" Click="OnGenerateVs2026Click" />
                            <MenuItem Header="Visual Studio 2022" Click="OnGenerateVs2022Click" />
                            <MenuItem Header="Visual Studio 2019" Click="OnGenerateVs2019Click" />
                            <Separator />
                            <MenuItem Header="Visual Studio Code" Click="OnGenerateVsCodeClick" />
                            <MenuItem Header="Rider" Click="OnGenerateRiderClick" />
                            <MenuItem Header="CMake" Click="OnGenerateCMakeClick" />
                        </MenuItem>
                        <Separator />
                        <MenuItem Header="E_xit" Click="OnExitClick" />
                    </ContextMenu>
                </Button.ContextMenu>
            </Button>

            <!-- Project chip -->
            <Button Grid.Column="1" Style="{StaticResource Style.Button.Icon}" Width="Auto"
                    Padding="8,4" Click="OnOpenProjectClick" ToolTip="Open project">
                <StackPanel Orientation="Horizontal">
                    <Path Data="{StaticResource Icon.Project}" Fill="{StaticResource Brush.TextSecondary}"
                          Stretch="Uniform" Width="13" Height="13" Margin="0,0,6,0" />
                    <TextBlock Text="{Binding ProjectName}" VerticalAlignment="Center" />
                </StackPanel>
            </Button>

            <!-- Platform / configuration chips: hidden when a build matrix is driving the build -->
            <ComboBox Grid.Column="2" MinWidth="110" Margin="6,0,0,0" VerticalAlignment="Center"
                      ItemsSource="{Binding AvailablePlatforms}"
                      SelectedItem="{Binding SelectedPlatform}"
                      Visibility="{Binding MatrixPanel.IsActive,
                                   Converter={StaticResource Conv.BoolToVisibility},
                                   ConverterParameter=Invert}" />
            <ComboBox Grid.Column="3" MinWidth="120" Margin="6,0,0,0" VerticalAlignment="Center"
                      ItemsSource="{Binding Configurations}"
                      SelectedItem="{Binding SelectedConfiguration}"
                      Visibility="{Binding MatrixPanel.IsActive,
                                   Converter={StaticResource Conv.BoolToVisibility},
                                   ConverterParameter=Invert}" />

            <!-- Action cluster -->
            <StackPanel Grid.Column="5" Orientation="Horizontal" VerticalAlignment="Center">
                <Button Style="{StaticResource Style.Button.Icon}" Click="OnBuildClick" ToolTip="Build"
                        Visibility="{Binding IsBuilding, Converter={StaticResource Conv.BoolToVisibility},
                                     ConverterParameter=Invert}">
                    <Path Data="{StaticResource Icon.Build}" Fill="{StaticResource Brush.Success}"
                          Stretch="Uniform" Width="14" Height="14" />
                </Button>
                <Button Style="{StaticResource Style.Button.Icon}" Click="OnCancelClick" ToolTip="Cancel"
                        Visibility="{Binding IsBuilding, Converter={StaticResource Conv.BoolToVisibility}}">
                    <Path Data="{StaticResource Icon.Cancel}" Fill="{StaticResource Brush.Error}"
                          Stretch="Uniform" Width="14" Height="14" />
                </Button>
                <Button Style="{StaticResource Style.Button.Icon}" Click="OnRebuildClick" ToolTip="Rebuild"
                        IsEnabled="{Binding IsBuilding, Converter={StaticResource Conv.InverseBool}}">
                    <Path Data="{StaticResource Icon.Rebuild}" Fill="{StaticResource Brush.TextPrimary}"
                          Stretch="Uniform" Width="14" Height="14" />
                </Button>
                <Button Style="{StaticResource Style.Button.Icon}" Click="OnCleanClick" ToolTip="Clean"
                        IsEnabled="{Binding IsBuilding, Converter={StaticResource Conv.InverseBool}}">
                    <Path Data="{StaticResource Icon.Clean}" Fill="{StaticResource Brush.TextPrimary}"
                          Stretch="Uniform" Width="14" Height="14" />
                </Button>
            </StackPanel>

            <Button Grid.Column="6" Style="{StaticResource Style.Button.Icon}" Click="OnMenuClick"
                    ToolTip="More">
                <Path Data="{StaticResource Icon.More}" Fill="{StaticResource Brush.TextSecondary}"
                      Stretch="Uniform" Width="14" Height="14" />
            </Button>
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Step 3: Create `TopBar.xaml.cs`**

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Omen.Executors.Orchestration;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Controls;

public partial class TopBar : UserControl
{
    public TopBar() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu })
        {
            menu.PlacementTarget = sender as UIElement;
            menu.IsOpen = true;
        }
        else
        {
            MainMenu.PlacementTarget = sender as UIElement;
            MainMenu.IsOpen = true;
        }
    }

    private void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var dialog = new OpenFolderDialog { Title = "Open Omen Project", Multiselect = false };
        if (dialog.ShowDialog() == true)
            vm.LoadProject(dialog.FolderName);
    }

    private void OnCloseProjectClick(object sender, RoutedEventArgs e) => ViewModel?.CloseProject();

    private void OnExitClick(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

    private async void OnBuildClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.BuildAsync();
    }

    private async void OnRebuildClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.RebuildAsync();
    }

    private async void OnCleanClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CleanAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel?.CancelBuild();

    private async void OnGenerateVs2026Click(object sender, RoutedEventArgs e) => await Generate(IdeKind.VS2026);
    private async void OnGenerateVs2022Click(object sender, RoutedEventArgs e) => await Generate(IdeKind.VS2022);
    private async void OnGenerateVs2019Click(object sender, RoutedEventArgs e) => await Generate(IdeKind.VS2019);
    private async void OnGenerateVsCodeClick(object sender, RoutedEventArgs e) => await Generate(IdeKind.VSCode);
    private async void OnGenerateRiderClick(object sender, RoutedEventArgs e) => await Generate(IdeKind.Rider);
    private async void OnGenerateCMakeClick(object sender, RoutedEventArgs e) => await Generate(IdeKind.CMake);

    private async Task Generate(IdeKind ide)
    {
        if (ViewModel is { } vm) await vm.GenerateProjectFilesAsync(ide);
    }
}
```

`OpenFolderDialog` is the .NET 8 `Microsoft.Win32` folder picker, replacing Avalonia's `StorageProvider.OpenFolderPickerAsync`. `OnOpenProjectClick` deliberately has no `try/catch` here — `LoadProject`'s failure reporting is fixed in Task 12 (defect #3).

- [ ] **Step 4: Add `ProjectName` to `MainWindowViewModel`**

The top bar's project chip shows a folder name, not a full path. In `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`, add next to the other computed members:

```csharp
    /// <summary>Folder name of the open project, for the top bar's project chip.</summary>
    public string ProjectName =>
        string.IsNullOrEmpty(ProjectPath) ? "No project" : Path.GetFileName(ProjectPath.TrimEnd(Path.DirectorySeparatorChar));
```

and add `[NotifyPropertyChangedFor(nameof(ProjectName))]` to the existing `_projectPath` field's attributes.

- [ ] **Step 5: Place the top bar in `MainWindow.xaml`**

Add `xmlns:controls="clr-namespace:Omen.GUI.Controls"` to the `Window` element, then replace the `TopBarSlot` line:

```xml
        <controls:TopBar Grid.Row="0" DataContext="{Binding}" />
```

- [ ] **Step 6: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`
Expected: a 40px header with burger, project chip reading "No project" (or the last project's name), platform/config combos, and the Build/Rebuild/Clean cluster. Click the burger and confirm the menu opens with all items. Close the window.

- [ ] **Step 7: Commit**

```bash
git add src/Omen.GUI/Controls src/Omen.GUI/Converters src/Omen.GUI/Themes/Controls.xaml src/Omen.GUI/Views/MainWindow.xaml src/Omen.GUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat(gui): add the unified Rider-style top bar replacing menubar and toolbar"
```

---

### Task 6: Status bar

**Files:**
- Create: `src/Omen.GUI/Controls/StatusBar.xaml` + `StatusBar.xaml.cs`
- Create: `src/Omen.GUI/Converters/BuildStateToBrushConverter.cs`
- Modify: `src/Omen.GUI/Themes/Controls.xaml` (register the converter)
- Modify: `src/Omen.GUI/Views/MainWindow.xaml` (fill `StatusBarSlot`)

**Interfaces:**
- Consumes: `MainWindowViewModel.{ProjectPath, StatusText, CurrentBuildState, IsBuilding, ProgressValue}`, converters from Task 5.
- Produces: converter key `Conv.BuildStateToBrush`. Completes the removal of the old `StatusDotBrush` view-type leak (spec §6) by doing the state→brush mapping in the view.

- [ ] **Step 1: Create `BuildStateToBrushConverter`**

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Converters;

/// <summary>
/// Maps build state to the status-dot brush. This mapping lives in the view rather than the
/// ViewModel, which previously returned an Avalonia IBrush directly.
/// </summary>
public sealed class BuildStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            MainWindowViewModel.BuildState.Building => "Brush.Accent",
            MainWindowViewModel.BuildState.Success => "Brush.Success",
            MainWindowViewModel.BuildState.Failed => "Brush.Error",
            _ => "Brush.TextSecondary"
        };

        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

Register it in `Themes/Controls.xaml` alongside the other converters:

```xml
    <conv:BuildStateToBrushConverter x:Key="Conv.BuildStateToBrush" />
```

- [ ] **Step 2: Create `StatusBar.xaml`**

```xml
<UserControl x:Class="Omen.GUI.Controls.StatusBar"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Height="28">
    <Border Background="{StaticResource Brush.SurfaceRaised}"
            BorderBrush="{StaticResource Brush.Border}"
            BorderThickness="0,1,0,0">
        <Grid Margin="10,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>

            <TextBlock Grid.Column="0" Style="{StaticResource Style.Text.Secondary}"
                       VerticalAlignment="Center" TextTrimming="CharacterEllipsis"
                       Text="{Binding ProjectPath}" ToolTip="{Binding ProjectPath}" />

            <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center"
                        Margin="12,0">
                <Ellipse Width="8" Height="8" Margin="0,0,6,0"
                         Fill="{Binding CurrentBuildState, Converter={StaticResource Conv.BuildStateToBrush}}" />
                <TextBlock Text="{Binding StatusText}" VerticalAlignment="Center" />
            </StackPanel>

            <ProgressBar Grid.Column="2" Width="180" Height="4" Minimum="0" Maximum="100"
                         Value="{Binding ProgressValue}"
                         Background="{StaticResource Brush.Surface}"
                         Foreground="{StaticResource Brush.Accent}"
                         BorderThickness="0" VerticalAlignment="Center"
                         Visibility="{Binding IsBuilding, Converter={StaticResource Conv.BoolToVisibility}}" />
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Step 3: Create `StatusBar.xaml.cs`**

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows.Controls;

namespace Omen.GUI.Controls;

public partial class StatusBar : UserControl
{
    public StatusBar() => InitializeComponent();
}
```

- [ ] **Step 4: Place it in `MainWindow.xaml`**

Replace the `StatusBarSlot` line:

```xml
        <controls:StatusBar Grid.Row="2" DataContext="{Binding}" />
```

- [ ] **Step 5: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`
Expected: a 28px footer showing the project path on the left and a grey status dot with "No project open" (or the restored project's status) on the right. Close the window.

- [ ] **Step 6: Commit**

```bash
git add src/Omen.GUI/Controls src/Omen.GUI/Converters src/Omen.GUI/Themes/Controls.xaml src/Omen.GUI/Views/MainWindow.xaml
git commit -m "feat(gui): add the status bar and move build-state brush mapping into the view"
```

---

### Task 7: Tool-window rails

**Files:**
- Create: `src/Omen.GUI/Controls/ToolWindowRail.xaml` + `ToolWindowRail.xaml.cs`
- Modify: `src/Omen.GUI/Views/MainWindow.xaml` (fill `LeftRailSlot` and `RightRailSlot`)

**Interfaces:**
- Consumes: the named anchorables from Task 4 (`ProjectAnchorable`, `OutputAnchorable`, `OptionsAnchorable`, `MatrixAnchorable`) and icon keys from Task 2.
- Produces: `ToolWindowRail` with an `Items` property (`ObservableCollection<ToolWindowRailItem>`); `ToolWindowRailItem` with `{ Icon (Geometry), Label (string), Anchorable (LayoutAnchorable) }`.

Rider's vertical icon strips are not an AvalonDock feature — its auto-hide strips are adjacent but styled differently (spec §4.3), so this is our own control.

- [ ] **Step 1: Create `ToolWindowRail.xaml`**

```xml
<UserControl x:Class="Omen.GUI.Controls.ToolWindowRail"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:controls="clr-namespace:Omen.GUI.Controls"
             Width="40">
    <Border Background="{StaticResource Brush.SurfaceRaised}"
            BorderBrush="{StaticResource Brush.Border}"
            BorderThickness="0,0,1,0">
        <ItemsControl ItemsSource="{Binding Items, RelativeSource={RelativeSource AncestorType=controls:ToolWindowRail}}">
            <ItemsControl.ItemTemplate>
                <DataTemplate DataType="{x:Type controls:ToolWindowRailItem}">
                    <Button Style="{StaticResource Style.Button.Icon}" Width="40" Height="40"
                            Click="OnRailItemClick" Tag="{Binding}" ToolTip="{Binding Label}">
                        <Grid>
                            <Border HorizontalAlignment="Left" Width="2" Margin="-6,6,0,6"
                                    Background="{StaticResource Brush.Accent}"
                                    Visibility="{Binding IsActive,
                                                 Converter={StaticResource Conv.BoolToVisibility}}" />
                            <Path Data="{Binding Icon}" Stretch="Uniform" Width="16" Height="16">
                                <Path.Style>
                                    <Style TargetType="Path">
                                        <Setter Property="Fill" Value="{StaticResource Brush.TextSecondary}" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding IsActive}" Value="True">
                                                <Setter Property="Fill" Value="{StaticResource Brush.Accent}" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Path.Style>
                            </Path>
                        </Grid>
                    </Button>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </Border>
</UserControl>
```

- [ ] **Step 2: Create `ToolWindowRail.xaml.cs`**

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvalonDock.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Omen.GUI.Controls;

/// <summary>
/// One icon on a tool-window rail. Wraps an AvalonDock anchorable so the rail can toggle and
/// reflect its visibility.
/// </summary>
public sealed partial class ToolWindowRailItem : ObservableObject
{
    public required Geometry Icon { get; init; }
    public required string Label { get; init; }
    public required LayoutAnchorable Anchorable { get; init; }

    [ObservableProperty]
    private bool _isActive = true;

    public void Toggle()
    {
        Anchorable.IsVisible = !Anchorable.IsVisible;
        IsActive = Anchorable.IsVisible;
    }
}

/// <summary>
/// Rider-style vertical icon strip that shows and hides docked tool windows. AvalonDock's own
/// auto-hide strips do not match this design, so the rail is implemented here.
/// </summary>
public partial class ToolWindowRail : UserControl
{
    public ObservableCollection<ToolWindowRailItem> Items { get; } = [];

    public ToolWindowRail() => InitializeComponent();

    private void OnRailItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToolWindowRailItem item })
            item.Toggle();
    }
}
```

- [ ] **Step 3: Populate the rails in `MainWindow.xaml.cs`**

Replace the body of `src/Omen.GUI/Views/MainWindow.xaml.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Media;
using Omen.GUI.Controls;

namespace Omen.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BuildRails();
    }

    private void BuildRails()
    {
        LeftRail.Items.Add(new ToolWindowRailItem
        {
            Icon = (Geometry)FindResource("Icon.Project"),
            Label = "Project",
            Anchorable = ProjectAnchorable
        });

        LeftRail.Items.Add(new ToolWindowRailItem
        {
            Icon = (Geometry)FindResource("Icon.Output"),
            Label = "Build Output",
            Anchorable = OutputAnchorable
        });

        RightRail.Items.Add(new ToolWindowRailItem
        {
            Icon = (Geometry)FindResource("Icon.Options"),
            Label = "Options",
            Anchorable = OptionsAnchorable
        });

        RightRail.Items.Add(new ToolWindowRailItem
        {
            Icon = (Geometry)FindResource("Icon.Matrix"),
            Label = "Build Matrix",
            Anchorable = MatrixAnchorable
        });
    }
}
```

- [ ] **Step 4: Place the rails in `MainWindow.xaml`**

Replace the two rail slot lines:

```xml
            <controls:ToolWindowRail x:Name="LeftRail" Grid.Column="0" />
```

```xml
            <controls:ToolWindowRail x:Name="RightRail" Grid.Column="2" />
```

On the right rail, flip the border so the hairline faces the content:

```xml
```
Set `BorderThickness="1,0,0,0"` on the right rail by adding to `ToolWindowRail.xaml` a `BorderThicknessOverride` — simplest correct approach: give `ToolWindowRail` a `RailBorderThickness` dependency property defaulting to `0,0,1,0`, bind the `Border`'s `BorderThickness` to it, and set `RailBorderThickness="1,0,0,0"` on the right rail instance. Add to `ToolWindowRail.xaml.cs`:

```csharp
    public static readonly DependencyProperty RailBorderThicknessProperty =
        DependencyProperty.Register(nameof(RailBorderThickness), typeof(Thickness), typeof(ToolWindowRail),
            new PropertyMetadata(new Thickness(0, 0, 1, 0)));

    public Thickness RailBorderThickness
    {
        get => (Thickness)GetValue(RailBorderThicknessProperty);
        set => SetValue(RailBorderThicknessProperty, value);
    }
```

and in `ToolWindowRail.xaml` change the `Border`'s attribute to:

```xml
            BorderThickness="{Binding RailBorderThickness, RelativeSource={RelativeSource AncestorType=controls:ToolWindowRail}}"
```

then set `RailBorderThickness="1,0,0,0"` on the `RightRail` instance in `MainWindow.xaml`.

- [ ] **Step 5: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`
Expected: 40px icon rails on both edges. Click each of the four icons and confirm the corresponding pane hides and shows, and that the icon's accent highlight and left accent bar track its visibility. Close the window.

- [ ] **Step 6: Commit**

```bash
git add src/Omen.GUI/Controls src/Omen.GUI/Views/MainWindow.xaml src/Omen.GUI/Views/MainWindow.xaml.cs
git commit -m "feat(gui): add Rider-style tool-window rails driving AvalonDock pane visibility"
```

---

### Task 8: Project tool window with lazy tree loading

**Files:**
- Modify: `src/Omen.GUI/Views/ProjectToolWindow.xaml` + `.xaml.cs`
- Modify: `src/Omen.GUI/Models/ProjectTreeNode.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.ProjectTreeRoots` (`ObservableCollection<ProjectTreeNode>`).
- Produces: `ProjectTreeNode` gains `IsExpanded` (`bool`, observable) and lazy child population. `ProjectTreeNode.BuildTree(string)` keeps its signature but no longer walks the whole tree.

Fixes defect #5 (spec §7): the current `BuildTree` walks the entire directory tree synchronously on the UI thread from `LoadProject`, which itself runs from the ViewModel constructor. On a NightFox-sized repo this hangs the window on open.

Independent of Tasks 9-11 (separate files); all four require Task 4.

- [ ] **Step 1: Make `ProjectTreeNode` lazy**

Rewrite `src/Omen.GUI/Models/ProjectTreeNode.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Omen.GUI.Models;

/// <summary>
/// One node in the project tree. Children are populated on first expand rather than eagerly:
/// the previous implementation walked the entire directory tree synchronously inside
/// LoadProject, which hangs the window on a large repository.
/// </summary>
public sealed partial class ProjectTreeNode : ObservableObject
{
    private static readonly string[] SkipDirectoryNames =
        ["bin", "obj", ".git", ".vs", "node_modules", "Intermediate", "Binaries"];

    private static readonly string[] RelevantExtensions =
        [".cs", ".cpp", ".c", ".h", ".hpp", ".json", ".xml"];

    private bool _childrenLoaded;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }

    public ObservableCollection<ProjectTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            LoadChildren();
    }

    public static ProjectTreeNode BuildTree(string path)
    {
        var root = new ProjectTreeNode
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            FullPath = path,
            IsDirectory = true
        };

        root.LoadChildren();
        return root;
    }

    /// <summary>
    /// Populates this node's immediate children, once. Each child directory gets a single
    /// placeholder-free entry whose own children load when it is expanded.
    /// </summary>
    public void LoadChildren()
    {
        if (_childrenLoaded || !IsDirectory)
            return;

        _childrenLoaded = true;

        foreach (var directory in EnumerateDirectories())
        {
            var child = new ProjectTreeNode
            {
                Name = Path.GetFileName(directory),
                FullPath = directory,
                IsDirectory = true
            };
            Children.Add(child);
            // Load one level ahead so the expander chevron only appears on non-empty folders.
            child.PeekForExpander();
        }

        foreach (var file in EnumerateFiles())
        {
            Children.Add(new ProjectTreeNode
            {
                Name = Path.GetFileName(file),
                FullPath = file,
                IsDirectory = false
            });
        }
    }

    private void PeekForExpander()
    {
        // Intentionally cheap: only determines whether anything is in there at all.
        if (EnumerateDirectories().Any() || EnumerateFiles().Any())
        {
            Children.Add(new ProjectTreeNode
            {
                Name = "Loading...",
                FullPath = string.Empty,
                IsDirectory = false
            });
            _childrenLoaded = false;
            Children.Clear();
            Children.Add(new ProjectTreeNode
            {
                Name = "Loading...",
                FullPath = string.Empty,
                IsDirectory = false
            });
        }
    }

    private IEnumerable<string> EnumerateDirectories()
    {
        try
        {
            return Directory.EnumerateDirectories(FullPath)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return !name.StartsWith('.') && !SkipDirectoryNames.Contains(name);
                })
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private IEnumerable<string> EnumerateFiles()
    {
        try
        {
            return Directory.EnumerateFiles(FullPath)
                .Where(IsRelevantFile)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool IsRelevantFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.'))
            return false;

        if (name.EndsWith(".target.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".module.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".gem.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        return RelevantExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
}
```

The `PeekForExpander` method above is deliberately rewritten in Step 2 — it is shown here only so the file compiles between steps. Do not leave it in this shape.

- [ ] **Step 2: Replace `PeekForExpander` with a correct placeholder scheme**

The version in Step 1 clears and re-adds redundantly. Replace the whole method, and add the placeholder constant, so the logic is honest and readable:

```csharp
    /// <summary>Marker child that makes the expander chevron appear before real children load.</summary>
    private static ProjectTreeNode CreatePlaceholder() => new()
    {
        Name = "Loading...",
        FullPath = string.Empty,
        IsDirectory = false
    };

    private void PeekForExpander()
    {
        if (!EnumerateDirectories().Any() && !EnumerateFiles().Any())
            return;

        Children.Add(CreatePlaceholder());
        _childrenLoaded = false;
    }
```

and change `LoadChildren`'s guard block to clear the placeholder first:

```csharp
        if (_childrenLoaded || !IsDirectory)
            return;

        _childrenLoaded = true;
        Children.Clear();
```

- [ ] **Step 3: Build the Project tool window**

`src/Omen.GUI/Views/ProjectToolWindow.xaml`:

```xml
<UserControl x:Class="Omen.GUI.Views.ProjectToolWindow"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:models="clr-namespace:Omen.GUI.Models"
             Background="{StaticResource Brush.Surface}">
    <TreeView ItemsSource="{Binding ProjectTreeRoots}" Margin="4">
        <TreeView.ItemContainerStyle>
            <Style TargetType="TreeViewItem" BasedOn="{StaticResource {x:Type TreeViewItem}}">
                <Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}" />
            </Style>
        </TreeView.ItemContainerStyle>
        <TreeView.ItemTemplate>
            <HierarchicalDataTemplate DataType="{x:Type models:ProjectTreeNode}"
                                      ItemsSource="{Binding Children}">
                <TextBlock Text="{Binding Name}" />
            </HierarchicalDataTemplate>
        </TreeView.ItemTemplate>
    </TreeView>
</UserControl>
```

`.xaml.cs` stays as created in Task 4.

- [ ] **Step 4: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`, open `F:\Omen-githubpublic\examples\GemSample` via the top bar's project chip.
Expected: the Project pane fills with the folder's contents. Expand a folder and confirm its children appear on demand. Confirm the window does not freeze while opening.

- [ ] **Step 5: Commit**

```bash
git add src/Omen.GUI/Models/ProjectTreeNode.cs src/Omen.GUI/Views/ProjectToolWindow.xaml
git commit -m "feat(gui): add the Project tool window with lazy tree loading"
```

---

### Task 9: Build Output tool window

**Files:**
- Modify: `src/Omen.GUI/Views/BuildOutputToolWindow.xaml` + `.xaml.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.OutputLines` (`ObservableCollection<OutputLine>`), `OutputLine.{Text, Level}`, `OutputLevelToBrushConverter` (Task 1), `Font.Mono` (Task 2).

Independent of Tasks 8, 10, 11.

- [ ] **Step 1: Build the view**

`src/Omen.GUI/Views/BuildOutputToolWindow.xaml`:

```xml
<UserControl x:Class="Omen.GUI.Views.BuildOutputToolWindow"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:conv="clr-namespace:Omen.GUI.Converters"
             xmlns:models="clr-namespace:Omen.GUI.Models"
             Background="{StaticResource Brush.Surface}">
    <UserControl.Resources>
        <conv:OutputLevelToBrushConverter x:Key="LevelToBrush" />
    </UserControl.Resources>

    <ListBox x:Name="OutputList"
             ItemsSource="{Binding OutputLines}"
             FontFamily="{StaticResource Font.Mono}"
             FontSize="12"
             ScrollViewer.HorizontalScrollBarVisibility="Auto"
             VirtualizingStackPanel.IsVirtualizing="True"
             VirtualizingStackPanel.VirtualizationMode="Recycling">
        <ListBox.ItemContainerStyle>
            <Style TargetType="ListBoxItem">
                <Setter Property="Padding" Value="6,1" />
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="BorderThickness" Value="0" />
            </Style>
        </ListBox.ItemContainerStyle>
        <ListBox.ItemTemplate>
            <DataTemplate DataType="{x:Type models:OutputLine}">
                <TextBlock Text="{Binding Text}"
                           TextWrapping="NoWrap"
                           FontFamily="{StaticResource Font.Mono}"
                           Foreground="{Binding Level, Converter={StaticResource LevelToBrush}}" />
            </DataTemplate>
        </ListBox.ItemTemplate>
    </ListBox>
</UserControl>
```

- [ ] **Step 2: Add auto-scroll in code-behind**

`src/Omen.GUI/Views/BuildOutputToolWindow.xaml.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class BuildOutputToolWindow : UserControl
{
    private INotifyCollectionChanged? _subscribed;

    public BuildOutputToolWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Re-subscribes on DataContext change and unsubscribes from the previous collection. The
    /// Avalonia version subscribed on every DataContext change without ever detaching, leaking
    /// the old ViewModel and double-scrolling.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (DataContext is MainWindowViewModel vm)
        {
            _subscribed = vm.OutputLines;
            _subscribed.CollectionChanged += OnOutputLinesChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_subscribed is null)
            return;

        _subscribed.CollectionChanged -= OnOutputLinesChanged;
        _subscribed = null;
    }

    private void OnOutputLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (OutputList.Items.Count == 0)
            return;

        OutputList.ScrollIntoView(OutputList.Items[^1]);
    }
}
```

This also fixes defect #12 (spec §7).

- [ ] **Step 3: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`, open `examples/GemSample`.
Expected: the bottom Build Output pane shows the rule-compilation lines from the automatic Configure, in monospace, spanning the full window width. Close the window.

- [ ] **Step 4: Commit**

```bash
git add src/Omen.GUI/Views/BuildOutputToolWindow.xaml src/Omen.GUI/Views/BuildOutputToolWindow.xaml.cs
git commit -m "feat(gui): add the Build Output tool window with virtualization and leak-free auto-scroll"
```

---

### Task 10: Options tool window

**Files:**
- Modify: `src/Omen.GUI/Views/OptionsToolWindow.xaml` + `.xaml.cs`
- Modify: `src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs`

**Interfaces:**
- Consumes: `OptionsPanelViewModel.{Options, StatusText, SearchText, ShowAdvanced, GroupByPrefix, FilteredOptions, GroupedOptions, ConfigureAsync(string, IProgress<OrchestratorEvent>?), AddEntryAsync(string, string, BuildOptionType, string), RemoveEntryAsync(BuildOptionViewModel)}`; `BuildOptionViewModel.{Name, Description, Value, IsChanged, IsManual, IsBool, IsString, IsInt, IsPath, IsEnum, IsChecked, NumericValue, AllowedValues}`; `MainWindowViewModel.ConfigureOptionsAsync()`; `PlaceholderTextBox` and `NumericTextBox` (Task 3).
- Produces: `OptionsPanelViewModel.{NewEntryName, NewEntryDescription, NewEntryDefaultValue, NewEntryType, AvailableOptionTypes}` — moving the Add Entry fields out of code-behind (they were named `TextBox`es read directly by the old handler) and adding the type picker.

Fixes defects #6 (hardcoded `String` type), #7 (fields never cleared), #8 (no virtualization), #9 (duplicated template), and #13's filtered-count item.

Independent of Tasks 8, 9, 11.

- [ ] **Step 1: Add the Add Entry state to `OptionsPanelViewModel`**

Add to `src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs`:

```csharp
    /// <summary>Types offerable when adding an option by hand. The backend supports all five;
    /// the previous UI hardcoded String, making the other four unreachable.</summary>
    public IReadOnlyList<BuildOptionType> AvailableOptionTypes { get; } =
        [BuildOptionType.String, BuildOptionType.Bool, BuildOptionType.Int, BuildOptionType.Path];

    [ObservableProperty]
    private string _newEntryName = "";

    [ObservableProperty]
    private string _newEntryDescription = "";

    [ObservableProperty]
    private string _newEntryDefaultValue = "";

    [ObservableProperty]
    private BuildOptionType _newEntryType = BuildOptionType.String;

    /// <summary>
    /// Adds the option described by the NewEntry* properties, then clears them. Enum is not
    /// offered because a manually added option has no way to declare its allowed values.
    /// </summary>
    public async Task AddCurrentEntryAsync()
    {
        if (string.IsNullOrWhiteSpace(NewEntryName))
            return;

        await AddEntryAsync(NewEntryName.Trim(), NewEntryDescription.Trim(), NewEntryType, NewEntryDefaultValue);

        NewEntryName = "";
        NewEntryDescription = "";
        NewEntryDefaultValue = "";
        NewEntryType = BuildOptionType.String;
    }
```

Add `using Omen.Core.Options;` if it is not already present.

- [ ] **Step 2: Report the filtered count in `StatusText`**

In `ConfigureAsync`, the status line currently counts `Options`. Replace the status assignment at the end of the method with a call to a new helper, and call that helper whenever the filters change:

```csharp
    private void UpdateStatusText()
    {
        var shown = FilteredOptions.Count();
        var total = Options.Count;

        StatusText = shown == total
            ? total == 1 ? "1 option" : $"{total} options"
            : $"{shown} of {total} options";
    }
```

Call `UpdateStatusText();` at the end of `ConfigureAsync` (replacing the existing `StatusText = ...` assignment) and from the generated `OnSearchTextChanged`, `OnShowAdvancedChanged` partial methods:

```csharp
    partial void OnSearchTextChanged(string value) => UpdateStatusText();
    partial void OnShowAdvancedChanged(bool value) => UpdateStatusText();
```

- [ ] **Step 3: Build the view with a single shared option template**

`src/Omen.GUI/Views/OptionsToolWindow.xaml`:

```xml
<UserControl x:Class="Omen.GUI.Views.OptionsToolWindow"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:controls="clr-namespace:Omen.GUI.Controls"
             xmlns:vm="clr-namespace:Omen.GUI.ViewModels"
             Background="{StaticResource Brush.Surface}">
    <UserControl.Resources>
        <!-- ONE editor template, used by both the flat and grouped lists. The Avalonia version
             copy-pasted this block, so every change had to be made twice. -->
        <DataTemplate x:Key="OptionEditor" DataType="{x:Type vm:BuildOptionViewModel}">
            <StackPanel Margin="8,6">
                <TextBlock Text="{Binding Name}" FontWeight="SemiBold">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding IsChanged}" Value="True">
                                    <Setter Property="Foreground" Value="{StaticResource Brush.Warning}" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>

                <TextBlock Text="{Binding Description}" Style="{StaticResource Style.Text.Secondary}"
                           TextWrapping="Wrap" Margin="0,1,0,4" />

                <CheckBox Content="Enabled" IsChecked="{Binding IsChecked}"
                          Visibility="{Binding IsBool, Converter={StaticResource Conv.BoolToVisibility}}" />

                <TextBox Text="{Binding Value, UpdateSourceTrigger=PropertyChanged}"
                         Visibility="{Binding IsString, Converter={StaticResource Conv.BoolToVisibility}}" />

                <controls:NumericTextBox Value="{Binding NumericValue, Mode=TwoWay}"
                                         Visibility="{Binding IsInt, Converter={StaticResource Conv.BoolToVisibility}}" />

                <Grid Visibility="{Binding IsPath, Converter={StaticResource Conv.BoolToVisibility}}">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <TextBox Grid.Column="0" Text="{Binding Value, UpdateSourceTrigger=PropertyChanged}" />
                    <Button Grid.Column="1" Content="..." Margin="4,0,0,0" Padding="8,2"
                            Tag="{Binding}" Click="OnBrowsePathClick" />
                </Grid>

                <ComboBox ItemsSource="{Binding AllowedValues}" SelectedItem="{Binding Value}"
                          HorizontalAlignment="Stretch"
                          Visibility="{Binding IsEnum, Converter={StaticResource Conv.BoolToVisibility}}" />

                <Button Content="Remove" HorizontalAlignment="Left" Margin="0,4,0,0"
                        Padding="8,2" FontSize="11" Tag="{Binding}" Click="OnRemoveEntryClick"
                        Visibility="{Binding IsManual, Converter={StaticResource Conv.BoolToVisibility}}" />
            </StackPanel>
        </DataTemplate>
    </UserControl.Resources>

    <DockPanel>
        <!-- Filters -->
        <controls:PlaceholderTextBox DockPanel.Dock="Top" Margin="8,8,8,4"
                                     Placeholder="Search..."
                                     Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}" />

        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="8,0,8,6">
            <CheckBox Content="Advanced" IsChecked="{Binding ShowAdvanced}" Margin="0,0,12,0" />
            <CheckBox Content="Grouped" IsChecked="{Binding GroupByPrefix}" />
        </StackPanel>

        <!-- Add Entry + Configure -->
        <StackPanel DockPanel.Dock="Bottom" Margin="8">
            <TextBlock Text="{Binding StatusText}" Style="{StaticResource Style.Text.Secondary}"
                       Margin="0,0,0,6" />
            <controls:PlaceholderTextBox Placeholder="New entry name" Margin="0,0,0,4"
                                         Text="{Binding NewEntryName, UpdateSourceTrigger=PropertyChanged}" />
            <controls:PlaceholderTextBox Placeholder="Description" Margin="0,0,0,4"
                                         Text="{Binding NewEntryDescription, UpdateSourceTrigger=PropertyChanged}" />
            <controls:PlaceholderTextBox Placeholder="Default value" Margin="0,0,0,4"
                                         Text="{Binding NewEntryDefaultValue, UpdateSourceTrigger=PropertyChanged}" />
            <ComboBox ItemsSource="{Binding AvailableOptionTypes}" SelectedItem="{Binding NewEntryType}"
                      Margin="0,0,0,4" />
            <Button Content="Add Entry" HorizontalAlignment="Stretch" Margin="0,0,0,6"
                    Click="OnAddEntryClick" />
            <Button Content="Configure" HorizontalAlignment="Stretch"
                    Style="{StaticResource Style.Button.Primary}"
                    Click="OnConfigureClick" />
        </StackPanel>

        <!-- Flat list -->
        <ListBox ItemsSource="{Binding FilteredOptions}"
                 ItemTemplate="{StaticResource OptionEditor}"
                 HorizontalContentAlignment="Stretch"
                 VirtualizingStackPanel.IsVirtualizing="True"
                 VirtualizingStackPanel.VirtualizationMode="Recycling"
                 ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                 Visibility="{Binding GroupByPrefix, Converter={StaticResource Conv.BoolToVisibility},
                              ConverterParameter=Invert}">
            <ListBox.ItemContainerStyle>
                <Style TargetType="ListBoxItem">
                    <Setter Property="Background" Value="Transparent" />
                    <Setter Property="BorderThickness" Value="0" />
                    <Setter Property="Padding" Value="0" />
                </Style>
            </ListBox.ItemContainerStyle>
        </ListBox>

        <!-- Grouped list -->
        <ItemsControl ItemsSource="{Binding GroupedOptions}"
                      Visibility="{Binding GroupByPrefix, Converter={StaticResource Conv.BoolToVisibility}}">
            <ItemsControl.Template>
                <ControlTemplate TargetType="ItemsControl">
                    <ScrollViewer VerticalScrollBarVisibility="Auto"
                                  HorizontalScrollBarVisibility="Disabled">
                        <ItemsPresenter />
                    </ScrollViewer>
                </ControlTemplate>
            </ItemsControl.Template>
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <StackPanel Margin="0,4">
                        <TextBlock Text="{Binding Key}" Style="{StaticResource Style.Text.PaneHeader}"
                                   Margin="8,4,8,0" />
                        <ItemsControl ItemsSource="{Binding}"
                                      ItemTemplate="{StaticResource OptionEditor}"
                                      HorizontalContentAlignment="Stretch" />
                    </StackPanel>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </DockPanel>
</UserControl>
```

- [ ] **Step 4: Wire the three handlers**

`src/Omen.GUI/Views/OptionsToolWindow.xaml.cs`:

```csharp
// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class OptionsToolWindow : UserControl
{
    public OptionsToolWindow() => InitializeComponent();

    private OptionsPanelViewModel? ViewModel => DataContext as OptionsPanelViewModel;

    private void OnBrowsePathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BuildOptionViewModel option })
            return;

        var dialog = new OpenFolderDialog
        {
            Title = $"Select value for {option.Name}",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
            option.Value = dialog.FolderName;
    }

    private async void OnAddEntryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.AddCurrentEntryAsync();
    }

    private async void OnRemoveEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BuildOptionViewModel option } && ViewModel is { } vm)
            await vm.RemoveEntryAsync(option);
    }

    private async void OnConfigureClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
            await main.ConfigureOptionsAsync();
    }
}
```

- [ ] **Step 5: Point the pane at the right DataContext**

The Options pane binds to `OptionsPanelViewModel`, not `MainWindowViewModel`. In `MainWindow.xaml`, set the anchorable's content DataContext:

```xml
                                <views:OptionsToolWindow DataContext="{Binding OptionsPanel}" />
```

- [ ] **Step 6: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`, open `examples/GemSample`.
Expected: the Options pane lists `VERBOSE_GREETING` and `LOAD_WINDOW` with checkboxes. Type in the search box and confirm filtering plus a "N of M options" status. Toggle Grouped. Edit a value and confirm the name turns amber. Add an entry with type `Bool`, click Configure, confirm it persists as a checkbox and the three input fields cleared. Remove it.

- [ ] **Step 7: Commit**

```bash
git add src/Omen.GUI/Views/OptionsToolWindow.xaml src/Omen.GUI/Views/OptionsToolWindow.xaml.cs src/Omen.GUI/ViewModels/OptionsPanelViewModel.cs src/Omen.GUI/Views/MainWindow.xaml
git commit -m "feat(gui): add the Options tool window with a shared editor template and entry type picker"
```

---

### Task 11: Build Matrix tool window and Welcome pane

**Files:**
- Modify: `src/Omen.GUI/Views/MatrixToolWindow.xaml`
- Modify: `src/Omen.GUI/Views/WelcomePane.xaml`
- Modify: `src/Omen.GUI/Views/MainWindow.xaml` (matrix DataContext)

**Interfaces:**
- Consumes: `MatrixPanelViewModel.DimensionGroups` (`ObservableCollection<DimensionGroupViewModel>`), `DimensionGroupViewModel.{DisplayName, Values}`, `DimensionValueViewModel.{DisplayName, IsChecked}`; `MainWindowViewModel.{ProjectName, ProjectPath, SelectedPlatform, SelectedConfiguration, StatusText}`.

The Welcome pane fills AvalonDock's document area, which would otherwise be an empty void — Omen has no editor (spec §4.2).

Independent of Tasks 8, 9, 10.

- [ ] **Step 1: Build the matrix view**

`src/Omen.GUI/Views/MatrixToolWindow.xaml`:

```xml
<UserControl x:Class="Omen.GUI.Views.MatrixToolWindow"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Omen.GUI.ViewModels"
             Background="{StaticResource Brush.Surface}">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <ItemsControl ItemsSource="{Binding DimensionGroups}">
            <ItemsControl.ItemTemplate>
                <DataTemplate DataType="{x:Type vm:DimensionGroupViewModel}">
                    <StackPanel Margin="8,6">
                        <TextBlock Text="{Binding DisplayName}" FontWeight="SemiBold" Margin="0,0,0,2" />
                        <ItemsControl ItemsSource="{Binding Values}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate DataType="{x:Type vm:DimensionValueViewModel}">
                                    <CheckBox Content="{Binding DisplayName}"
                                              IsChecked="{Binding IsChecked}"
                                              Margin="12,2,0,0" />
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </StackPanel>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 2: Build the Welcome pane**

`src/Omen.GUI/Views/WelcomePane.xaml`:

```xml
<UserControl x:Class="Omen.GUI.Views.WelcomePane"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{StaticResource Brush.Surface}">
    <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" MaxWidth="420">
        <TextBlock Text="Omen Build System" FontSize="20" FontWeight="SemiBold"
                   HorizontalAlignment="Center" Margin="0,0,0,16" />

        <Grid Margin="0,0,0,6">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="110" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="Project" Style="{StaticResource Style.Text.Secondary}" />
            <TextBlock Grid.Column="1" Text="{Binding ProjectName}" TextTrimming="CharacterEllipsis" />
        </Grid>

        <Grid Margin="0,0,0,6">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="110" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="Path" Style="{StaticResource Style.Text.Secondary}" />
            <TextBlock Grid.Column="1" Text="{Binding ProjectPath}" TextTrimming="CharacterEllipsis"
                       ToolTip="{Binding ProjectPath}" />
        </Grid>

        <Grid Margin="0,0,0,6">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="110" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="Platform" Style="{StaticResource Style.Text.Secondary}" />
            <TextBlock Grid.Column="1" Text="{Binding SelectedPlatform}" />
        </Grid>

        <Grid Margin="0,0,0,6">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="110" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="Configuration" Style="{StaticResource Style.Text.Secondary}" />
            <TextBlock Grid.Column="1" Text="{Binding SelectedConfiguration}" />
        </Grid>

        <Grid Margin="0,10,0,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="110" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="Status" Style="{StaticResource Style.Text.Secondary}" />
            <TextBlock Grid.Column="1" Text="{Binding StatusText}" />
        </Grid>
    </StackPanel>
</UserControl>
```

- [ ] **Step 3: Point the matrix pane at its ViewModel**

In `MainWindow.xaml`:

```xml
                                <views:MatrixToolWindow DataContext="{Binding MatrixPanel}" />
```

- [ ] **Step 4: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`, open `examples/GemSample`.
Expected: the Welcome pane shows project name, path, platform, configuration and status. The Build Matrix tab shows dimension groups if `GemSample` declares a matrix, otherwise an empty pane. Close the window.

- [ ] **Step 5: Commit**

```bash
git add src/Omen.GUI/Views/MatrixToolWindow.xaml src/Omen.GUI/Views/WelcomePane.xaml src/Omen.GUI/Views/MainWindow.xaml
git commit -m "feat(gui): add the Build Matrix tool window and the Welcome document pane"
```

---

### Task 12: ViewModel defect sweep

**Files:**
- Modify: `src/Omen.GUI/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Omen.GUI/ViewModels/MatrixPanelViewModel.cs`
- Modify: `src/Omen.GUI/ViewModels/BuildOptionViewModel.cs`

**Interfaces:**
- Consumes: everything established in Tasks 1-11.
- Produces: no new public members other than `MainWindowViewModel.FindTargetFile()` (private) — this task changes behaviour, not shape.

Fixes the remaining spec §7 defects that live in ViewModels: #2 (Cancel lies during Clean/Generate), #3 (silent catches), #4 (`CloseProject` leaves stale state), #10 (silent no-op returns), #11 (`ProgressValue` never resets), the target-file duplication (#10 in the inventory), and #13's `GroupName` edge case.

Must run after Tasks 8-11 so it is sweeping the final shape of the code.

- [ ] **Step 1: Consolidate target-file discovery**

`MainWindowViewModel` looks up `*.target.cs` in three places. Add one private helper and call it from `ConfigureOptionsAsync`, `ConfigureMatrixAsync`, and `BuildAsync`:

```csharp
    /// <summary>
    /// Finds the project's target file. "A project" means a directory containing at least one
    /// *.target.cs; this lookup was previously duplicated at three call sites.
    /// </summary>
    private string? FindTargetFile()
    {
        if (string.IsNullOrEmpty(ProjectPath))
            return null;

        try
        {
            return Directory
                .GetFiles(ProjectPath, "*.target.cs", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppendLine($"Could not scan '{ProjectPath}' for a .target.cs file: {ex.Message}",
                OrchestratorEventLevel.Error);
            return null;
        }
    }
```

- [ ] **Step 2: Make Cancel honest**

`_buildCts` is only created by `BuildAsync`, yet `CleanAsync` and `GenerateProjectFilesAsync` set `IsBuilding = true`, which enables a Cancel button whose handler does nothing.

`CleanOrchestrator.CleanAsync` and `ProjectGenerationOrchestrator.GenerateAsync` take no `CancellationToken` (spec's frozen seam — do not add one). So Cancel must not be *offered* for them. Add a separate flag:

```csharp
    /// <summary>
    /// True only while an operation that can actually be cancelled is running. Clean and
    /// project generation set IsBuilding but are not cancellable, so Cancel is hidden for them
    /// rather than shown and inert.
    /// </summary>
    [ObservableProperty]
    private bool _isCancellable;
```

Set `IsCancellable = true` immediately after `_buildCts = new CancellationTokenSource();` in `BuildAsync`, and `IsCancellable = false;` in that method's `finally` block. Do not set it in `CleanAsync` or `GenerateProjectFilesAsync`.

Then in `src/Omen.GUI/Controls/TopBar.xaml`, change both Cancel-related bindings from `IsBuilding` to `IsCancellable`:

```xml
                <Button Style="{StaticResource Style.Button.Icon}" Click="OnBuildClick" ToolTip="Build"
                        Visibility="{Binding IsCancellable, Converter={StaticResource Conv.BoolToVisibility},
                                     ConverterParameter=Invert}">
```

```xml
                <Button Style="{StaticResource Style.Button.Icon}" Click="OnCancelClick" ToolTip="Cancel"
                        Visibility="{Binding IsCancellable, Converter={StaticResource Conv.BoolToVisibility}}">
```

and the burger menu's Cancel item:

```xml
                        <MenuItem Header="_Cancel" Click="OnCancelClick"
                                  IsEnabled="{Binding IsCancellable}" />
```

- [ ] **Step 3: Report instead of returning silently**

`BuildAsync`, `CleanAsync`, and `GenerateProjectFilesAsync` each return silently when no project is open. Replace each guard with a reported one. In `BuildAsync`:

```csharp
        if (string.IsNullOrEmpty(ProjectPath))
        {
            AppendLine("No project is open. Use the project chip to open one.", OrchestratorEventLevel.Warning);
            return;
        }

        if (!MatrixPanel.IsActive && SelectedPlatform is null)
        {
            AppendLine("No platform is selected.", OrchestratorEventLevel.Warning);
            return;
        }

        var targetFile = FindTargetFile();
        if (targetFile is null)
        {
            AppendLine("No .target.cs file found in this project.", OrchestratorEventLevel.Error);
            return;
        }
```

Apply the same pattern to `CleanAsync` and `GenerateProjectFilesAsync` (project-open check only — neither needs a target file).

- [ ] **Step 4: Reset progress on completion**

In `BuildAsync`'s `finally` block, alongside clearing `_buildCts`:

```csharp
            ProgressValue = 0;
```

- [ ] **Step 5: Clear all state in `CloseProject`**

Replace the body of `CloseProject`:

```csharp
    public void CloseProject()
    {
        ProjectTreeRoots.Clear();
        OutputLines.Clear();
        OptionsPanel.Reset();
        MatrixPanel.Reset();
        ProjectPath = null;
        StatusText = "No project open";
        CurrentBuildState = BuildState.Idle;
        ProgressValue = 0;
    }
```

Add `Reset()` to `OptionsPanelViewModel`:

```csharp
    /// <summary>
    /// Drops all option state, including the remembered target file. Without this, Add/Remove
    /// Entry would keep writing to the project that was just closed.
    /// </summary>
    public void Reset()
    {
        Options.Clear();
        _lastTargetFile = null;
        SearchText = "";
        StatusText = "";
        OnPropertyChanged(nameof(FilteredOptions));
        OnPropertyChanged(nameof(GroupedOptions));
    }
```

and to `MatrixPanelViewModel`:

```csharp
    /// <summary>Drops all matrix state so a closed project's dimensions do not linger.</summary>
    public void Reset()
    {
        DimensionGroups.Clear();
        IsActive = false;
    }
```

- [ ] **Step 6: Report the previously-silent failures**

Three `catch` blocks swallow real failures with no user feedback. In `MatrixPanelViewModel.ResolveAsync`, the rule-compilation `catch` currently does `return;`. Give the class an optional reporter and use it:

```csharp
    /// <summary>
    /// Set by MainWindowViewModel so matrix-resolution failures reach the output pane instead
    /// of vanishing. The old code returned silently on any rule-compilation error.
    /// </summary>
    public Action<string, OrchestratorEventLevel>? Report { get; set; }
```

and in the `catch`:

```csharp
        catch (Exception ex)
        {
            Report?.Invoke($"Could not resolve the build matrix: {ex.Message}", OrchestratorEventLevel.Warning);
            return;
        }
```

Wire it in `MainWindowViewModel`'s constructor, after `MatrixPanel` is created:

```csharp
        MatrixPanel.Report = AppendLine;
```

`AppendLine` is currently `private`; leave it private and assign the method group — that compiles because the assignment happens inside the declaring class.

In `MainWindowViewModel.LoadProject`, wrap the tree build so a failure is reported rather than swallowed by the caller:

```csharp
    public void LoadProject(string path)
    {
        try
        {
            ProjectTreeRoots.Clear();
            ProjectTreeRoots.Add(ProjectTreeNode.BuildTree(path));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppendLine($"Could not read '{path}': {ex.Message}", OrchestratorEventLevel.Error);
            return;
        }

        ProjectPath = path;
        StatusText = $"Project: {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}";
        _settings.LastProjectPath = path;
        _settings.Save();

        _ = ConfigureOptionsAsync();
        _ = ConfigureMatrixAsync();
    }
```

The Add/Remove Entry handlers in `OptionsToolWindow.xaml.cs` (Task 10) have no `catch` at all, so their failures already surface — no silent swallow to fix there.

- [ ] **Step 7: Fix the `GroupName` edge case**

In `BuildOptionViewModel`, a name beginning with `_` currently yields an empty group header. Replace the property:

```csharp
    /// <summary>
    /// Group header for the grouped options view: the name's prefix before its first
    /// underscore. A leading underscore would otherwise produce an empty header.
    /// </summary>
    public string GroupName
    {
        get
        {
            var index = Name.IndexOf('_');
            return index > 0 ? Name[..index] : Name;
        }
    }
```

- [ ] **Step 8: Build and run**

Run: `dotnet build F:\Omen-githubpublic\Omen.sln -c Debug`
Expected: 0 errors.

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`.
Expected, with no project open: clicking Build writes "No project is open..." to the Build Output pane instead of doing nothing silently. Open `examples/GemSample`, then use the burger menu's Close Project — confirm the Options pane, Build Matrix pane and Build Output all clear. Close the window.

- [ ] **Step 9: Commit**

```bash
git add src/Omen.GUI/ViewModels src/Omen.GUI/Controls/TopBar.xaml
git commit -m "fix(gui): honest Cancel, reported failures, full CloseProject reset, progress reset"
```

---

### Task 13: Remove the last Avalonia traces and run the manual acceptance gate

**Files:**
- Modify: `Directory.Packages.props` (verify no Avalonia entries remain)
- Delete: `src/Omen.GUI/Styles/` (if the empty directory survives)
- Create: `docs/superpowers/plans/2026-08-19-omen-gui-wpf-acceptance.md`

**Interfaces:**
- Consumes: the finished application.
- Produces: a filled-in acceptance record.

The previous GUI plan shipped with nobody having ever run the app. This task is the gate that stops that recurring (spec §8), and **its checklist must actually be executed by a human, not asserted**.

- [ ] **Step 1: Verify no Avalonia references survive**

Run:

```bash
cd F:\Omen-githubpublic
grep -ri "avalonia" --include=*.cs --include=*.csproj --include=*.xaml --include=*.props src/ | grep -vi avalondock
```

Expected: no output. (`AvalonDock` matches the substring "avalon" but is the docking library, hence the filter.) If anything is listed, remove it before continuing.

Run:

```bash
git status --short
ls src/Omen.GUI/Styles 2>/dev/null && git rm -r src/Omen.GUI/Styles
```

- [ ] **Step 2: Full clean build**

Run:

```bash
dotnet clean F:\Omen-githubpublic\Omen.sln -c Debug
dotnet build F:\Omen-githubpublic\Omen.sln -c Debug
```

Expected: 0 errors. Record the warning count in the task report.

- [ ] **Step 3: Confirm the rest of the solution still passes**

Run:

```bash
dotnet test F:\Omen-githubpublic\tests\Omen.Core.Tests\Omen.Core.Tests.csproj -c Debug
dotnet test F:\Omen-githubpublic\tests\Omen.Executors.Tests\Omen.Executors.Tests.csproj -c Debug
```

Expected: all pass. These cover the backend seam the GUI depends on; a failure here means the port touched something it should not have.

- [ ] **Step 4: Create the acceptance checklist document**

Create `docs/superpowers/plans/2026-08-19-omen-gui-wpf-acceptance.md`:

```markdown
# Omen.GUI WPF — manual acceptance record

Run: `dotnet run --project F:\Omen-githubpublic\src\Omen.GUI -c Debug`
Project under test: `F:\Omen-githubpublic\examples\GemSample`

Mark each PASS/FAIL with a note. An unfilled checklist is not an acceptance.

## Shell
- [ ] Window opens at 1400x900 with the dark Rider-style surface, no visual artefacts.
- [ ] Top bar shows burger, project chip, platform + config combos, Build/Rebuild/Clean.
- [ ] Status bar shows project path (left) and status dot + text (right).
- [ ] Left rail shows Project and Build Output icons; right rail shows Options and Build Matrix.

## Docking (the old app's "buggy layout")
- [ ] Dragging the splitter between Project and the centre pane actually resizes it.
- [ ] Dragging the splitter above Build Output actually resizes it.
- [ ] Dragging the splitter beside Options actually resizes it.
- [ ] A pane can be dragged out to float, and re-docked.
- [ ] Each rail icon hides and shows its pane, and the accent highlight tracks visibility.

## Project
- [ ] Opening GemSample fills the Project tree; the window does not freeze.
- [ ] Expanding a folder loads its children on demand.

## Build Output
- [ ] Configure output appears in monospace across the full window width.
- [ ] Output auto-scrolls to the newest line.

## Options
- [ ] VERBOSE_GREETING and LOAD_WINDOW appear as checkboxes.
- [ ] Search filters the list; the status reads "N of M options" while filtered.
- [ ] The Advanced checkbox reveals an advanced option (add one temporarily if none exists).
- [ ] The Grouped checkbox switches to prefix-grouped headers.
- [ ] Editing a value turns its name amber; Configure clears the amber.
- [ ] Add Entry with type Bool creates a checkbox option, and the input fields clear.
- [ ] The added entry survives a Configure click.
- [ ] Remove appears only on the manually added entry, and removes it.
- [ ] A Path-type option's "..." button opens a folder picker and assigns the chosen path.

## Build
- [ ] Build runs, output streams, progress bar advances, status dot turns green on success.
- [ ] Cancel appears only while a build is running and actually stops it.
- [ ] Cancel is NOT offered during Clean or Generate Project Files.
- [ ] With no project open, Build reports "No project is open..." rather than doing nothing.

## Close
- [ ] Close Project clears the tree, options, matrix and output.
```

- [ ] **Step 5: Execute the checklist**

Run the application and work through every item, filling in the results. **Do not mark this task complete on a build-clean result alone** — if the checklist cannot be executed in the current environment (no display access), report `DONE_WITH_CONCERNS` naming exactly which items are unverified, so a human runs them before merge.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore(gui): remove remaining Avalonia traces and record the WPF acceptance checklist"
```

---

## Self-review notes

- **Spec coverage:** §1 project structure → Tasks 1-4 create every listed directory. §2 carry-over and the frozen backend seam → Task 1 (ViewModels ported untouched) and Global Constraints. §3 visual system → Task 2 (all 13 colour tokens, metrics, typography). §4.1 top bar → Task 5. §4.2 docking → Task 4. §4.3 rails → Task 7. §4.4 status bar → Task 6. §5 feature checklist → Tasks 5, 8, 9, 10, 11 collectively cover every listed control. §6 ViewModel changes → Task 1 (`StatusDotBrush` removal), Task 10 (`NewEntryType`), Task 7 (rail binding surface), Task 12 (target-file consolidation). §7's 13 defects → #1 Task 4, #2 Task 12, #3 Task 12, #4 Task 12, #5 Task 8, #6 Task 10, #7 Task 10, #8 Tasks 9+10, #9 Task 10, #10 Task 12, #11 Task 12, #12 Task 9, #13 Tasks 10+12. §8 testing → Task 13. The spec's explicit non-goals (new features, light theme, non-Windows, dock-layout persistence) have no task, matching the spec.
- **Placeholder scan:** every code step contains the actual code. Two places deliberately show a first version then correct it rather than hiding the reasoning: Task 8's `PeekForExpander` (Step 1 shows a version that compiles, Step 2 replaces it with the honest one) and Task 7's rail border thickness (introduced as an attribute, then given a real dependency property). Both are explicit instructions, not TODOs.
- **Type consistency:** `MainWindowViewModel.BuildState` is referenced identically in Task 1 (removal of `StatusDotBrush`) and Task 6 (`BuildStateToBrushConverter`). `ToolWindowRailItem.{Icon, Label, Anchorable, IsActive, Toggle()}` is defined once in Task 7 and used only there. `OptionsPanelViewModel.{NewEntryName, NewEntryDescription, NewEntryDefaultValue, NewEntryType, AvailableOptionTypes, AddCurrentEntryAsync(), Reset(), UpdateStatusText()}` are defined in Tasks 10 and 12 and consumed by Task 10's XAML. `Conv.BoolToVisibility`, `Conv.InverseBool`, `Conv.BuildStateToBrush` are registered in Tasks 5 and 6 and used consistently thereafter. `FindTargetFile()` is introduced in Task 12 Step 1 and used by Steps 3's guards.
- **Known sequencing constraints:** Tasks 1→2→3→4 are strictly sequential. Tasks 5, 6, 7 all modify `MainWindow.xaml` and must run in order after Task 4. Tasks 8, 9, 10, 11 touch disjoint files and may run in parallel after Task 4 (Task 10 additionally needs Task 3's controls and Task 5's converters). Task 12 must run after 8-11. Task 13 is last.
