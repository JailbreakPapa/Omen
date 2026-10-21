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

