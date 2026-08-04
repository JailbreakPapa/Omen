// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Rules;

/// <summary>
/// Resolves a "Gem::&lt;GemName&gt;.&lt;Alias&gt;" reference (as used in
/// TargetRules.ExtraModules) to the concrete expanded module name that alias points at.
/// A plain module name passes through unchanged.
/// </summary>
public static class GemAliasResolver
{
    private const string Prefix = "Gem::";

    public static string Resolve(string extraModuleEntry, IReadOnlyList<GemRules> gems)
    {
        if (!extraModuleEntry.StartsWith(Prefix, StringComparison.Ordinal))
            return extraModuleEntry;

        var rest = extraModuleEntry[Prefix.Length..];
        var dot = rest.IndexOf('.');
        if (dot < 0)
        {
            throw new InvalidOperationException(
                $"'{extraModuleEntry}' must be in the form 'Gem::<GemName>.<Alias>'.");
        }

        var gemName = rest[..dot];
        var aliasName = rest[(dot + 1)..];

        var gem = gems.FirstOrDefault(g => g.Name.Equals(gemName, StringComparison.OrdinalIgnoreCase));
        if (gem == null)
            throw new InvalidOperationException($"'{extraModuleEntry}' references unknown gem '{gemName}'.");

        if (!gem.Aliases.TryGetValue(aliasName, out var flavorKind))
            throw new InvalidOperationException($"Gem '{gemName}' has no alias '{aliasName}'.");

        return $"{gem.Name}.{flavorKind}";
    }
}
