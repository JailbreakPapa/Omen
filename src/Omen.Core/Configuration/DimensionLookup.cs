// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Reflection;

namespace Omen.Core.Configuration;

/// <summary>
/// Resolves a dimension by name (e.g. from a CLI --dim RenderApi=DirectX12 flag) to its
/// Type and a parsed value - on-demand lookup, not a persistent registry, since the only
/// consumer that needs a string-to-Type mapping is the CLI/GUI filter-parsing layer.
/// TargetMatrix.Resolve() itself never needs this: it works directly with the Type objects
/// a rules file's own For&lt;T&gt; calls already reference.
/// </summary>
public static class DimensionLookup
{
    /// <summary>
    /// Scans Omen.Core's own assembly (the three built-in dimensions) plus the given rules
    /// assembly (any studio-defined custom dimensions) for a [BuildDimension] enum whose
    /// name matches, case-insensitively.
    /// </summary>
    public static Type? FindDimensionType(Assembly rulesAssembly, string name)
    {
        var candidates = typeof(BuildDimensionAttribute).Assembly.GetTypes()
            .Concat(rulesAssembly.GetTypes());

        return candidates.FirstOrDefault(t =>
            t.IsEnum
            && t.GetCustomAttribute<BuildDimensionAttribute>() != null
            && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Case-insensitive Enum.Parse against the given dimension type.</summary>
    public static bool TryParseValue(Type dimensionType, string valueName, out Enum? value)
    {
        if (Enum.TryParse(dimensionType, valueName, ignoreCase: true, out var parsed) && parsed is Enum enumValue)
        {
            value = enumValue;
            return true;
        }
        value = null;
        return false;
    }
}
