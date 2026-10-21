// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.RegularExpressions;
using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Reads O3DE's `ly_associate_package(PACKAGE_NAME n TARGETS a b PACKAGE_HASH h)` calls out of
/// the engine tree's cmake/3rdParty/**/BuiltInPackages*.cmake files. Regex over lines, not a
/// CMake evaluator: every real call is a single self-contained invocation with fixed keyword
/// arguments, and reading the tree's own files means Omen's package table can never drift from
/// CMake's. Files are found under &lt;root&gt;/cmake/3rdParty recursively, which covers the
/// per-platform Platform/&lt;name&gt;/BuiltInPackages_&lt;platform&gt;.cmake layout.
/// </summary>
public static class BuiltInPackagesReader
{
    private static readonly Regex AssociateRegex = new(
        @"ly_associate_package\s*\(\s*PACKAGE_NAME\s+(?<package>\S+)\s+TARGETS\s+(?<targets>.+?)\s+PACKAGE_HASH\s+(?<hash>[0-9a-fA-F]+)\s*\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Target name (e.g. "Lua") -> package folder name (e.g. "Lua-5.4.4-rev1-windows").
    /// Case-insensitive keys: the tree itself aliases 3rdParty::zlib to 3rdParty::ZLIB.</summary>
    public static IReadOnlyDictionary<string, string> Read(string projectRoot)
    {
        var associations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestDir = Path.Combine(projectRoot, "cmake", "3rdParty");
        if (!Directory.Exists(manifestDir))
            return associations;

        foreach (var file in Directory.GetFiles(manifestDir, "BuiltInPackages*.cmake", SearchOption.AllDirectories))
        {
            foreach (Match match in AssociateRegex.Matches(File.ReadAllText(file)))
            {
                foreach (var target in match.Groups["targets"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    associations[target] = match.Groups["package"].Value;
            }
        }
        return associations;
    }
}

/// <summary>
/// Locates downloaded O3DE packages on disk. Honors LY_3RDPARTY_PATH (the same variable the
/// CMake build uses), defaulting to ~/.o3de/3rdParty/packages.
/// ponytail: local cache only - no downloader yet; a missing package surfaces through the
/// existing unresolved-dependency warning. Add HttpClient + SHA256 + `tar -xf` when needed.
/// </summary>
public static class PackageStore
{
    public static string Root =>
        Environment.GetEnvironmentVariable("LY_3RDPARTY_PATH") is { Length: > 0 } env
            ? Path.Combine(env, "packages")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".o3de", "3rdParty", "packages");

    public static string? FindPackageDirectory(string packageName)
    {
        var candidate = Path.Combine(Root, packageName);
        return Directory.Exists(candidate) ? candidate : null;
    }
}

/// <summary>
/// The synthesized module standing in for one resolved `3rdParty::X` target - a HeaderOnly
/// module (compiles nothing) carrying the package's real include paths, prebuilt libraries,
/// and definitions, exactly the shape the hand-vendored third-party modules already use, so
/// the action graph and generators need nothing new.
/// </summary>
internal sealed class ThirdPartyPackageModuleRules : ModuleRules
{
    /// <param name="name">The name the dependency asked for, which is not always
    /// <see cref="ThirdPartyTargetDefinition.TargetName"/>: a package may declare its real
    /// target under its own namespace and alias 3rdParty::X onto it (Findexpat.cmake declares
    /// expat::expat and aliases both 3rdParty::expat and EXPAT::EXPAT to it). Naming the module
    /// after the real target leaves the dependency that asked for the alias unresolved, and a
    /// third-party module nothing depends on contributes nothing - the package's include paths
    /// and libraries simply vanish from the consumer's command line.</param>
    public ThirdPartyPackageModuleRules(BuildContext context, ThirdPartyTargetDefinition definition, string name)
        : base(context, explicitName: name)
    {
        Type = ModuleType.ThirdParty;
        BinaryType = TargetType.HeaderOnly;
        SourceDirectory = definition.PackageDirectory;
        PublicIncludePaths.AddRange(definition.IncludeDirectories);
        PublicLibraries.AddRange(definition.Libraries);
        PublicDefinitions.AddRange(definition.Definitions);
        PublicSystemLibraries.AddRange(definition.SystemLibraries);
        AdditionalLinkerFlags.AddRange(definition.LinkerOptions);
        RuntimeFiles.AddRange(definition.RuntimeFiles);
        // Cross-package references ("3rdParty::Imath") resolve like any other third-party
        // dependency - the resolver chases them transitively.
        ThirdPartyDependencies.AddRange(definition.Dependencies);
    }
}

/// <summary>
/// Turns the `3rdParty::X` names modules declare in <see cref="ModuleRules.ThirdPartyDependencies"/>
/// into synthesized modules, using the engine tree's own package associations and the local
/// package store. Two-tier by design: a hand-written module already named "3rdParty::X" wins
/// outright (the override tier for packages whose Find module the parser can't handle); a
/// package outside the parser's template throws <see cref="FindModuleParseException"/> loudly
/// rather than translating wrong; a package simply not present locally is skipped and left to
/// the existing unresolved-dependency warning.
/// </summary>
public static class ThirdPartyPackageResolver
{
    public static IReadOnlyList<ModuleRules> Synthesize(BuildContext context, IReadOnlyList<ModuleRules> modules)
    {
        var known = modules.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(modules
            .SelectMany(m => m.ThirdPartyDependencies)
            .Where(IsResolvableName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !known.Contains(n)));

        if (pending.Count == 0)
            return [];

        var associations = AssociationsByRoot.GetOrAdd(context.ProjectRoot, BuiltInPackagesReader.Read);
        var synthesized = new List<ModuleRules>();
        var parsedPackages = ParsedPackagesFor(context);

        var vcpkgRoot = VcpkgResolver.FindInstalledRoot(context.ProjectRoot);
        var vcpkgDepends = vcpkgRoot != null ? VcpkgResolver.ReadStatusDependencies(vcpkgRoot) : null;

        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!known.Add(name))
                continue;

            if (name.StartsWith(VcpkgResolver.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (vcpkgRoot == null)
                    continue; // no vcpkg tree - the unresolved-dependency warning names it
                var vcpkgDefinition = VcpkgResolver.Resolve(name, context, vcpkgRoot, vcpkgDepends!);
                if (vcpkgDefinition == null)
                    continue; // port not installed for this triplet - same warning path
                var vcpkgModule = new ThirdPartyPackageModuleRules(context, vcpkgDefinition, name);
                synthesized.Add(vcpkgModule);
                foreach (var dependency in vcpkgModule.ThirdPartyDependencies)
                {
                    if (!known.Contains(dependency))
                        pending.Enqueue(dependency);
                }
                continue;
            }

            var bare = name["3rdParty::".Length..];
            if (!associations.TryGetValue(bare, out var packageName))
                continue; // no association - existing unresolved-dependency warning names it
            var packageDir = PackageStore.FindPackageDirectory(packageName);
            if (packageDir == null)
                continue; // not downloaded - same warning path

            if (!parsedPackages.TryGetValue(packageDir, out var targets))
            {
                targets = new FindModuleParser(packageDir, context, locatePackageDirectory: targetName =>
                        associations.TryGetValue(targetName, out var pkg) ? PackageStore.FindPackageDirectory(pkg) : null)
                    .ParsePackage();
                parsedPackages[packageDir] = targets;
            }

            if (!targets.TryGetValue(name, out var definition))
            {
                throw new FindModuleParseException(
                    $"Package '{packageName}' parsed, but defines no target '{name}' " +
                    $"(found: {string.Join(", ", targets.Keys)}).");
            }

            var module = new ThirdPartyPackageModuleRules(context, definition, name);
            synthesized.Add(module);

            foreach (var dependency in module.ThirdPartyDependencies)
            {
                if (IsResolvableName(dependency) && !known.Contains(dependency))
                    pending.Enqueue(dependency);
            }
        }

        return synthesized;
    }

    // Memoized for the process: a generation instantiates the rules once per build context
    // (nine at engine scale) and each one re-read the manifests and re-parsed every package's
    // Find module. Parse results depend on platform and configuration (config generator
    // expressions), so they are keyed by both.
    // ponytail: process-lifetime memo, so a long-running GUI does not see a package edited
    // after its first Configure; restart, or key by manifest write time if that ever bites.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> AssociationsByRoot =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, IReadOnlyDictionary<string, ThirdPartyTargetDefinition>>> ParsedPackagesByContext =
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, IReadOnlyDictionary<string, ThirdPartyTargetDefinition>> ParsedPackagesFor(BuildContext context) =>
        ParsedPackagesByContext.GetOrAdd(
            $"{context.ProjectRoot}|{context.Platform}|{context.Configuration}",
            _ => new Dictionary<string, IReadOnlyDictionary<string, ThirdPartyTargetDefinition>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Qt goes through ModuleRules.UseQt (its own real resolution path), never here.</summary>
    private static bool IsResolvableName(string name) =>
        name.StartsWith(VcpkgResolver.Prefix, StringComparison.OrdinalIgnoreCase) ||
        (name.StartsWith("3rdParty::", StringComparison.OrdinalIgnoreCase) &&
         !name.StartsWith("3rdParty::Qt", StringComparison.OrdinalIgnoreCase));
}
