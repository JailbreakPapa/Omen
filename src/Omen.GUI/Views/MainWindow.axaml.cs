// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Omen Project",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.TryGetLocalPath() is { } path)
        {
            ViewModel.LoadProject(path);
        }
    }

    private void OnCloseProjectClick(object? sender, RoutedEventArgs e) => ViewModel.CloseProject();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnBuildClick(object? sender, RoutedEventArgs e)
    {
        // Task 8: calls ViewModel.BuildAsync().
    }

    private void OnRebuildClick(object? sender, RoutedEventArgs e)
    {
        // Task 8: calls ViewModel.RebuildAsync().
    }

    private void OnCleanClick(object? sender, RoutedEventArgs e)
    {
        // Task 8: calls ViewModel.CleanAsync().
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        // Task 8: calls ViewModel.CancelBuild().
    }

    private void OnGenerateVS2022Click(object? sender, RoutedEventArgs e)
    {
        // Task 9.
    }

    private void OnGenerateVS2019Click(object? sender, RoutedEventArgs e)
    {
        // Task 9.
    }

    private void OnGenerateVSCodeClick(object? sender, RoutedEventArgs e)
    {
        // Task 9.
    }

    private void OnGenerateCMakeClick(object? sender, RoutedEventArgs e)
    {
        // Task 9.
    }
}
