// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views.Settings;

public partial class SettingsGeneralPage : UserControl
{
    public SettingsGeneralPage() => InitializeComponent();

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Default Project Folder", Multiselect = false };

        if (dialog.ShowDialog() == true && ViewModel is { } vm)
            vm.DefaultProjectFolder = dialog.FolderName;
    }
}
