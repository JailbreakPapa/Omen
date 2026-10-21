// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Omen.Executors.Orchestration;

namespace Omen.GUI.Converters;

/// <summary>
/// Maps an orchestrator event level to the themed brush the build-output list draws it in.
/// Brushes are resolved from the application resource dictionary by key so that all colour
/// values stay in Themes/Colors.xaml.
/// </summary>
public sealed class OutputLevelToBrushConverter : IValueConverter
{
    public static readonly OutputLevelToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            OrchestratorEventLevel.Error => "Brush.Error",
            OrchestratorEventLevel.Warning => "Brush.Warning",
            OrchestratorEventLevel.Success => "Brush.Success",
            _ => "Brush.TextPrimary"
        };

        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
