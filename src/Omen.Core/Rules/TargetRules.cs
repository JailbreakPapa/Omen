// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Base class for target definitions. Inherit from this class in your .target.cs files.
/// Inspired by Unreal Engine's TargetRules pattern.
/// </summary>
public abstract class TargetRules
{
    /// <summary>
    /// The build context for this target.
    /// </summary>
    protected BuildContext Context { get; }
    
    /// <summary>
    /// Name of the target.
    /// </summary>
    public string Name { get; }
    
    /// <summary>
    /// Type of target (Executable, Library, etc.).
    /// </summary>
    public TargetType Type { get; set; } = TargetType.Executable;
    
    /// <summary>
    /// Platforms this target can be built for.
    /// </summary>
    public List<TargetPlatform> SupportedPlatforms { get; } = 
    [
        TargetPlatform.Windows,
        TargetPlatform.Linux,
        TargetPlatform.FreeBSD,
        TargetPlatform.Android,
        TargetPlatform.iOS
    ];
    
    /// <summary>
    /// The launch module (entry point module for executables).
    /// </summary>
    public string? LaunchModuleName { get; set; }
    
    /// <summary>
    /// Additional modules to include in the build.
    /// </summary>
    public List<string> ExtraModules { get; } = [];
    
    /// <summary>
    /// Link type for this target.
    /// </summary>
    public LinkType LinkType { get; set; } = LinkType.Default;
    
    /// <summary>
    /// Whether to use unity builds for this target.
    /// </summary>
    public bool UseUnityBuild { get; set; } = true;
    
    /// <summary>
    /// Whether to use adaptive unity build (excludes recently modified files).
    /// </summary>
    public bool UseAdaptiveUnityBuild { get; set; } = true;
    
    /// <summary>
    /// Whether to use precompiled headers.
    /// </summary>
    public bool UsePCHFiles { get; set; } = true;
    
    /// <summary>
    /// Whether to use incremental linking.
    /// </summary>
    public bool UseIncrementalLinking { get; set; } = true;
    
    /// <summary>
    /// Global preprocessor definitions applied to all modules.
    /// </summary>
    public List<string> GlobalDefinitions { get; } = [];
    
    /// <summary>
    /// Pre-build steps to execute before compilation.
    /// </summary>
    public List<BuildStep> PreBuildSteps { get; } = [];
    
    /// <summary>
    /// Post-build steps to execute after compilation.
    /// </summary>
    public List<BuildStep> PostBuildSteps { get; } = [];
    
    /// <summary>
    /// Output name override (without extension).
    /// </summary>
    public string? OutputName { get; set; }
    
    /// <summary>
    /// Output directory override.
    /// </summary>
    public string? OutputDirectory { get; set; }
    
    /// <summary>
    /// Default C++ standard for all modules.
    /// </summary>
    public CppStandard DefaultCppStandard { get; set; } = CppStandard.Cpp20;
    
    /// <summary>
    /// Default warning level for all modules.
    /// </summary>
    public WarningLevel DefaultWarningLevel { get; set; } = WarningLevel.Level4;
    
    /// <summary>
    /// Whether to treat warnings as errors by default.
    /// </summary>
    public bool DefaultTreatWarningsAsErrors { get; set; } = false;
    
    /// <summary>
    /// Whether distributed build is enabled for this target.
    /// </summary>
    public bool EnableDistributedBuild { get; set; } = true;
    
    /// <summary>
    /// Maximum number of parallel compilation jobs.
    /// </summary>
    public int? MaxParallelActions { get; set; }
    
    /// <summary>
    /// Whether to enable link-time optimization.
    /// </summary>
    public bool EnableLTO { get; set; } = false;
    
    /// <summary>
    /// Whether to generate debug symbols.
    /// </summary>
    public bool GenerateDebugInfo { get; set; } = true;
    
    protected TargetRules(BuildContext context)
    {
        Context = context;
        Name = GetType().Name.Replace("Target", "");
    }
    
    /// <summary>
    /// Override to add platform-specific configuration.
    /// </summary>
    protected void ConfigureForPlatform(TargetPlatform platform, Action configuration)
    {
        if (Context.Platform == platform)
        {
            configuration();
        }
    }
    
    /// <summary>
    /// Override to add configuration-specific settings.
    /// </summary>
    protected void ConfigureForConfiguration(BuildConfiguration config, Action configuration)
    {
        if (Context.Configuration == config)
        {
            configuration();
        }
    }
}

/// <summary>
/// Represents a build step command.
/// </summary>
public sealed class BuildStep
{
    public required string Description { get; init; }
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string> Environment { get; init; } = [];
    public bool ContinueOnError { get; init; } = false;
}
