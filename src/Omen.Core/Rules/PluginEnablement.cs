// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Rules;

/// <summary>
/// The symbolic axis O3DE's `ly_enable_plugins`/`ly_set_plugin_variant_to_load` resolve a plugin
/// dependency against - `${plugin_name}.Clients`, `${plugin_name}.Servers`, etc. Distinct from
/// <see cref="PluginFlavorKind"/>: a flavor is a plugin's own build-shape (Static/Runtime/Editor/...),
/// while a variant is which flavor a given *consumer* (a Clients app vs. a Servers app) wants,
/// resolved through the plugin's own <see cref="PluginRules.Aliases"/>/<see cref="PluginRules.MultiTargetAliases"/>
/// - the same alias mechanism PluginAliasResolver already resolves for TargetRules.ExtraModules.
/// </summary>
public enum PluginVariant
{
    Clients,
    Servers,
    Unified,
    Tools,
    Builders
}

/// <summary>
/// The plugin-enablement system: given a project's enabled plugin list and a variant, resolves the
/// full transitive set of concrete module targets that must be built. This is what makes
/// <see cref="PluginAliasResolver"/> (previously wired into nothing) actually reachable from a
/// project's plugin list, mirroring `cmake/Plugins.cmake`'s `get_all_plugin_dependencies` (transitive
/// plugin.json dependency closure) + `ly_add_plugin_dependencies_to_project_variants`
/// (`${plugin_name}.${VARIANT}` target lookup, skipped when the plugin doesn't expose that variant).
///
/// O3DE needs a delayed/two-phase pass (`ly_enable_plugins_delayed`) because CMake processes
/// directories in `add_subdirectory` order, so a plugin's CMake target may not exist yet at the
/// point `ly_enable_plugins` is called for a project that happens to be configured first. Omen has
/// no equivalent ordering hazard: RuleCompiler compiles every `.plugin.cs`/`.module.cs`/`.target.cs`
/// file into one assembly before any PluginRules is instantiated (see CompiledRules.CreatePluginRules),
/// so by the time a caller has an `IReadOnlyList&lt;PluginRules&gt;` to pass in here, every plugin
/// (and its Aliases/MultiTargetAliases) already exists - resolution is naturally "delayed" until
/// after everything is known, with no separate deferred pass required.
/// </summary>
public static class PluginEnablement
{
    /// <summary>
    /// Resolves the transitive closure of plugin names for a project's explicitly enabled plugin
    /// list, walking each plugin's <see cref="PluginManifest.Dependencies"/> (plugin.json's
    /// "dependencies" array) the way `get_all_plugin_dependencies` walks it recursively. A plugin
    /// depended on by two different enabled plugins (a diamond) appears exactly once, in the
    /// position it was first reached - same visited-set guard CMake's implementation uses,
    /// which also makes a dependency cycle a no-op instead of infinite recursion.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A named plugin (explicitly enabled, or reached via another plugin's "dependencies") is not
    /// present in <paramref name="allPlugins"/>. Mirrors `get_all_plugin_dependencies`'s
    /// FATAL_ERROR for an unregistered plugin path - a silently dropped dependency is worse than
    /// a hard failure here. O3DE's PLUGIN_OPTIONAL escape hatch for an unresolvable-but-optional
    /// dependency is not modeled (see the report's "deferred" section).
    /// </exception>
    public static IReadOnlyList<string> ResolveEnabledPluginNames(
        IReadOnlyList<string> enabledPluginNames,
        IReadOnlyList<PluginRules> allPlugins)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string pluginName)
        {
            if (!visited.Add(pluginName))
                return;

            var plugin = FindPlugin(pluginName, allPlugins);
            result.Add(plugin.Name);

            foreach (var dependency in plugin.Manifest?.Dependencies ?? [])
                Visit(dependency);
        }

        foreach (var pluginName in enabledPluginNames)
            Visit(pluginName);

        return result;
    }

    /// <summary>
    /// Resolves a project's enabled plugin list (transitively, via <see cref="ResolveEnabledPluginNames"/>)
    /// to the concrete module targets that variant must build - O3DE's
    /// `ly_add_plugin_dependencies_to_project_variants`. A plugin that never aliases the requested
    /// variant (neither a single-flavor <see cref="PluginRules.Aliases"/> entry nor a
    /// <see cref="PluginRules.MultiTargetAliases"/> entry) contributes nothing for it, silently -
    /// matching CMake's `if(NOT TARGET ${plugin_variant_target}) continue()`, not an error: plenty
    /// of plugins have no Tools/Builders flavor at all. The `PLUGIN_VARIANT_DEFAULT_*` fallback CMake
    /// has for exactly one variant (HeadlessServers -> Servers) is not modeled here - none of
    /// the five variants this enum covers needs it (see the report's "deferred" section).
    /// A target reachable through two different plugins (or two different aliases) is only
    /// returned once.
    /// </summary>
    public static IReadOnlyList<string> ResolveEnabledTargets(
        IReadOnlyList<string> enabledPluginNames,
        PluginVariant variant,
        IReadOnlyList<PluginRules> allPlugins)
    {
        var variantName = variant.ToString();
        var targets = new List<string>();
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pluginName in ResolveEnabledPluginNames(enabledPluginNames, allPlugins))
        {
            var plugin = FindPlugin(pluginName, allPlugins);

            if (!plugin.Aliases.ContainsKey(variantName) && !plugin.MultiTargetAliases.ContainsKey(variantName))
                continue;

            foreach (var target in PluginAliasResolver.ResolveAliasTargets($"Plugin::{plugin.Name}.{variantName}", allPlugins))
            {
                if (seenTargets.Add(target))
                    targets.Add(target);
            }
        }

        return targets;
    }

    /// <summary>
    /// Filters an already-created module list (CompiledRules.CreateModuleRules) down to the
    /// modules an enabled-plugins list/variant actually enables - the one filter both
    /// BuildOrchestrator and ProjectGenerationOrchestrator apply, so they can't independently
    /// drift on what "enabled" means. A hand-written ModuleRules (<see cref="ModuleRules.PluginName"/>
    /// null) is never a `Plugin::*` target O3DE's `ly_enable_plugins` would have governed, so it always
    /// stays included; only a synthesized plugin-flavor module is subject to the filter.
    ///
    /// <paramref name="enabledPluginNames"/> null means no enabled-plugins list was ever declared for
    /// this build (neither a per-request override nor OmenProjectRules.EnabledPlugins) - every plugin
    /// stays active and <paramref name="modules"/> is returned unchanged, exactly the behavior
    /// every pre-existing project depends on. An explicitly empty list is not the same thing: it
    /// resolves to zero enabled targets, so every plugin-flavor module is filtered out.
    /// </summary>
    public static IReadOnlyList<ModuleRules> FilterEnabledModules(
        IReadOnlyList<ModuleRules> modules,
        IReadOnlyList<string>? enabledPluginNames,
        PluginVariant variant,
        IReadOnlyList<PluginRules> allPlugins)
    {
        if (enabledPluginNames == null)
            return modules;

        var enabledTargetNames = new HashSet<string>(
            ResolveEnabledTargets(enabledPluginNames, variant, allPlugins),
            StringComparer.Ordinal);

        return modules.Where(m => m.PluginName == null || enabledTargetNames.Contains(m.Name)).ToList();
    }

    private static PluginRules FindPlugin(string pluginName, IReadOnlyList<PluginRules> allPlugins)
    {
        var plugin = allPlugins.FirstOrDefault(g => g.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
        if (plugin == null)
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginName}' is not among the compiled plugin rules - is it missing a `.plugin.cs` file, " +
                "or misspelled in an enabled-plugins list or another plugin's plugin.json \"dependencies\"?");
        }
        return plugin;
    }
}
