// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Converters;

/// <summary>
/// Maps build state to the status-dot brush. This mapping lives in the view rather than the
/// ViewModel, which previously returned an Avalonia IBrush directly.
/// </summary>
public sealed class BuildStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            MainWindowViewModel.BuildState.Building => "Brush.Accent",
            MainWindowViewModel.BuildState.Success => "Brush.Success",
            MainWindowViewModel.BuildState.Failed => "Brush.Error",
            _ => "Brush.TextSecondary"
        };

        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
