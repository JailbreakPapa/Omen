// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.GUI.ViewModels;

public sealed partial class DimensionValueViewModel : ViewModelBase
{
    public required Enum Value { get; init; }
    public string DisplayName => Value.ToString();

    [ObservableProperty]
    private bool _isChecked = true;
}

public sealed partial class DimensionGroupViewModel : ViewModelBase
{
    public required Type DimensionType { get; init; }
    public required string DisplayName { get; init; }
    public ObservableCollection<DimensionValueViewModel> Values { get; } = [];
}

/// <summary>
/// Resolves a target's declared TargetMatrix (if any) into one checkbox group per varying
/// dimension - unchecking a value is equivalent to a CLI --dim/--platform exclusion on that
/// value. For a target without a declared matrix, IsActive stays false and the GUI's
/// existing single-combination Platform/Configuration ComboBoxes are what's shown instead
/// (see MainWindow.axaml).
/// </summary>
public sealed partial class MatrixPanelViewModel : ViewModelBase
{
    private static readonly Type[] BuiltInOrder = [typeof(TargetPlatform), typeof(TargetArchitecture), typeof(BuildConfiguration)];

    public ObservableCollection<DimensionGroupViewModel> DimensionGroups { get; } = [];

    [ObservableProperty]
    private bool _isActive;

    public async Task ResolveAsync(string targetFile)
    {
        DimensionGroups.Clear();
        IsActive = false;

        var workingDir = Path.GetDirectoryName(targetFile) ?? Path.GetPathRoot(targetFile) ?? targetFile;
        var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));
        CompiledRules compiledRules;
        try
        {
            compiledRules = await ruleCompiler.CompileRulesAsync(workingDir);
        }
        catch
        {
            return; // Configure/Build's own error reporting already surfaces rule-compile failures.
        }

        var targetType = compiledRules.FindTargetType();
        if (targetType == null) return;

        var matrix = compiledRules.GetSupportedTargets(targetType);
        if (matrix == null) return;

        var resolved = matrix.Resolve();
        if (resolved.Count == 0) return;

        var varyingDimensions = resolved[0].Values.Keys
            .Where(dimensionType => resolved.Select(r => r.Values[dimensionType]).Distinct().Count() > 1)
            .OrderBy(t => Array.IndexOf(BuiltInOrder, t) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(t => t.Name);

        foreach (var dimensionType in varyingDimensions)
        {
            var group = new DimensionGroupViewModel { DimensionType = dimensionType, DisplayName = dimensionType.Name };
            foreach (var value in resolved.Select(r => r.Values[dimensionType]).Distinct().OrderBy(v => v.ToString()))
            {
                group.Values.Add(new DimensionValueViewModel { Value = value });
            }
            DimensionGroups.Add(group);
        }

        IsActive = true;
    }

    /// <summary>Reads current checkbox state into the shape BuildOrchestratorRequest expects.
    /// An unchecked dimension group (nothing checked) is treated as "everything" rather than
    /// "nothing" - an empty result would mean "match nothing," which is never what an
    /// all-unchecked group means to a user.</summary>
    public (TargetPlatform? Platform, TargetArchitecture? Architecture, BuildConfiguration? Configuration, IReadOnlyDictionary<Type, IReadOnlyList<Enum>>? DimensionFilter) BuildFilter()
    {
        TargetPlatform? platform = null;
        TargetArchitecture? architecture = null;
        BuildConfiguration? configuration = null;
        var customFilter = new Dictionary<Type, IReadOnlyList<Enum>>();

        foreach (var group in DimensionGroups)
        {
            var checkedValues = group.Values.Where(v => v.IsChecked).Select(v => v.Value).ToList();
            if (checkedValues.Count == 0 || checkedValues.Count == group.Values.Count)
                continue; // unconstrained: nothing checked, or everything checked - same effective meaning.

            if (group.DimensionType == typeof(TargetPlatform) && checkedValues.Count == 1)
                platform = (TargetPlatform)checkedValues[0];
            else if (group.DimensionType == typeof(TargetArchitecture) && checkedValues.Count == 1)
                architecture = (TargetArchitecture)checkedValues[0];
            else if (group.DimensionType == typeof(BuildConfiguration) && checkedValues.Count == 1)
                configuration = (BuildConfiguration)checkedValues[0];
            else
                customFilter[group.DimensionType] = checkedValues;
        }

        return (platform, architecture, configuration, customFilter.Count > 0 ? customFilter : null);
    }
}
