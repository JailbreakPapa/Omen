// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Validates a resolved module graph against declared layering rules before any compile
/// or link action is built. All violations that would be found are still reported one at
/// a time (the first one found throws) since architectural drift is best fixed as soon as
/// it's introduced.
/// </summary>
public static class LayeringValidator
{
    public static void Validate(IReadOnlyList<ModuleRules> modules)
    {
        var byName = modules.ToDictionary(m => m.Name);

        foreach (var module in modules)
        {
            foreach (var (forbiddenName, reason) in module.ForbiddenDependencies)
            {
                if (string.IsNullOrWhiteSpace(reason))
                {
                    throw new LayeringViolationException(
                        $"Module '{module.Name}' forbids dependency on '{forbiddenName}' with no reason. A reason is required.");
                }

                var path = FindPath(module, forbiddenName, byName);
                if (path != null)
                {
                    throw new LayeringViolationException(
                        $"Layering violation: {string.Join(" -> ", path)} reaches forbidden dependency '{forbiddenName}'. Reason: {reason}");
                }
            }

            if (module.Type != ModuleType.ThirdParty)
                continue;

            foreach (var depName in module.PublicDependencies.Concat(module.PrivateDependencies))
            {
                if (byName.TryGetValue(depName, out var dep) && dep.Type != ModuleType.ThirdParty)
                {
                    throw new LayeringViolationException(
                        $"Layering violation: third-party module '{module.Name}' depends on first-party module '{dep.Name}'. Vendored code must stand alone.");
                }
            }
        }
    }

    private static List<string>? FindPath(ModuleRules start, string targetName, Dictionary<string, ModuleRules> byName)
    {
        var visited = new HashSet<string>();
        var path = new List<string> { start.Name };
        return Search(start, targetName, byName, visited, path) ? path : null;
    }

    private static bool Search(ModuleRules current, string targetName, Dictionary<string, ModuleRules> byName, HashSet<string> visited, List<string> path)
    {
        if (!visited.Add(current.Name))
            return false;

        foreach (var depName in current.PublicDependencies.Concat(current.PrivateDependencies))
        {
            path.Add(depName);

            if (depName == targetName)
                return true;

            if (byName.TryGetValue(depName, out var dep) && Search(dep, targetName, byName, visited, path))
                return true;

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }
}

/// <summary>
/// Thrown when <see cref="LayeringValidator.Validate"/> finds a layering violation.
/// </summary>
public sealed class LayeringViolationException(string message) : Exception(message);
