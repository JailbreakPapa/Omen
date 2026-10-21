// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Rules;

/// <summary>
/// Resolves a "Plugin::&lt;PluginName&gt;.&lt;Alias&gt;" reference (as used in
/// TargetRules.ExtraModules) to the concrete expanded module name that alias points at.
/// A plain module name passes through unchanged.
/// </summary>
public static class PluginAliasResolver
{
    private const string Prefix = "Plugin::";

    /// <summary>
    /// Finds a plugin declaring <paramref name="fullName"/> as an absolute alias. Checked before
    /// the usual plugin-name-prefix split, because an absolute alias name shares no prefix with
    /// its owning plugin - splitting first would hunt for a plugin that does not exist.
    /// </summary>
    private static PluginRules? FindAbsoluteAliasOwner(string fullName, IReadOnlyList<PluginRules> plugins) =>
        plugins.FirstOrDefault(g => g.AbsoluteAliasNames.Contains(fullName));

    public static string Resolve(string extraModuleEntry, IReadOnlyList<PluginRules> plugins)
    {
        if (!extraModuleEntry.StartsWith(Prefix, StringComparison.Ordinal))
            return extraModuleEntry;

        var fullName = extraModuleEntry[Prefix.Length..];
        var absoluteOwner = FindAbsoluteAliasOwner(fullName, plugins);
        var (pluginName, aliasName) = absoluteOwner is null
            ? SplitReference(extraModuleEntry)
            : (absoluteOwner.Name, fullName);
        var plugin = absoluteOwner ?? FindPlugin(pluginName, extraModuleEntry, plugins);

        if (plugin.Aliases.TryGetValue(aliasName, out var flavorKind))
            return PluginFlavorNaming.ModuleName(plugin.Name, flavorKind);

        if (plugin.CustomAliases.TryGetValue(aliasName, out var customSuffix))
            return PluginFlavorNaming.ModuleName(plugin.Name, plugin.CustomFlavors[customSuffix]);

        if (plugin.MultiTargetAliases.ContainsKey(aliasName))
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginName}' alias '{aliasName}' backs multiple targets - use " +
                $"{nameof(PluginAliasResolver)}.{nameof(ResolveAliasTargets)} instead of {nameof(Resolve)}.");
        }

        throw new InvalidOperationException($"Plugin '{pluginName}' has no alias '{aliasName}'.");
    }

    /// <summary>
    /// Like <see cref="Resolve"/>, but for an alias that may back 2+ targets
    /// (<see cref="PluginRules.MultiTargetAliases"/>): returns every concrete module name the
    /// alias expands to (1 for a single-flavor alias, N for a multi-target one, flattened
    /// recursively if one of those targets is itself an alias reference).
    /// </summary>
    public static IReadOnlyList<string> ResolveAliasTargets(string aliasReference, IReadOnlyList<PluginRules> plugins)
    {
        if (!aliasReference.StartsWith(Prefix, StringComparison.Ordinal))
            return [aliasReference];

        var fullName = aliasReference[Prefix.Length..];
        var absoluteOwner = FindAbsoluteAliasOwner(fullName, plugins);
        var (pluginName, aliasName) = absoluteOwner is null
            ? SplitReference(aliasReference)
            : (absoluteOwner.Name, fullName);
        var plugin = absoluteOwner ?? FindPlugin(pluginName, aliasReference, plugins);

        if (plugin.Aliases.TryGetValue(aliasName, out var flavorKind))
            return [PluginFlavorNaming.ModuleName(plugin.Name, flavorKind)];

        if (plugin.CustomAliases.TryGetValue(aliasName, out var customSuffix))
            return [PluginFlavorNaming.ModuleName(plugin.Name, plugin.CustomFlavors[customSuffix])];

        if (plugin.MultiTargetAliases.TryGetValue(aliasName, out var targets))
            return targets.SelectMany(t => ResolveTargetReference(t, plugins)).ToList();

        throw new InvalidOperationException($"Plugin '{pluginName}' has no alias '{aliasName}'.");
    }

    /// <summary>
    /// Resolves one raw CMake-TARGETS-style entry from a multi-target alias's target list:
    /// a bare "Plugin::&lt;PluginName&gt;" (no alias suffix - real O3DE's plain namespaced target)
    /// means that plugin's Runtime flavor; "Plugin::&lt;PluginName&gt;.&lt;Alias&gt;" recurses through
    /// ResolveAliasTargets; anything else passes through unchanged (already a concrete name).
    /// Unlike <see cref="Resolve"/>, a bare plugin reference is valid here - it's how real O3DE
    /// TARGETS lists reference the current or another plugin's plain target.
    /// </summary>
    private static IReadOnlyList<string> ResolveTargetReference(string targetRef, IReadOnlyList<PluginRules> plugins)
    {
        if (!targetRef.StartsWith(Prefix, StringComparison.Ordinal))
            return [targetRef];

        var rest = targetRef[Prefix.Length..];
        if (!rest.Contains('.'))
        {
            var plugin = FindPlugin(rest, targetRef, plugins);
            return [PluginFlavorNaming.ModuleName(plugin.Name, PluginFlavorKind.Runtime)];
        }

        return ResolveAliasTargets(targetRef, plugins);
    }

    private static (string PluginName, string AliasName) SplitReference(string extraModuleEntry)
    {
        var rest = extraModuleEntry[Prefix.Length..];
        var dot = rest.IndexOf('.');
        if (dot < 0)
        {
            throw new InvalidOperationException(
                $"'{extraModuleEntry}' must be in the form 'Plugin::<PluginName>.<Alias>'.");
        }
        return (rest[..dot], rest[(dot + 1)..]);
    }

    private static PluginRules FindPlugin(string pluginName, string extraModuleEntry, IReadOnlyList<PluginRules> plugins)
    {
        var plugin = plugins.FirstOrDefault(g => g.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
        if (plugin == null)
            throw new InvalidOperationException($"'{extraModuleEntry}' references unknown plugin '{pluginName}'.");
        return plugin;
    }
}
