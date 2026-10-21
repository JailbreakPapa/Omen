// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System;
using System.Windows;

namespace Omen.GUI.Services;

/// <summary>
/// Swaps the merged palette and metric dictionaries at runtime. Brushes and metrics are
/// consumed via DynamicResource throughout, so replacing the dictionary they resolve
/// against re-themes the live window without a restart. Requires App.xaml to merge
/// Themes/Colors.xaml and Themes/Metrics.xaml exactly once, at the top level (verified:
/// it does, as of this task).
/// </summary>
public static class ThemeService
{
    public static void ApplyTheme(string theme) =>
        Swap("Themes/Colors", theme == "Light" ? "Themes/Colors.Light.xaml" : "Themes/Colors.xaml");

    public static void ApplyDensity(string density) =>
        Swap("Themes/Metrics", density switch
        {
            "Compact" => "Themes/Metrics.Compact.xaml",
            "Roomy" => "Themes/Metrics.Roomy.xaml",
            _ => "Themes/Metrics.xaml"
        });

    private static void Swap(string prefix, string newSource)
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null) return;

        for (var i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.OriginalString;
            if (src is null || !src.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            merged[i] = new ResourceDictionary { Source = new Uri(newSource, UriKind.Relative) };
            return;
        }
    }
}
