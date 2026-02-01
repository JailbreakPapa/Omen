// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Base class for module definitions. Inherit from this class in your .module.cs files.
/// Inspired by Unreal Engine's ModuleRules pattern.
/// </summary>
public abstract class ModuleRules
{
    /// <summary>
    /// The build context for this module.
    /// </summary>
    protected BuildContext Context { get; }

    /// <summary>
    /// Name of the module (derived from class name by convention).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Type of module.
    /// </summary>
    public ModuleType Type { get; set; } = ModuleType.Runtime;

    /// <summary>
    /// Programming language for the module.
    /// </summary>
    public ModuleLanguage Language { get; set; } = ModuleLanguage.Cpp;

    /// <summary>
    /// Precompiled header usage mode.
    /// </summary>
    public PCHUsage PCHUsage { get; set; } = PCHUsage.UseExplicitOrShared;

    /// <summary>
    /// Public dependencies - modules that are required both for compilation and linking.
    /// These are propagated to dependent modules.
    /// </summary>
    public List<string> PublicDependencies { get; } = [];

    /// <summary>
    /// Private dependencies - modules required only by this module's implementation.
    /// Not propagated to dependent modules.
    /// </summary>
    public List<string> PrivateDependencies { get; } = [];

    /// <summary>
    /// Public include paths exposed to dependent modules.
    /// </summary>
    public List<string> PublicIncludePaths { get; } = [];

    /// <summary>
    /// Private include paths used only by this module.
    /// </summary>
    public List<string> PrivateIncludePaths { get; } = [];

    /// <summary>
    /// Public preprocessor definitions exposed to dependent modules.
    /// </summary>
    public List<string> PublicDefinitions { get; } = [];

    /// <summary>
    /// Private preprocessor definitions used only by this module.
    /// </summary>
    public List<string> PrivateDefinitions { get; } = [];

    /// <summary>
    /// Additional public libraries to link against.
    /// </summary>
    public List<string> PublicLibraries { get; } = [];

    /// <summary>
    /// Additional private libraries to link against.
    /// </summary>
    public List<string> PrivateLibraries { get; } = [];

    /// <summary>
    /// System/framework libraries (e.g., "user32" on Windows, "pthread" on Linux).
    /// </summary>
    public List<string> PublicSystemLibraries { get; } = [];

    /// <summary>
    /// Public framework dependencies (macOS/iOS).
    /// </summary>
    public List<string> PublicFrameworks { get; } = [];

    /// <summary>
    /// Additional compiler flags.
    /// </summary>
    public List<string> AdditionalCompilerFlags { get; } = [];

    /// <summary>
    /// Additional linker flags.
    /// </summary>
    public List<string> AdditionalLinkerFlags { get; } = [];

    /// <summary>
    /// Whether to enable RTTI.
    /// </summary>
    public bool EnableRTTI { get; set; } = true;

    /// <summary>
    /// Whether to enable exceptions.
    /// </summary>
    public bool EnableExceptions { get; set; } = true;

    /// <summary>
    /// Optimization level override for this module.
    /// </summary>
    public OptimizationLevel? OptimizeCode { get; set; }

    /// <summary>
    /// Warning level for this module.
    /// </summary>
    public WarningLevel WarningLevel { get; set; } = WarningLevel.Level4;

    /// <summary>
    /// Whether to treat warnings as errors.
    /// </summary>
    public bool TreatWarningsAsErrors { get; set; } = false;

    /// <summary>
    /// C++ standard to use.
    /// </summary>
    public CppStandard CppStandard { get; set; } = CppStandard.Cpp20;

    /// <summary>
    /// C standard to use.
    /// </summary>
    public CStandard CStandard { get; set; } = CStandard.C17;

    /// <summary>
    /// Whether this module should be built as a unity build.
    /// </summary>
    public bool? UseUnityBuild { get; set; }

    /// <summary>
    /// Source files to exclude from unity builds.
    /// </summary>
    public List<string> UnityBuildExclusions { get; } = [];

    /// <summary>
    /// Precompiled header file to use (relative to module source).
    /// </summary>
    public string? PrecompiledHeaderFile { get; set; }

    /// <summary>
    /// Shared PCH header file (for cross-module PCH sharing).
    /// </summary>
    public string? SharedPCHHeaderFile { get; set; }

    /// <summary>
    /// Force-included headers.
    /// </summary>
    public List<string> ForcedIncludes { get; } = [];

    /// <summary>
    /// All include paths (public + private).
    /// </summary>
    public IReadOnlyList<string> IncludePaths =>
        PublicIncludePaths.Concat(PrivateIncludePaths).ToList();

    /// <summary>
    /// Directory containing the module's source files (relative to project root).
    /// </summary>
    public string? SourceDirectory { get; set; }

    // ============== C# Specific Properties ==============

    /// <summary>
    /// C# language version (for C# modules).
    /// </summary>
    public CSharpVersion CSharpVersion { get; set; } = CSharpVersion.Latest;

    /// <summary>
    /// Target .NET framework (for C# modules).
    /// </summary>
    public DotNetFramework TargetFramework { get; set; } = DotNetFramework.Net80;

    /// <summary>
    /// NuGet package references (for C# modules).
    /// Format: "PackageName" or "PackageName/Version"
    /// </summary>
    public List<string> PackageReferences { get; } = [];

    /// <summary>
    /// Whether to enable nullable reference types (for C# modules).
    /// </summary>
    public bool EnableNullable { get; set; } = true;

    /// <summary>
    /// Whether to enable implicit usings (for C# modules).
    /// </summary>
    public bool ImplicitUsings { get; set; } = true;

    /// <summary>
    /// Assembly references (for C# modules).
    /// </summary>
    public List<string> AssemblyReferences { get; } = [];

    // ============== Qt Specific Properties ==============

    /// <summary>
    /// Qt version to use.
    /// </summary>
    public QtVersion QtVersion { get; set; } = QtVersion.None;

    /// <summary>
    /// Qt modules to link (e.g., "Core", "Widgets", "Gui", "Network").
    /// </summary>
    public List<string> QtModules { get; } = [];

    /// <summary>
    /// Qt installation path (if not using environment variable).
    /// </summary>
    public string? QtPath { get; set; }

    /// <summary>
    /// Whether to run MOC (Meta-Object Compiler).
    /// </summary>
    public bool EnableMoc { get; set; } = true;

    /// <summary>
    /// Whether to run UIC (User Interface Compiler).
    /// </summary>
    public bool EnableUic { get; set; } = true;

    /// <summary>
    /// Whether to run RCC (Resource Compiler).
    /// </summary>
    public bool EnableRcc { get; set; } = true;

    /// <summary>
    /// Whether this is a Qt project (convenience property).
    /// </summary>
    public bool IsQtProject => QtVersion != QtVersion.None;

    /// <summary>
    /// Whether this is a C# project (convenience property).
    /// </summary>
    public bool IsCSharpProject => Language == ModuleLanguage.CSharp;

    protected ModuleRules(BuildContext context)
    {
        Context = context;
        Name = GetType().Name;
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

    /// <summary>
    /// Configure Qt modules for this project.
    /// </summary>
    protected void UseQt(QtVersion version, params string[] modules)
    {
        QtVersion = version;
        QtModules.AddRange(modules);
    }
}
