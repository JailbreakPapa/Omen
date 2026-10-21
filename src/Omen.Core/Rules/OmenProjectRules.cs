// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Options;

namespace Omen.Core.Rules;

/// <summary>
/// Base class for the single root project definition file (a `*.omenproject.cs` file living
/// directly in the project root - not nested, never globbed recursively). Inherit from this
/// class to declare project identity/version and, most importantly, which subdirectories
/// RuleCompiler is allowed to scan for `.module.cs`/`.target.cs`/`.plugin.cs` files - mirroring
/// what O3DE's root CMakeLists.txt does with `project(...)` + its ordered `add_subdirectory`
/// calls, instead of RuleCompiler's default behavior of globbing the entire project tree.
///
/// Unlike TargetRules/PluginRules/ModuleRules, this has no BuildContext - project structure
/// (which folders exist, which are scanned) is resolved once, before any Platform/Architecture/
/// Configuration is chosen, exactly like RuleCompiler.CompiledRules.FindTargetType() reads
/// SupportedTargets by reflection before a BuildContext exists to construct one.
///
/// Optional: a project with no `*.omenproject.cs` file falls back to RuleCompiler's original
/// whole-tree glob, so every existing project keeps compiling unchanged.
/// </summary>
public abstract class OmenProjectRules
{
    /// <summary>
    /// Name of the project (derived from the class name by convention, e.g. "FooProject" ->
    /// "Foo" - mirrors TargetRules/PluginRules's own Name convention).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Project version. O3DE's root CMakeLists.txt sets this via
    /// `project(O3DE LANGUAGES C CXX VERSION ${...})`; recorded-only today, nothing in Omen
    /// enforces or compares it.
    /// </summary>
    public string Version { get; set; } = "0.0.0";

    /// <summary>
    /// The generated solution's file name (without ".sln"). Unset means the project's own
    /// <see cref="Name"/>; a project with no *.omenproject.cs at all falls back to the
    /// target's name, the pre-existing behavior.
    /// </summary>
    public string? SolutionName { get; set; }

    /// <summary>
    /// Ordered list of directories (relative to the project root) RuleCompiler scans for rule
    /// files - O3DE's root CMakeLists.txt `add_subdirectory(Code)`, `add_subdirectory(Plugins)`,
    /// etc. Order is preserved here for a generator to reproduce the same add_subdirectory
    /// sequence, but does not affect which types RuleCompiler
    /// discovers - unlike CMake's configure-time target visibility, every rule file across every
    /// root is compiled into one assembly and reflected over together, so declaration order
    /// between roots has no effect on correctness.
    /// </summary>
    public List<string> SourceRoots { get; } = [];

    /// <summary>
    /// Additional directories to scan for rule files that live outside the project tree
    /// (absolute paths), or project-relative paths outside <see cref="SourceRoots"/> - O3DE's
    /// externally-registered plugin paths from engine.json/the o3de manifest
    /// (`add_o3de_manifest_json_external_subdirectories()` /
    /// `add_engine_json_external_subdirectories()` in cmake/Subdirectories.cmake). Omen does not
    /// parse those manifest formats itself (see the report's "deferred" section) - a generator
    /// is expected to resolve them ahead of time and emit the resulting paths here directly.
    /// </summary>
    public List<string> ExternalSubdirectories { get; } = [];

    /// <summary>
    /// Source dependencies fetched at rule-discovery time - Omen's CMake-FetchContent
    /// equivalent. Each lands in <c>External/&lt;Name&gt;</c> under the project root (add that
    /// folder to .gitignore) and automatically joins the rule-file scan roots, so a fetched
    /// tree carrying its own *.module.cs files participates directly; a plain C++ library is
    /// consumed by writing a module whose SourceDirectory points into it. See
    /// <see cref="ContentFetch"/> for the git/archive source options.
    /// </summary>
    public List<ContentFetch> FetchContent { get; } = [];

