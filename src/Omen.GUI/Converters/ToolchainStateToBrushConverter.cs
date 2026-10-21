// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Converters;

/// <summary>
/// Maps a toolchain's detection state to the status-text brush.
/// </summary>
public sealed class ToolchainStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ToolchainState.Detected => "Brush.Success",
            ToolchainState.NotFound => "Brush.Error",
            ToolchainState.Unavailable => "Brush.TextDim",
            _ => "Brush.TextSecondary"
        };

        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
