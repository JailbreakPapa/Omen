// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Omen.Executors.Orchestration;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();

        // DataContext is assigned by the caller's object initializer after this constructor
        // runs (see App.axaml.cs), so it isn't available yet here - defer the subscription
        // until it actually arrives.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                ((INotifyCollectionChanged)vm.OutputLines).CollectionChanged += OnOutputLinesChanged;
        };
    }

    private void OnOutputLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.OutputLines.Count == 0) return;
        OutputListBox.ScrollIntoView(ViewModel.OutputLines[^1]);
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
            try
            {
                ViewModel.LoadProject(path);
            }
            catch
            {
                // Last-resort guard: an unexpected failure anywhere in the load path must not
                // crash the app. No UI messaging yet (output pane arrives in Task 8) - leave
                // the project unopened.
            }
        }
    }

    private void OnCloseProjectClick(object? sender, RoutedEventArgs e) => ViewModel.CloseProject();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnBuildClick(object? sender, RoutedEventArgs e) => await ViewModel.BuildAsync();

    private async void OnRebuildClick(object? sender, RoutedEventArgs e) => await ViewModel.RebuildAsync();

    private async void OnCleanClick(object? sender, RoutedEventArgs e) => await ViewModel.CleanAsync();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => ViewModel.CancelBuild();

    private async void OnGenerateVS2022Click(object? sender, RoutedEventArgs e) => await ViewModel.GenerateProjectFilesAsync(IdeKind.VS2022);

    private async void OnGenerateVS2019Click(object? sender, RoutedEventArgs e) => await ViewModel.GenerateProjectFilesAsync(IdeKind.VS2019);

    private async void OnGenerateVSCodeClick(object? sender, RoutedEventArgs e) => await ViewModel.GenerateProjectFilesAsync(IdeKind.VSCode);

    private async void OnGenerateCMakeClick(object? sender, RoutedEventArgs e) => await ViewModel.GenerateProjectFilesAsync(IdeKind.CMake);
}