    /// <summary>
    /// Directories (relative to the project root, or absolute) whose rule files are ignored even
    /// though they sit under a <see cref="SourceRoots"/> entry.
    ///
    /// A tree of any size has rule files that are not part of the build: fixtures, samples, and
    /// generated proof output. O3DE's own checkout keeps OmenConvert's proof-of-conversion output
    /// under Tools/, which is a source root - scanning both put a second definition of nine plugin
    /// classes into the rule compilation and the whole build failed with "already contains a
    /// definition for CameraPlugin" before it could do anything else. CMake never hits this because
    /// only an explicit add_subdirectory pulls a directory in; Omen globs, so it needs a way to
    /// say no.
    /// </summary>
    public List<string> ExcludedDirectories { get; } = [];

    /// <summary>
    /// The project's enabled-plugin list - O3DE's `ly_enable_plugins(PLUGINS ...)` - resolved transitively
    /// through <see cref="PluginEnablement.ResolveEnabledPluginNames"/> and then to concrete build
    /// targets through <see cref="PluginEnablement.ResolveEnabledTargets"/>. Null (the default) means
    /// no list was ever declared: every plugin in the compiled tree stays active, exactly as before
    /// this property existed - this is what keeps every pre-existing project (and the ~230
    /// generated rule files with no opinion on plugin enablement) building unchanged. Only a project
    /// that explicitly sets this list opts into plugin filtering; an explicitly empty list opts into
    /// "no plugins enabled" rather than "unfiltered".
    /// </summary>
    public List<string>? EnabledPlugins { get; set; }

    /// <summary>
    /// Which variant <see cref="EnabledPlugins"/> resolves against - O3DE's per-project-variant
    /// `ly_enable_plugins(VARIANTS ...)` (a Clients app vs. a Servers app enabling different flavors
    /// of the same plugin list). Only consulted when <see cref="EnabledPlugins"/> is non-null.
    /// </summary>
    public PluginVariant Variant { get; set; } = PluginVariant.Unified;

    /// <summary>
    /// PAL trait values per target platform - O3DE's <c>ly_set(PAL_TRAIT_X TRUE|FALSE)</c> calls
    /// in <c>cmake/Platform/&lt;Platform&gt;/PAL_&lt;platform&gt;.cmake</c> and each plugin's own
    /// <c>Code/Platform/&lt;Platform&gt;/platform_&lt;platform&gt;.cmake</c>.
    ///
    /// This is the table <see cref="TraitGating"/> was written against and nothing has ever
    /// filled: without it every RequiredTraits gate is "unknown", which defaults to satisfied,
    /// so a Tools-only or tests-only module is included on every platform. A trait recorded here
    /// as false does exclude its module.
    /// </summary>
    public Dictionary<TargetPlatform, Dictionary<string, bool>> PlatformTraits { get; } = new();

    /// <summary>The (created-on-demand) trait table for one platform.</summary>
    public Dictionary<string, bool> PlatformTraitsFor(TargetPlatform platform)
    {
        if (!PlatformTraits.TryGetValue(platform, out var traits))
        {
            traits = new Dictionary<string, bool>(StringComparer.Ordinal);
            PlatformTraits[platform] = traits;
        }

        return traits;
    }

    /// <summary>
    /// Unity builds for every target, when a build doesn't override it. Null leaves each
    /// target's own TargetRules.UseUnityBuild in charge - CMake's LY_UNITY_BUILD maps here.
    /// </summary>
    public bool? UnityBuild { get; set; }

    /// <summary>Precompiled headers for every target, when a build doesn't override it.</summary>
    public bool? PrecompiledHeaders { get; set; }

    /// <summary>
    /// Forces every target's link type - CMake's LY_MONOLITHIC_GAME. Null leaves each target's
    /// own TargetRules.LinkType alone.
    /// </summary>
    public LinkType? LinkTypeOverride { get; set; }

    /// <summary>
    /// Default parallel action count, when a build doesn't pass -j. Null means one per core.
    /// CMake spells this LY_PARALLEL_COMPILE_JOBS.
    /// </summary>
    public int? MaxParallelActions { get; set; }

    /// <summary>
    /// Engine-wide compiler/linker configuration, per compiler family - see
    /// <see cref="GlobalToolchainOptions"/>. Empty by default, which means Omen contributes
    /// nothing beyond what each toolchain already emits, exactly as before this existed.
    /// </summary>
    public Dictionary<CompilerFamily, GlobalToolchainOptions> ToolchainOptions { get; } = new();

