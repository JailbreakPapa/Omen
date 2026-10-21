// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows.Data;

namespace Omen.GUI.Converters;

/// <summary>Uppercases a string for display (group headers). WPF has no CSS text-transform.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(culture) ?? value ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

