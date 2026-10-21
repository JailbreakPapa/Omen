// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Omen.Executors.Orchestration;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Controls;

public partial class MenuBar : UserControl
{
    public MenuBar() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

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

    private async void OnGenerateVs2022Click(object sender, RoutedEventArgs e) => await Generate(IdeKind.VS2022);
    private async void OnGenerateVs2019Click(object sender, RoutedEventArgs e) => await Generate(IdeKind.VS2019);
    private async void OnGenerateVsCodeClick(object sender, RoutedEventArgs e) => await Generate(IdeKind.VSCode);
    private async void OnGenerateCMakeClick(object sender, RoutedEventArgs e) => await Generate(IdeKind.CMake);

    private async Task Generate(IdeKind ide)
    {
        if (ViewModel is { } vm) await vm.GenerateProjectFilesAsync(ide);
    }

    // Tools/Help: no backing implementation yet. Report explicitly rather than doing nothing.
    private void OnOmenNetCoordinatorClick(object sender, RoutedEventArgs e) =>
        ViewModel?.ReportNotImplemented("OmenNet Coordinator is not wired up yet.");

    private void OnOmenNetAgentClick(object sender, RoutedEventArgs e) =>
        ViewModel?.ReportNotImplemented("OmenNet Agent is not wired up yet.");

    private void OnClearRuleCacheClick(object sender, RoutedEventArgs e) =>
        ViewModel?.ReportNotImplemented("Clear Rule Cache is not wired up yet.");

    private void OnDocumentationClick(object sender, RoutedEventArgs e) =>
        ViewModel?.OpenDocumentation();

    private void OnAboutOmenClick(object sender, RoutedEventArgs e) =>
        ViewModel?.OpenAbout();

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) window.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

    /// <summary>Swaps the maximize-button glyph/tooltip to reflect the window's current state.</summary>
    public void SetMaximized(bool isMaximized)
    {
        MaximizeGlyph.UriSource = new Uri(
            $"pack://application:,,,/Assets/Icons/{(isMaximized ? "window-restore" : "window-maximize")}.svg");
        MaximizeButton.ToolTip = isMaximized ? "Restore Down" : "Maximize";
    }
}

