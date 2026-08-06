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
    /// as a side effect of constructing TargetRules/ModuleRules/GemRules against this context -
    /// mirrors how CMake's option() calls register into its cache during Configure.
    /// </summary>
    public List<BuildOptionDeclaration> DeclaredOptions { get; init; } = [];

    /// <summary>
    /// Persisted option overrides (from a prior Configure), consulted by BuildOptions.Declare
    /// when a rules file declares an option - a name present here overrides that option's
    /// compiled-in default for this build.
    /// </summary>
    public IReadOnlyDictionary<string, string> CachedOptionValues { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets a string identifier for this build context.
    /// </summary>
    public string GetContextId() => $"{Platform}-{Architecture}-{Configuration}";
}
