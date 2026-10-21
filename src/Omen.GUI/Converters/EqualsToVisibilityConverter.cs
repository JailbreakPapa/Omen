// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Omen.GUI.Converters;

/// <summary>
/// Visible when the bound value's string form equals the ConverterParameter, Collapsed otherwise.
/// Used to show/hide the Settings dialog's category rows and pages by name.
/// </summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
