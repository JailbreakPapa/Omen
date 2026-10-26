// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Build;
using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Base class for module definitions. Inherit from this class in your .module.cs files.
/// Inspired by Unreal Engine's ModuleRules pattern.
///
/// A subclass may optionally declare `public static TargetMatrix SupportedTargets => ...`,
/// but unlike TargetRules/PluginRules this is never resolved into a matrix of its own - it's a
/// per-combination membership gate. For each ResolvedTarget the owning target resolves to,
/// BuildOrchestrator checks whether this module's SupportedTargets (if declared) contains
/// that combination; if not, the module is silently excluded from that combination's action
/// graph only (present in others where it's included). A module with no SupportedTargets
/// participates in every combination, unchanged from today.
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
    /// The plugin this module was synthesized from (see PluginFlavorModuleRules and
    /// CompiledRules.CreateModuleRules), or null for a hand-written ModuleRules. This is what
    /// PluginEnablement-based filtering (BuildOrchestrator/ProjectGenerationOrchestrator) uses to
    /// tell a `Plugin::*` target O3DE's `ly_enable_plugins` would govern apart from the target's own
    /// hand-written code, which stays included regardless of any enabled-plugins list - internal
    /// setter because only PluginFlavorModuleRules (same assembly) is ever allowed to set it.
    /// </summary>
    public string? PluginName { get; internal set; }

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
    /// Modules this module (and anything reachable through its dependency closure) must
    /// never depend on, with a mandatory reason. Checked by <see cref="LayeringValidator"/>.
    /// </summary>
    public List<(string ModuleName, string Reason)> ForbiddenDependencies { get; } = [];

    /// <summary>
    /// O3DE's RUNTIME_DEPENDENCIES - names of targets/plugins that must be present at runtime
    /// (loaded, or sitting next to this module's output) but this module never links against
    /// or compiles with. Functional, within one limit: <see cref="Graph.ActionGraphBuilder"/>
    /// emits a Copy action staging the dependency's built shared-library artifact next to a
    /// target's output whenever the name resolves to a SharedLibrary module that is also part
    /// of the same Build() call - an external target Omen doesn't itself compile (the common
    /// case for cross-plugin/cross-repo entries like "Legacy::EditorCommon") is recorded here for
    /// inspection but produces no action, since Omen has no artifact path for it. An optional
    /// "Plugin::" prefix (O3DE's own target-alias syntax) is stripped before that lookup.
    /// </summary>
    public List<string> RuntimeDependencies { get; } = [];

    /// <summary>
    /// Code-generation steps that run before this module compiles - Omen's
    /// add_custom_command equivalent (the shape AzAutoGen, .setreg generation, or any
    /// generate-then-compile tool needs). Each step becomes a real action in the graph:
    /// its Outputs are tracked (timestamp/digest incrementality applies), every compile in
    /// this module orders after every step (generated headers exist before anything
    /// includes them), and declared outputs ending in .cpp/.c are themselves compiled and
    /// linked into the module. When any step is declared, the module's intermediate
    /// directory (where relative Outputs land) is added to its own include paths.
    /// </summary>
    public List<CodeGenStep> CodeGenSteps { get; } = [];

    /// <summary>
    /// Whether this module's NF_CLASS-annotated headers get their Reflect() generated - see
    /// <see cref="Reflect.ReflectCodeGenSynthesizer"/>. On by default because it costs a module
    /// with no annotated header only a text scan, and an annotated header that is silently not
    /// generated for fails at link time with no clue why.
    /// </summary>
    public bool EnableReflectCodeGen { get; set; } = true;

    /// <summary>
    /// Absolute paths of files (prebuilt DLLs, tools) that must sit next to the consuming
    /// target's output at runtime - a vcpkg port's bin/*.dll, an O3DE package's
    /// ly_add_target_files entries. Unlike <see cref="RuntimeDependencies"/> (which names
    /// modules), these are concrete files; ActionGraphBuilder emits a Copy action per file
    /// into the target's output directory whenever this module is in the build.
    /// </summary>
    public List<string> RuntimeFiles { get; } = [];

    /// <summary>
    /// O3DE's `3rdParty::` BUILD_DEPENDENCIES entries (e.g. "3rdParty::Lua", "3rdParty::zstd",
    /// "3rdParty::Qt::Core") - names O3DE resolves through its own 3rdParty manifest and
    /// find_package() calls to real include paths and link inputs. Omen has no equivalent
    /// manifest or probe, so this is a recorded-only declaration: it exists so a converted
    /// module can name every real dependency (see AzCore.module.cs's comment for the case that
    /// motivated this - a module whose PublicDependencies/PrivateDependencies were empty not
    /// because it has no dependencies, but because all of them were 3rdParty:: targets with
    /// nowhere to go), but nothing here resolves an entry to an include path or a link input,
    /// and nothing here is emitted by any generator. Deliberately NOT PublicDependencies/
    /// PrivateDependencies: those lists are walked by ActionGraphBuilder to link this module
    /// against other Omen modules, and a 3rdParty:: name dropped in there would silently resolve
    /// to nothing at link time (see ActionGraphBuilder.BuildLinkAction's comment on unknown
    /// dependency names). Qt is the one 3rdParty family Omen already resolves for real - see
    /// QtVersion/QtModules/UseQt below - so "3rdParty::Qt::Widgets" belongs in UseQt(...), not
    /// here.
    /// </summary>
    public List<string> ThirdPartyDependencies { get; } = [];

    /// <summary>
    /// Every module this one depends on, of whatever kind - the single list the graph walks.
    ///
    /// ThirdPartyDependencies is in it because a third-party package IS a module in Omen: a
    /// vendored HeaderOnly module under 3rdParty/ that compiles nothing and carries the
    /// package's include paths and prebuilt library. It was recorded-only for exactly as long
    /// as there was nothing for it to point at, which is why AzCore's
    /// ThirdPartyDependencies.Add("3rdParty::RapidJSON") resolved to nothing and the build
    /// failed on rapidjson/rapidjson.h.
    /// </summary>
    public IEnumerable<string> AllDependencies =>
        PublicDependencies.Concat(PrivateDependencies).Concat(ThirdPartyDependencies);

    /// <summary>
    /// O3DE PAL trait names (e.g. "PAL_TRAIT_BUILD_TESTS_SUPPORTED",
    /// "PAL_TRAIT_BUILD_HOST_TOOLS") this target was declared under a CMake `if(...)` for.
    /// Empty means unconditional (today's behavior, unchanged). Non-empty means "only keep this
    /// module in the compiled set when every named trait holds" - mirrors
    /// <see cref="PluginFlavor.RequiredTraits"/> for the non-plugin targets under Code/ that
    /// <see cref="PluginRules"/> doesn't cover; see <see cref="TraitGating.IsSatisfied"/> for the
    /// exact rule (an unknown trait - the common case, since Omen has no PAL-file reader -
    /// evaluates as satisfied). Checked by CompiledRules.CreateModuleRules for every
    /// hand-written ModuleRules, the same place PluginFlavor's gate is checked for synthesized ones.
    /// </summary>
    public List<string> RequiredTraits { get; } = [];

    /// <summary>
    /// O3DE's PLATFORM_INCLUDE_FILES entries this target was declared with (e.g.
    /// "${pal_dir}/platform_${PAL_PLATFORM_NAME_LOWERCASE}.cmake") - per-platform CMake
    /// fragments O3DE `include()`s into the target's own CMakeLists.txt at configure time.
    /// Recorded-only, same treatment as <see cref="ThirdPartyDependencies"/> and mirroring
    /// <see cref="PluginFlavor.PlatformIncludeFiles"/>: real fragments range from empty (the common
    /// case in the engine tree) to a handful of recurring `set(LY_BUILD_DEPENDENCIES/
    /// LY_COMPILE_OPTIONS/LY_COMPILE_DEFINITIONS/...)` blocks to genuinely open-ended CMake
    /// (dynamic `find_library` results, custom code-generation scripts, PAL_TRAIT variable
    /// definitions) - Omen has no CMake evaluator, so it cannot safely execute a fragment and
    /// fold its settings into this module's own lists without risking a silently wrong
    /// translation. Nothing reads or executes the fragment's contents; it exists purely so a
    /// converted module can name every real per-platform file it referenced.
    /// </summary>
    public List<string> PlatformIncludeFiles { get; } = [];

    /// <summary>
    /// O3DE's `set_target_properties(... PROPERTIES O3DE_PRIVATE_TARGET TRUE)` - marks this
    /// target as an internal implementation detail never installed/exported publicly. Forwarded
    /// from PluginFlavor.IsPrivateTarget unchanged. Functional within one limit, same shape as
    /// RuntimeDependencies above: ProjectGenerationOrchestrator.GenerateCMakeAsync emits the
    /// same set_target_properties call back out when this is true, but Omen has no install/
    /// export step of its own to actually consume the property downstream of that.
    /// </summary>
    public bool IsPrivateTarget { get; set; }

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

    /// <summary>
    /// Explicit list of source files (relative to <see cref="SourceDirectory"/>, or to
    /// <c>Source/{Name}</c> when that is unset), mirroring O3DE's per-target FILES_CMAKE
    /// list. When non-empty this is authoritative and <see cref="SourceDirectory"/> is
    /// NOT globbed - only these files participate, even if the directory holds more (e.g.
    /// several O3DE targets sharing one Source/ folder via different FILES_CMAKE lists).
    /// When empty, behavior is unchanged: <see cref="SourceDirectory"/> is globbed.
    /// </summary>
    public List<string> SourceFiles { get; } = [];

    /// <summary>
    /// Resolves this module's source files to full paths, optionally filtered by
    /// extension (e.g. ".cpp", ".h" - case-insensitive, dot included). Honors the
    /// <see cref="SourceFiles"/> vs <see cref="SourceDirectory"/> precedence rule: an
    /// explicit, non-empty <see cref="SourceFiles"/> list is used as-is (no directory
    /// globbing) - but only for extensions it actually contains an entry for.
    ///
    /// <see cref="SourceFiles"/> exists to hand-pick which translation units get compiled
    /// (e.g. excluding Private/Tests/*.cpp so a standalone self-check's own main() never
    /// links into the module) - it says nothing about the module's headers, which never
    /// cause the duplicate-symbol/duplicate-main problem a stray .cpp does. A module that
    /// sets SourceFiles to a .cpp-only list still wants ResolveSourceFiles(root, ".h") to
    /// glob SourceDirectory normally; treating the .cpp-only list as authoritative for
    /// headers too silently produces zero ClInclude items for every real header the module
    /// has. Asking for an extension actually present in SourceFiles still takes the
    /// explicit list, unchanged from before this comment.
    /// </summary>
    public IReadOnlyList<string> ResolveSourceFiles(string projectRoot, params string[] extensions)
    {
        var sourceDir = Path.Combine(projectRoot, SourceDirectory ?? $"Source/{Name}");

        // An explicit source list is authoritative for every extension, including the ones it
        // has none of: a module listing only .cpp files has no headers to moc. Falling through to
        // the directory glob here handed AssetProcessorBatch (two .cpp files, SourceDirectory at the
        // Asset Processor root) every Q_OBJECT header in the tree, unit tests included.
        if (SourceFiles.Count > 0)
        {
            return SourceFiles
                .Select(f => Path.Combine(sourceDir, f))
                .Where(f => extensions.Length == 0 || extensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!Directory.Exists(sourceDir))
        {
            return [];
        }

        // Distinct on both paths. A file named twice is never meaningfully two files: the graph
        // would emit two compile actions writing one .obj, which race and fail with "Permission
        // denied", and the .vcxproj would list it twice, which MSBuild refuses to load outright
        // ("Cannot load project with duplicated project items"). The converter left 1,172 such
        // lines across 35 rule files, so this is a live condition rather than a hypothetical.
        return extensions
            .SelectMany(ext => Directory.GetFiles(sourceDir, $"*{ext}", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// When set, this module is linked as its own independent binary (its object files
    /// are archived/linked separately) instead of folding its objects into the target's
    /// link. Dependents link against the resulting artifact rather than absorbing its
    /// objects directly. When null (the default), behavior is unchanged from today.
    /// </summary>
    public TargetType? BinaryType { get; set; }

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

    /// <summary>
    /// Whether this C# module is a WPF application/library. Sets the generated csproj's
    /// &lt;UseWPF&gt; to true, appends "-windows" to the target framework moniker (WPF requires
    /// the Windows-specific TFM), defaults the output type to WinExe for Runtime/Editor modules
    /// so the app doesn't open a console window alongside its UI, and treats every ".xaml" file
    /// under the module's source as a Page item (or an ApplicationDefinition item, for a file
    /// literally named "App.xaml") instead of a plain compile input.
    /// </summary>
    public bool UseWPF { get; set; }

    // ============== Qt Specific Properties ==============

    /// <summary>
    /// Qt version to use.
    /// </summary>
    public QtVersion QtVersion { get; set; } = QtVersion.None;

    /// <summary>
    /// Qt modules to link (e.g., "Core", "Widgets", "Gui", "Network").
    /// </summary>
    /// <summary>
    /// Include roots that belong to somebody else: warnings from headers found here are not this
    /// build's to fix, so they compile under /external:W0 rather than /W4 /WX.
    ///
    /// <see cref="ModuleType.ThirdParty"/> already implies this for a whole module, but Qt is not
    /// a module - its roots are attached to whichever module asked for Qt, and Qt 5.15's
    /// qpixelformat.h fails a /WX build on C5054 from inside code nobody here wrote.
    /// </summary>
    public List<string> PublicExternalIncludePaths { get; } = [];

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
    /// Keeps the module out of the generated .sln/.slnx. Its project file is still written.
    /// </summary>
    public bool HideFromSolution { get; set; }

    /// <summary>
    /// Whether this is a C# project (convenience property).
    /// </summary>
    public bool IsCSharpProject => Language == ModuleLanguage.CSharp;

    protected ModuleRules(BuildContext context) : this(context, explicitName: null) { }

    /// <summary>
    /// Used by generated module wrappers (e.g. a plugin flavor, Task 10) that need a Name
    /// other than the declaring class's own name. Hand-written .module.cs files should
    /// keep using the single-argument constructor.
    /// </summary>
    protected ModuleRules(BuildContext context, string? explicitName)
    {
        Context = context;
        Name = explicitName ?? GetType().Name;
        RuleFilePath = context.RuleFileFor(GetType());
    }

    /// <summary>
    /// The rule file this module was declared in (its *.module.cs, or the *.plugin.cs for a
    /// synthesized plugin flavor), so a generated IDE project can list it beside the sources.
    /// Null for a module built without compiled rules.
    /// </summary>
    public string? RuleFilePath { get; protected set; }

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

    /// <summary>
    /// Runs O3DE's AzAutoGen expansion over this module's data files - the equivalent of
    /// ly_add_autogen's AUTOGEN_RULES. Each rule is "&lt;data glob&gt;,&lt;template&gt;,&lt;output pattern&gt;",
    /// e.g. <c>*.AutoPackets.xml,AutoPackets_Header.jinja,$path/$fileprefix.AutoPackets.h</c>.
    ///
    /// The set of files a rule expands into is a property of the data and the templates, not of
    /// the rule text, so it cannot be derived here: a template is free to emit one file per XML
    /// element. AzAutoGen answers that question itself with --dryrun, so this asks it, exactly as
    /// LyAutoGen.cmake does at configure time. That costs one Python process per module that
    /// calls this, and buys outputs the graph can order on rather than guess at.
    ///
    /// Call this AFTER the module's SourceFiles list: the inputs are its .xml/.json/.jinja
    /// entries, matching CMake's filter over ALLFILES.
    /// </summary>
    protected void AddAutoGen(params string[] expansionRules)
    {
        if (expansionRules.Length == 0) return;

        // SourceDirectory is still rule-relative here - RuleCompiler absolutises it only after
        // the constructor returns.
        var relativeSourceDirectory = SourceDirectory ?? $"Source/{Name}";
        var moduleDir = RuleFilePath is not null
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RuleFilePath)!, relativeSourceDirectory))
            : Context.ResolveRulePath(GetType(), relativeSourceDirectory);
        string[] dataExtensions = [".xml", ".json", ".jinja"];

        var inputs = (SourceFiles.Count > 0
                ? SourceFiles.Select(f => Path.Combine(moduleDir, f))
                : Directory.Exists(moduleDir)
                    ? Directory.EnumerateFiles(moduleDir, "*", SearchOption.AllDirectories)
                    : [])
            .Where(f => dataExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFullPath)
            .ToList();

        if (inputs.Count == 0) return;

        var script = Path.Combine(Context.ProjectRoot, "cmake", "AzAutoGen.py");
        var azcgDir = Path.Combine(Context.IntermediateDirectory, ModulePathName.Sanitize(Name), "Azcg");
        var cacheDir = Path.Combine(azcgDir, "TemplateCache");
        var outputDir = Path.Combine(azcgDir, "Generated");
        var inputListFile = Path.Combine(azcgDir, "input_files.txt");

        // AzAutoGen reads its input list from a file rather than the command line, and the dryrun
        // below reads it immediately, so it has to exist now.
        Directory.CreateDirectory(azcgDir);
        File.WriteAllText(inputListFile, string.Join(";", inputs));

        var rules = string.Join(";", expansionRules);
        string Arguments(bool dryRun) => string.Join(" ",
            $"\"{script}\"",
            dryRun ? "-n" : "--prune",
            $"\"{Name}\"",
            $"\"{cacheDir}\"",
            $"\"{outputDir}\"",
            $"\"{moduleDir}\"",
            $"\"{inputListFile}\"",
            $"\"{rules}\"");

        var outputs = RunAutoGenDryRun(Arguments(dryRun: true));

        // PUBLIC, like ly_add_autogen: dependents include the generated headers too.
        PublicIncludePaths.Add(outputDir);
        CodeGenSteps.Add(new CodeGenStep
        {
            Description = $"AutoGen {Name}",
            Command = $"python {Arguments(dryRun: false)}",
            Inputs = [.. inputs, script],
            Outputs = outputs
        });
    }

    private static List<string> RunAutoGenDryRun(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "python",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Without this, every module construction that calls AddAutoGen flashes a real
            // console window - the GUI (no console of its own) spawning a console-subsystem
            // child otherwise gets one allocated for it. Every other process spawn in this
            // codebase already sets this; this one was missed.
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start python for AzAutoGen.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"AzAutoGen expansion failed:{Environment.NewLine}{stderr}");
        }

        return stdout.Trim()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}

