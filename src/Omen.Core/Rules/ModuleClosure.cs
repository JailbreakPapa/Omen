// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Rules;

/// <summary>
/// The result of <see cref="ModuleClosure.Compute"/>: the modules a target actually needs, plus
/// the seeds that named nothing.
/// </summary>
public sealed class ModuleClosureResult
{
    public required IReadOnlyList<ModuleRules> Modules { get; init; }

    /// <summary>Seed names (LaunchModuleName / ExtraModules / an enabled plugin's variant target)
    /// that matched no module. Distinct from an unresolved *dependency*, which is a warning: a
    /// seed is what the target explicitly asked to build, so naming nothing is an error.</summary>
    public required IReadOnlyList<string> UnresolvedSeeds { get; init; }

    /// <summary>True when the target declared no seeds at all and every module was kept - see
    /// <see cref="ModuleClosure.Compute"/> on why that is the fallback rather than "build
    /// nothing".</summary>
    public required bool WasUnfiltered { get; init; }

    /// <summary>
    /// For each reached module, the module that pulled it in - absent for a seed, which is its
    /// own reason. Answers "why is this in my build?", which is otherwise unanswerable: a module
    /// nobody appears to reference can arrive through several hops of dependencies and runtime
    /// dependencies, and reading the rule files does not show it.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ReachedVia { get; init; }

    /// <summary>
    /// The chain from <paramref name="moduleName"/> back to the seed that reached it, nearest
    /// first. A seed returns just itself. Guards against a malformed chain looping.
    /// </summary>
    public IReadOnlyList<string> PathToSeed(string moduleName)
    {
        var path = new List<string> { moduleName };
        var seen = new HashSet<string>(StringComparer.Ordinal) { moduleName };

        var current = moduleName;
        while (ReachedVia.TryGetValue(current, out var parent) && seen.Add(parent))
        {
            path.Add(parent);
            current = parent;
        }

        return path;
    }
}

/// <summary>
/// Narrows a build to the modules a target can actually reach.
///
/// Before this existed, every module discovered anywhere under the project's scan roots was
/// compiled and linked. That is indistinguishable from correct when the project is one plugin, and
/// nonsense at engine scale: building the Camera plugin pulled in 33 modules including AzFramework's
/// test executables, and died linking a program nobody asked for.
///
/// The closure starts from what the target names - <see cref="TargetRules.LaunchModuleName"/>,
/// <see cref="TargetRules.ExtraModules"/>, and the variant targets of the project's enabled plugins -
/// and walks Public/Private/Runtime dependencies transitively. Runtime dependencies are in because
/// they are the shared libraries staged next to the target's output; leaving them out would mean
/// building a program whose plugins do not exist.
/// </summary>
public static class ModuleClosure
{
    /// <summary>
    /// Computes the closure. A target that names no seeds at all keeps every module, unchanged
    /// from before this existed: plenty of small projects declare a target and some modules and
    /// nothing linking the two, and silently building nothing for them would be far worse than
    /// over-building. Filtering is opt-in by saying what you want.
    /// </summary>
    /// <param name="target">The target being built.</param>
    /// <param name="modules">Every module that survived trait gating and plugin enablement.</param>
    /// <param name="plugins">Every plugin, for resolving "Plugin::X" / "Plugin::X.Alias" references.</param>
    /// <param name="additionalSeeds">
    /// Extra roots the caller wants kept - the concrete targets of a project's enabled-plugins list.
    /// A project that enables a plugin means it, whether or not the target also names it.
    /// </param>
    public static ModuleClosureResult Compute(
        TargetRules target,
        IReadOnlyList<ModuleRules> modules,
        IReadOnlyList<PluginRules> plugins,
        IEnumerable<string>? additionalSeeds = null)
    {
        var seeds = new List<string>();
        if (!string.IsNullOrWhiteSpace(target.LaunchModuleName))
            seeds.Add(target.LaunchModuleName);
        seeds.AddRange(target.ExtraModules);
        if (additionalSeeds != null)
            seeds.AddRange(additionalSeeds);

        if (seeds.Count == 0)
        {
            return new ModuleClosureResult
            {
                Modules = modules,
                UnresolvedSeeds = [],
                WasUnfiltered = true,
                ReachedVia = new Dictionary<string, string>(StringComparer.Ordinal)
            };
        }

        // First definition wins. Duplicate module names are already a hard error upstream
        // (BuildOrchestrator), so this only ever picks between identical candidates.
        var byName = new Dictionary<string, ModuleRules>(StringComparer.Ordinal);
        foreach (var module in modules)
            byName.TryAdd(module.Name, module);

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedSeeds = new List<string>();
        var queue = new Queue<string>();

        // Who pulled each module in. First arrival wins, matching the BFS order, so the recorded
        // path is a shortest one rather than whichever happened to be walked last.
        var reachedVia = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var seed in seeds.Distinct(StringComparer.Ordinal))
        {
            var expanded = Expand(seed, plugins);
            if (expanded.Count == 0 || expanded.All(name => !byName.ContainsKey(name)))
            {
                unresolvedSeeds.Add(seed);
                continue;
            }

            foreach (var name in expanded)
                queue.Enqueue(name);
        }

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!reached.Add(name))
                continue;

            // An unreachable dependency is not fatal here - ReportUnresolvedDependencies warns
            // about it, and plenty of real dependency names are external targets Omen does not
            // build at all (3rdParty::Qt::Core, Legacy::EditorCommon).
            if (!byName.TryGetValue(name, out var module))
                continue;

            var dependencies = module.AllDependencies.Concat(module.RuntimeDependencies);

            foreach (var dependency in dependencies)
            {
                foreach (var expanded in Expand(dependency, plugins))
                {
                    if (reached.Contains(expanded))
                        continue;

                    reachedVia.TryAdd(expanded, name);
                    queue.Enqueue(expanded);
                }
            }
        }

        return new ModuleClosureResult
        {
            // Original order, not discovery order through the graph: downstream code (the
            // topological sort, the duplicate check) is order-insensitive, but a stable module
            // list keeps build logs diffable between runs.
            Modules = modules.Where(m => reached.Contains(m.Name)).ToList(),
            UnresolvedSeeds = unresolvedSeeds,
            WasUnfiltered = false,
            ReachedVia = reachedVia
        };
    }

    /// <summary>
    /// One reference to the concrete module name(s) it stands for. "Plugin::X.Alias" can expand to
    /// several (a multi-target alias); a plain module name is itself. An alias that names a plugin
    /// or alias that does not exist throws inside PluginAliasResolver - swallowed here, because a
    /// reference Omen cannot resolve is reported by the caller's unresolved-dependency pass
    /// rather than aborting the whole closure.
    /// </summary>
    private static IReadOnlyList<string> Expand(string reference, IReadOnlyList<PluginRules> plugins)
    {
        try
        {
            return PluginAliasResolver.ResolveAliasTargets(reference, plugins);
        }
        catch (InvalidOperationException)
        {
            return [reference];
        }
    }
}
