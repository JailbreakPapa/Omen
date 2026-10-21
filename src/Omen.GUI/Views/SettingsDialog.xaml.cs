// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class SettingsDialog : UserControl
{
    public SettingsDialog() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnBackdropMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ViewModel?.CloseSettings();

    // Stops a click inside the dialog from bubbling up to the backdrop's MouseDown and closing it.
    private void OnDialogMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        e.Handled = true;

    private void OnCloseClick(object sender, RoutedEventArgs e) => ViewModel?.CloseSettings();

    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel?.CloseSettings();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        vm.Settings.Save();
        vm.CloseSettings();
    }
}