    /// <summary>The (created-on-demand) options for one compiler family, so a rule file can
    /// write <c>ToolchainOptionsFor(CompilerFamily.Msvc).Common.CompilerOptions.Add(...)</c>
    /// without null checks.</summary>
    public GlobalToolchainOptions ToolchainOptionsFor(CompilerFamily family)
    {
        if (!ToolchainOptions.TryGetValue(family, out var options))
        {
            options = new GlobalToolchainOptions();
            ToolchainOptions[family] = options;
        }

        return options;
    }

    protected OmenProjectRules()
    {
        Name = GetType().Name.Replace("Project", "");
        _optionValues = LoadingOptionValues ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// The saved option values RuleCompiler hands the project definition while constructing it.
    /// The definition takes no BuildContext (project structure is resolved before any platform
    /// or configuration exists), so the cache arrives this way instead of as a parameter.
    /// </summary>
    /// <summary>
    /// Thread-local: two projects can be loaded at once (a GUI Configure beside a build, or
    /// parallel tests), and a plain static let one load's values - or its cleanup - land in
    /// the other's definition.
    /// </summary>
    [ThreadStatic]
    internal static IReadOnlyDictionary<string, string>? LoadingOptionValues;

    private readonly IReadOnlyDictionary<string, string> _optionValues;

    /// <summary>
    /// Project-wide build options - CMake's option() and set(... CACHE ...), the entries
    /// cmake-gui lists. Declared here rather than through BuildOptions.Declare because these
    /// belong to the project as a whole, and the project definition is evaluated before any
    /// BuildContext exists. RuleCompiler merges them into every context's DeclaredOptions, so
    /// the GUI's Options panel and any rule file see one list.
    /// </summary>
    public List<BuildOptionDeclaration> Options { get; } = [];

    /// <summary>Declares a boolean option and returns its effective value (saved value, else default).</summary>
    protected bool DeclareOption(string name, string description, bool defaultValue, bool advanced = false) =>
        Declare(name, description, BuildOptionType.Bool, defaultValue ? "true" : "false", advanced)
            is var v && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1"
                         || v.Equals("on", StringComparison.OrdinalIgnoreCase)
                         || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>Declares a string option and returns its effective value.</summary>
    protected string DeclareOption(string name, string description, string defaultValue, bool advanced = false) =>
        Declare(name, description, BuildOptionType.String, defaultValue, advanced);

    /// <summary>Declares an integer option and returns its effective value.</summary>
    protected int DeclareOption(string name, string description, int defaultValue, bool advanced = false) =>
        int.TryParse(Declare(name, description, BuildOptionType.Int, defaultValue.ToString(), advanced), out var parsed)
            ? parsed : defaultValue;

    /// <summary>Declares a filesystem-path option and returns its effective value.</summary>
    protected string DeclareOptionPath(string name, string description, string defaultValue, bool advanced = false) =>
        Declare(name, description, BuildOptionType.Path, defaultValue, advanced);

    /// <summary>Declares an option limited to a fixed set of values (CMake's STRINGS property).</summary>
    protected string DeclareOptionEnum(string name, string description, string[] allowedValues, string defaultValue, bool advanced = false)
    {
        var value = Declare(name, description, BuildOptionType.Enum, defaultValue, advanced, allowedValues);
        return allowedValues.Contains(value, StringComparer.Ordinal) ? value : defaultValue;
    }

    private string Declare(string name, string description, BuildOptionType type, string defaultValue, bool advanced, string[]? allowedValues = null)
    {
        var existing = Options.FirstOrDefault(o => o.Name == name);
        if (existing != null)
            return existing.EffectiveValue;

        var effective = _optionValues.TryGetValue(name, out var saved) ? saved : defaultValue;
        Options.Add(new BuildOptionDeclaration
        {
            Name = name,
            Description = description,
            Type = type,
            DefaultValue = defaultValue,
            EffectiveValue = effective,
            IsAdvanced = advanced,
            AllowedValues = allowedValues
        });
        return effective;
    }
}
