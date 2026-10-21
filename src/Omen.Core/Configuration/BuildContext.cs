// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Options;

namespace Omen.Core.Configuration;

/// <summary>
/// Represents the current build context with platform, architecture, and configuration.
/// </summary>
public sealed class BuildContext
{
    public required TargetPlatform Platform { get; init; }
    public required TargetArchitecture Architecture { get; init; }
    public required BuildConfiguration Configuration { get; init; }
    public required string ProjectRoot { get; init; }
    public required string OutputDirectory { get; init; }
    public required string IntermediateDirectory { get; init; }
    
    /// <summary>
    /// Additional global definitions applied to all modules.
    /// </summary>
    public List<string> GlobalDefinitions { get; init; } = [];
    
    /// <summary>
    /// Number of parallel jobs for compilation.
    /// </summary>
    public int ParallelJobs { get; init; } = Environment.ProcessorCount;
    
    /// <summary>
    /// Whether to use unity builds.
    /// </summary>
    public bool UseUnityBuild { get; init; } = true;
    
    /// <summary>
    /// Whether to use precompiled headers.
    /// </summary>
    public bool UsePCH { get; init; } = true;
    
    /// <summary>
    /// Whether to use incremental linking.
    /// </summary>
    public bool UseIncrementalLinking { get; init; } = true;
    
    /// <summary>
    /// Whether distributed build is enabled.
    /// </summary>
    public bool UseDistributedBuild { get; init; } = false;
    
    /// <summary>
    /// OmenNet coordinator address for distributed builds.
    /// </summary>
    public string? CoordinatorAddress { get; init; }

    /// <summary>
    /// Build options declared during rule instantiation (via BuildOptions.Declare), collected
    /// as a side effect of constructing TargetRules/ModuleRules/PluginRules against this context -
    /// mirrors how CMake's option() calls register into its cache during Configure.
    /// </summary>
    public List<BuildOptionDeclaration> DeclaredOptions { get; init; } = [];

    /// <summary>The effective value of a declared option, or null when nothing declared it.</summary>
    public string? Option(string name) =>
        DeclaredOptions.FirstOrDefault(o => o.Name == name)?.EffectiveValue;

    /// <summary>A declared boolean option's effective value, CMake's truthiness rules.</summary>
    public bool OptionBool(string name, bool fallback = false) => Option(name) switch
    {
        null or "" => fallback,
        var v => v.Equals("true", StringComparison.OrdinalIgnoreCase)
                 || v.Equals("on", StringComparison.OrdinalIgnoreCase)
                 || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                 || v == "1"
    };

