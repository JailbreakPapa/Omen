// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Omen.Executors.Orchestration;

namespace Omen.GUI.Converters;

public sealed class OutputLevelToBrushConverter : IValueConverter
{
    public static readonly OutputLevelToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        OrchestratorEventLevel.Error => Brushes.OrangeRed,
        OrchestratorEventLevel.Warning => Brushes.Goldenrod,
        OrchestratorEventLevel.Success => Brushes.LimeGreen,
        _ => Brushes.LightGray
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
