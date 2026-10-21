// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class UnsavedChangesDialog : UserControl
{
    public UnsavedChangesDialog() => InitializeComponent();

    private RuleEditorPanelViewModel? ViewModel => DataContext as RuleEditorPanelViewModel;

    // Stops a click inside the dialog from bubbling to nothing in particular - kept for
    // symmetry with SettingsDialog's own backdrop-guard, even though this dialog has no
    // backdrop-dismiss to guard against.
    private void OnDialogMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel?.CancelClose();

    private void OnDontSaveClick(object sender, RoutedEventArgs e) => ViewModel?.ConfirmCloseDiscard();

    private void OnSaveClick(object sender, RoutedEventArgs e) => ViewModel?.ConfirmCloseSave();
}