    /// <summary>
    /// Persisted option overrides (from a prior Configure), consulted by BuildOptions.Declare
    /// when a rules file declares an option - a name present here overrides that option's
    /// compiled-in default for this build.
    /// </summary>
    public IReadOnlyDictionary<string, string> CachedOptionValues { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Values for any dimension beyond the built-in Platform/Architecture/Configuration -
    /// populated by BuildOrchestrator/ProjectGenerationOrchestrator when constructing a
    /// BuildContext from a ResolvedTarget that carries a custom dimension. Read via
    /// GetDimension&lt;T&gt;(), never directly, so built-in and custom dimensions share one API.
    /// </summary>
    public IReadOnlyDictionary<Type, Enum> AdditionalDimensions { get; init; } = new Dictionary<Type, Enum>();

    /// <summary>
    /// Reads this context's value for any [BuildDimension] enum - Platform/Architecture/
    /// Configuration via their own properties, anything else via AdditionalDimensions.
    /// </summary>
    public T GetDimension<T>() where T : struct, Enum
    {
        if (typeof(T) == typeof(TargetPlatform)) return (T)(object)Platform;
        if (typeof(T) == typeof(TargetArchitecture)) return (T)(object)Architecture;
        if (typeof(T) == typeof(BuildConfiguration)) return (T)(object)Configuration;
        if (AdditionalDimensions.TryGetValue(typeof(T), out var value)) return (T)value;
        throw new InvalidOperationException($"BuildContext has no value for dimension '{typeof(T).Name}'.");
    }

    /// <summary>
    /// Ground truth for O3DE PAL_TRAIT_* conditionals that a PluginFlavor.RequiredTraits or
    /// ModuleRules.RequiredTraits gate checks against (e.g. "PAL_TRAIT_BUILD_HOST_TOOLS"). Omen
    /// does not read O3DE's per-platform PAL CMake files, so this is empty by default - meaning
    /// every trait is "unknown" until some future PAL-file reader populates it here. A trait
    /// present with value `false` fails its gate; a trait present with `true`, or simply absent
    /// ("unknown"), passes it - see Rules.TraitGating.IsSatisfied for the exact rule and why
    /// "unknown" defaults to satisfied rather than not.
    /// </summary>
    public IReadOnlyDictionary<string, bool> PlatformTraits { get; set; } = new Dictionary<string, bool>();

    /// <summary>
    /// Engine-wide compiler flags for this build's compiler family and configuration, resolved
    /// from the project's OmenProjectRules.ToolchainOptions. Applied to every compile action on
    /// top of whatever the module itself declares. Paths in these flags (an MSVC /FI force-include,
    /// say) may be written relative to the project root: compile actions run with ProjectRoot as
    /// their working directory.
    /// </summary>
    public IReadOnlyList<string> GlobalCompilerOptions { get; set; } = [];

    /// <summary>Engine-wide linker flags, same resolution as
    /// <see cref="GlobalCompilerOptions"/>.</summary>
    public IReadOnlyList<string> GlobalLinkerOptions { get; set; } = [];

    /// <summary>
    /// Absolute path of the rule file each rule type was declared in, keyed by type full name.
    /// Populated by CompiledRules just before it instantiates anything; empty for a hand-built
    /// context, in which case every rule resolves against ProjectRoot as before.
    /// </summary>
    public IReadOnlyDictionary<string, string> RuleFilePaths { get; set; } =
        new Dictionary<string, string>();

    /// <summary>The directory <paramref name="ruleType"/>'s rule file lives in, or ProjectRoot
    /// when it is not known.</summary>
    public string RuleDirectoryFor(Type ruleType) =>
        RuleFilePaths.TryGetValue(ruleType.FullName ?? ruleType.Name, out var path)
            ? Path.GetDirectoryName(path) ?? ProjectRoot
            : ProjectRoot;

    /// <summary>The rule file <paramref name="ruleType"/> was declared in, or null when it is
    /// not known (a hand-built context).</summary>
    public string? RuleFileFor(Type ruleType) =>
        RuleFilePaths.TryGetValue(ruleType.FullName ?? ruleType.Name, out var path) ? path : null;

    /// <summary>
    /// Resolves a path written in a rule file. Rule paths are relative to the rule file that
    /// declares them - the same thing CMake means by a path in a CMakeLists.txt - which is what
    /// lets a plugin's own `LoadManifest(".")` / `SourceDirectory = "Code"` mean the plugin whether
    /// the plugin is built standalone or as one of a hundred under an engine root.
    ///
    /// ponytail: the ProjectRoot fallback is a compatibility shim for rule files that were
    /// generated with project-root-relative paths (every *.module.cs OmenConvert has emitted so
    /// far writes its own full path, e.g. AzCore.module.cs sets SourceDirectory =
    /// "Code/Framework/AzCore"). Resolving those against their own directory would double the
    /// path, so a rule-relative candidate that does not exist falls back. Delete the fallback
    /// once the converter emits rule-relative paths and the corpus is regenerated.
    /// </summary>
    public string ResolveRulePath(Type ruleType, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return Path.GetFullPath(relativePath);

        var ruleRelative = Path.GetFullPath(Path.Combine(RuleDirectoryFor(ruleType), relativePath));
        if (Directory.Exists(ruleRelative) || File.Exists(ruleRelative))
            return ruleRelative;

        var projectRelative = Path.GetFullPath(Path.Combine(ProjectRoot, relativePath));
        return Directory.Exists(projectRelative) || File.Exists(projectRelative)
            ? projectRelative
            : ruleRelative;
    }

    /// <summary>
    /// Gets a string identifier for this build context.
    /// </summary>
    public string GetContextId() => $"{Platform}-{Architecture}-{Configuration}";
}
