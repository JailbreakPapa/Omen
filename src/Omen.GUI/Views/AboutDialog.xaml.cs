// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class AboutDialog : UserControl
{
    public AboutDialog() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnCloseClick(object sender, RoutedEventArgs e) => ViewModel?.CloseAbout();

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => ViewModel?.CloseAbout();

    /// <summary>Keeps a click inside the panel from reaching the scrim and dismissing it.</summary>
    private void OnPanelClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnDocumentationClick(object sender, RoutedEventArgs e) => ViewModel?.OpenDocumentation();
}
