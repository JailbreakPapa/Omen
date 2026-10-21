// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows.Controls;
using Omen.GUI.Services;

namespace Omen.GUI.Views.Settings;

public partial class SettingsAppearancePage : UserControl
{
    public SettingsAppearancePage() => InitializeComponent();

    // Thin dispatch into ThemeService (a service class, not view-model logic) so the theme
    // previews live the instant the user picks it, matching Task 17's brief. The "proper" home
    // for this is a SettingsViewModel.OnThemeChanged/OnUiDensityChanged partial hook - out of
    // scope here since this task may not edit the settings view model; see the task report.
    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is string theme)
            ThemeService.ApplyTheme(theme);
    }

    private void OnDensitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DensityCombo.SelectedItem is string density)
            ThemeService.ApplyDensity(density);
    }
}
