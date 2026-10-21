// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Controls;

public partial class Toolbar : UserControl
{
    public Toolbar() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var dialog = new OpenFolderDialog { Title = "Open Omen Project", Multiselect = false };
        if (dialog.ShowDialog() == true)
            vm.LoadProject(dialog.FolderName);
    }

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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
            main.OpenSettings();
    }
}
