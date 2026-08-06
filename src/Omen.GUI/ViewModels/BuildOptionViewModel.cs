// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Core.Options;

namespace Omen.GUI.ViewModels;

/// <summary>
/// An editable wrapper around a discovered BuildOptionDeclaration. The underlying storage is
/// always a string (Value) - IsChecked/NumericValue are typed views onto it for the widgets
/// that need a bool?/decimal? rather than a string (CheckBox, NumericUpDown).
/// </summary>
public sealed partial class BuildOptionViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required BuildOptionType Type { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked))]
    [NotifyPropertyChangedFor(nameof(NumericValue))]
    private string _value = "";

    public bool IsBool => Type == BuildOptionType.Bool;
    public bool IsString => Type == BuildOptionType.String;
    public bool IsInt => Type == BuildOptionType.Int;
    public bool IsPath => Type == BuildOptionType.Path;

    public bool? IsChecked
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value == true ? "true" : "false";
    }

    public decimal? NumericValue
    {
        get => decimal.TryParse(Value, out var parsed) ? parsed : 0m;
        set => Value = ((long)(value ?? 0)).ToString();
    }
}
