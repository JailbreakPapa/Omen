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
