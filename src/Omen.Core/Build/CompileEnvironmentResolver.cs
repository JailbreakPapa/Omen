// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.Core.Build;

/// <summary>
/// The single source of truth for include/define resolution.
///
/// The behaviour here is ActionGraphBuilder's, moved unchanged. That is the derivation the
/// compiler actually receives, so it is correct by definition; the generators that used to
/// re-derive it independently are reconciled onto this rather than the other way round.
///
/// Ordering is observable - the include order reaches the compiler command line - so the
/// traversal below is deliberately literal. Reordering it here would be indistinguishable from
/// the drift bug this type exists to remove.
/// </summary>
public sealed class CompileEnvironmentResolver
{
    private readonly BuildContext _context;
    private readonly IReadOnlyDictionary<string, ModuleRules> _modules;

    public CompileEnvironmentResolver(BuildContext context, IReadOnlyDictionary<string, ModuleRules> modules)
    {
        _context = context;
        _modules = modules;
    }

    /// <summary>
    /// Resolve the include paths and definitions <paramref name="module"/> compiles with under
    /// <paramref name="target"/>.
    /// </summary>
    public CompileEnvironment Resolve(ModuleRules module, TargetRules target)
    {
        var (own, external) = BuildIncludePaths(module);
        return new CompileEnvironment(own, BuildDefinitions(module, target), external);
    }

    /// <summary>
    /// Own and dependency include paths, split by origin. A ThirdParty module's paths go to the
    /// second list so the toolchain can mark them external: under /W4 with warnings-as-errors,
    /// RapidJSON's pointer.h and Qt's qpixelformat.h fail the build from inside code that is not
    /// ours to fix. MSVC's /external:I with /external:W0 silences warnings from those roots only.
    /// </summary>
    private (IReadOnlyList<string> Own, IReadOnlyList<string> External) BuildIncludePaths(ModuleRules module)
    {
        var ownPaths = new List<string>();
        var externalPaths = new List<string>();
        var visited = new HashSet<string>();

        void AddModuleIncludePaths(ModuleRules mod, bool includePrivate)
        {
            if (visited.Contains(mod.Name))
                return;
            visited.Add(mod.Name);
            var paths = mod.Type == ModuleType.ThirdParty ? externalPaths : ownPaths;

            var sourceDir = mod.SourceDirectory ?? $"Source/{mod.Name}";
            var baseDir = Path.Combine(_context.ProjectRoot, sourceDir);

            // Add module's source directory
            paths.Add(baseDir);

            // Add Public subdirectory if it exists
            var publicDir = Path.Combine(baseDir, "Public");
            if (Directory.Exists(publicDir))
            {
                paths.Add(publicDir);
            }

            // Add Private subdirectory only for the main module
            if (includePrivate)
            {
                var privateDir = Path.Combine(baseDir, "Private");
                if (Directory.Exists(privateDir))
                {
                    paths.Add(privateDir);
                }
            }

            // Add explicit public include paths (relative to module source dir)
            foreach (var p in mod.PublicIncludePaths)
            {
                var fullPath = Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);
                if (!paths.Contains(fullPath))
                    paths.Add(fullPath);
            }

            // Someone else's headers, wherever they were declared from.
            foreach (var p in mod.PublicExternalIncludePaths)
            {
                var fullPath = Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);
                if (!externalPaths.Contains(fullPath))
                    externalPaths.Add(fullPath);
            }

            // Add explicit private include paths only for main module
            if (includePrivate)
            {
                foreach (var p in mod.PrivateIncludePaths)
                {
                    var fullPath = Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);
                    if (!paths.Contains(fullPath))
                        paths.Add(fullPath);
                }
            }

            // Recursively add dependencies' public include paths
            foreach (var depName in mod.AllDependencies)
            {
                if (_modules.TryGetValue(depName, out var depModule))
                {
                    AddModuleIncludePaths(depModule, false); // Only public paths for dependencies
                }
            }
        }

        // Start with the main module (include private paths)
        AddModuleIncludePaths(module, true);

        return (ownPaths, externalPaths);
    }

    private IReadOnlyList<string> BuildDefinitions(ModuleRules module, TargetRules target)
    {
        var defs = new List<string>();

        // Configuration definitions
        defs.Add($"OMEN_{_context.Configuration.ToString().ToUpperInvariant()}=1");
        defs.Add($"OMEN_{_context.Platform.ToString().ToUpperInvariant()}=1");

        // Engine-wide definitions (the project's ToolchainOptions for this compiler family and
        // configuration), then the target's own.
        defs.AddRange(_context.GlobalDefinitions);
        defs.AddRange(target.GlobalDefinitions);

        // Module definitions
        defs.AddRange(module.PublicDefinitions);
        defs.AddRange(module.PrivateDefinitions);

        // Add public definitions from dependencies
        var visited = new HashSet<string>();
        void AddDependencyDefinitions(ModuleRules mod)
        {
            if (visited.Contains(mod.Name))
                return;
            visited.Add(mod.Name);

            foreach (var depName in mod.AllDependencies)
            {
                if (_modules.TryGetValue(depName, out var depModule))
                {
                    defs.AddRange(depModule.PublicDefinitions);
                    AddDependencyDefinitions(depModule);
                }
            }
        }
        AddDependencyDefinitions(module);

        return defs.Distinct().ToList();
    }
}