/// <summary>
/// One code-generation command in a module's build - see <see cref="ModuleRules.CodeGenSteps"/>.
/// Command, Inputs, and Outputs may use the tokens <c>$(ModuleDir)</c> (the module's source
/// directory), <c>$(IntermediateDir)</c> (the module's intermediate directory),
/// <c>$(ProjectRoot)</c>, and <c>$(OutputDir)</c>. Relative Inputs resolve against the
/// module's source directory; relative Outputs against the module's intermediate directory.
/// The command runs through the platform shell from the project root.
/// </summary>
public sealed class CodeGenStep
{
    public required string Description { get; init; }
    public required string Command { get; init; }

    /// <summary>Files the command reads - editing one reruns the step (and everything after it).</summary>
    public List<string> Inputs { get; init; } = [];

    /// <summary>Files the command writes. Required, non-empty: outputs are what the graph
    /// tracks, orders on, and (for .cpp/.c entries) compiles into the module.</summary>
    public List<string> Outputs { get; init; } = [];

    /// <summary>
    /// Whether generated .cpp/.c outputs become translation units of their own. False for a file
    /// that an existing source #includes - a moc file included by its own .cpp is part of that
    /// translation unit, and compiling it separately as well defines every symbol in it twice.
    /// </summary>
    public bool CompileGeneratedSources { get; init; } = true;
}
