// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// The build-relevant flavors an O3DE plugin can produce. A plugin declares 1-4 of these; each
/// becomes its own ModuleRules-shaped build unit once expanded (see Task 10).
/// </summary>
public enum PluginFlavorKind
{
    Static,
    Runtime,
    Editor,
    Tools,

    /// <summary>
    /// Runtime unit tests (O3DE's `${plugin_name}.Tests`, built as
    /// `${PAL_TRAIT_TEST_TARGET_TYPE}` and registered with `ly_add_googletest`). Distinct
    /// from <see cref="EditorTests"/> because the two have different sources, different
    /// dependencies (this one privately depends on the plugin's Static flavor, not Editor),
    /// and different platform/host-tools gating - one PluginFlavorKind cannot represent both
    /// without one silently overwriting the other in PluginRules.Flavors.
    /// </summary>
    Tests,

    /// <summary>
    /// Editor/tool unit tests (O3DE's `${plugin_name}.Editor.Tests`, only built under
    /// PAL_TRAIT_BUILD_HOST_TOOLS). Privately depends on the plugin's Editor flavor, the way
    /// real O3DE's `${plugin_name}.Editor.Tests` depends on `${plugin_name}.Editor.Private.Object`.
    /// Named "EditorTests" (no dot - not legal in a C# enum member) rather than mirroring the
    /// target name; PluginFlavorModuleRules special-cases the synthesized module name back to
    /// "{plugin}.Editor.Tests" so the on-disk/CTest name still matches O3DE exactly.
    /// </summary>
    EditorTests,

    /// <summary>
    /// O3DE's modern four-target plugin layout's `${plugin_name}.API` - an INTERFACE target:
    /// public headers only, compiles nothing. Consumers (in or out of the plugin) depend on
    /// this, never on <see cref="PrivateObject"/> directly. Maps to
    /// <see cref="Omen.Core.Configuration.TargetType.HeaderOnly"/> (defaulted by
    /// DefineFlavor, same treatment Static gets for StaticLibrary).
    /// </summary>
    Api,

    /// <summary>
    /// O3DE's modern-layout `${plugin_name}.Editor.API` (host-tools only) - same shape as
    /// <see cref="Api"/> but for the editor/tools side.
    /// </summary>
    EditorApi,

    /// <summary>
    /// O3DE's modern-layout `${plugin_name}.Private.Object` - a STATIC library holding the
    /// plugin's own translation units, privately depended on by the `${plugin_name}` module
    /// (and by `Tests`, per the real CMakeLists). A modern-layout plugin has no
    /// <see cref="Static"/> flavor at all - this is what its Tests/Runtime flavor's
    /// PrivateDependencies should name instead, which is what makes the earlier
    /// "rewrite .Private.Object deps onto .Static" workaround unnecessary here.
    /// </summary>
    PrivateObject,

    /// <summary>
    /// O3DE's modern-layout `${plugin_name}.Editor.Private.Object` (host-tools only) - the
    /// STATIC translation-unit library backing `${plugin_name}.Editor`, mirroring
    /// <see cref="PrivateObject"/> for the editor side.
    /// </summary>
    EditorPrivateObject,

    /// <summary>
    /// The classic-layout plugin's editor-side static library (O3DE's `${plugin_name}.Editor.Static`)
    /// - holds the editor/tool translation units, privately depended on by the plugin's
    /// <see cref="Editor"/> module the same way <see cref="Static"/> backs <see cref="Runtime"/>.
    /// Distinct from <see cref="EditorPrivateObject"/>: that one is the modern four-target
    /// layout's name for the same role (`.Editor.Private.Object`) - a plugin uses one name or the
    /// other depending on which CMakeLists shape it has, never both. Maps to
    /// <see cref="Omen.Core.Configuration.TargetType.StaticLibrary"/> by default, same
    /// treatment <see cref="Static"/> gets.
    /// </summary>
    EditorStatic,

    // --- RHI-backend-family kinds (Round 9) ---
    // Evidence: reading the real CMakeLists.txt of Atom/RHI, Atom/RHI/DX12, Atom/RHI/Vulkan,
    // Atom/RHI/Metal, Atom/RHI/Null, Atom/RHI/AGC and Atom/Feature/Common. These plugins use a
    // THIRD target-family shape (neither the classic Static/Runtime/Editor layout nor the
    // modern Api/PrivateObject layout): a graphics-backend plugin never has a bare "${plugin_name}"
    // Runtime target at all - only these. Each kind below recurs across 6-14 plugins (see
    // docs/superpowers/sdd/omen-flavor-suffix-tail-report.md's frequency table) - a real,
    // named O3DE concept, not one plugin's private business.

    /// <summary>
    /// O3DE's `${plugin_name}.Reflect` - a STATIC library holding just the reflection-registration
    /// (AZ::SerializeContext/BehaviorContext) code for a graphics-backend plugin, depended on by
    /// every other target in the same plugin's family (Private.Static, Public, Builders...) so the
    /// reflection code links exactly once. Maps to <see cref="Omen.Core.Configuration.TargetType.StaticLibrary"/>
    /// by default, same treatment <see cref="Static"/> gets.
    /// </summary>
    Reflect,

    /// <summary>
    /// O3DE's `${plugin_name}.Private` - the backend's main loadable module (built as
    /// `${PAL_TRAIT_MONOLITHIC_DRIVEN_MODULE_TYPE}`, i.e. shared unless the build is
    /// monolithic), privately depending on <see cref="PrivateStatic"/> and <see cref="Reflect"/>.
    /// Distinct from <see cref="Runtime"/>: a graphics-backend plugin (Atom_RHI_DX12,
    /// Atom_RHI_Vulkan, ...) uses this name instead of a bare "${plugin_name}" Runtime target.
    /// BinaryType is left unset by default, same as <see cref="Runtime"/>/<see cref="Editor"/> -
    /// the PAL_TRAIT is genuinely plugin/platform-specific, not something DefineFlavor can guess.
    /// </summary>
    Private,

    /// <summary>
    /// O3DE's `${plugin_name}.Private.Static` - a STATIC library holding the backend's own
    /// translation units, privately depended on by <see cref="Private"/> (and, for DX12/Vulkan,
    /// by <see cref="Interface"/>). The RHI-backend family's counterpart to <see cref="PrivateObject"/>
    /// - same role, different real O3DE name, so it needs its own kind rather than reusing
    /// PrivateObject's name (PluginFlavorNaming special-cases the dotted target name back to
    /// "{plugin}.Private.Static"). Maps to StaticLibrary by default, same treatment as PrivateObject.
    /// </summary>
    PrivateStatic,

    /// <summary>
    /// O3DE's `${plugin_name}.Interface` - a STATIC library (seen in Atom_RHI_DX12, Atom_RHI_Vulkan)
    /// exposing the concrete backend classes (e.g. RHI/Device.h) to code that legitimately needs
    /// them despite <see cref="PrivateStatic"/> keeping its own Source directory PRIVATE (the
    /// per-platform RHI testbed is the real consumer). Maps to StaticLibrary by default.
    /// </summary>
    Interface,

    /// <summary>
    /// O3DE's `${plugin_name}.Public` - a library (built as
    /// `${PAL_TRAIT_MONOLITHIC_DRIVEN_LIBRARY_TYPE}`) exposing a graphics-backend plugin's public
    /// surface to other plugins (e.g. `Plugin::Atom_RHI.Public`), as distinct from the header-only
    /// <see cref="Api"/> - this one compiles/links real code. BinaryType left unset by default,
    /// same reasoning as <see cref="Private"/>.
    /// </summary>
    Public,

    /// <summary>
    /// O3DE's `${plugin_name}.Headless` (seen in Atom_AtomBridge, Atom_Feature_Common) - a
    /// dedicated-server/headless build variant of the plugin's main module, built as
    /// `${PAL_TRAIT_MONOLITHIC_DRIVEN_MODULE_TYPE}` alongside the normal module. BinaryType
    /// left unset by default, same reasoning as <see cref="Private"/>.
    /// </summary>
    Headless,

    /// <summary>
    /// O3DE's `${plugin_name}.Builders` - a PLUGIN_MODULE, host-tools-only (every real instance is
    /// wrapped in `if(PAL_TRAIT_BUILD_HOST_TOOLS)`) asset-builder plugin. The single most
    /// common unmapped suffix in the residue report (14 occurrences across 10 plugins) - O3DE's
    /// asset-pipeline builder concept, distinct from the human-facing <see cref="Tools"/>.
    /// BinaryType left unset by default (PLUGIN_MODULE has no direct TargetType equivalent);
    /// classified as <see cref="Omen.Core.Configuration.ModuleType.Editor"/> in
    /// PluginFlavorModuleRules since it is always host-tools-gated in the real trees.
    /// </summary>
    Builders,

    /// <summary>
    /// O3DE's `${plugin_name}.Builders.Static` - the STATIC library backing <see cref="Builders"/>,
    /// mirroring how <see cref="PrivateStatic"/> backs <see cref="Private"/>. Maps to
    /// StaticLibrary by default and is classified Editor alongside Builders (same tier, matching
    /// the existing EditorPrivateObject/Editor pairing pattern).
    /// </summary>
    BuildersStatic,

    /// <summary>
    /// O3DE's `${plugin_name}.Edit` (seen in Atom_RHI, Atom_RPI) - a host-tools-only STATIC or
    /// monolithic-driven-library target (real trees use both shapes) providing editor-time
    /// support code for a graphics-backend plugin. Always wrapped in
    /// `if(PAL_TRAIT_BUILD_HOST_TOOLS)` in the real trees, so classified as
    /// <see cref="Omen.Core.Configuration.ModuleType.Editor"/> in PluginFlavorModuleRules.
    /// BinaryType left unset by default since the real shape varies per plugin.
    /// </summary>
    Edit,

    /// <summary>
    /// O3DE's `${plugin_name}.Mocks` - a HEADERONLY library of test-double headers for other
    /// plugins' tests to depend on (seen in GradientSignal, LmbrCentral, Terrain). Maps to
    /// <see cref="Omen.Core.Configuration.TargetType.HeaderOnly"/> by default, same treatment
    /// <see cref="Api"/> gets.
    /// </summary>
    Mocks,

    /// <summary>
    /// O3DE's `${plugin_name}.Headers` - a HEADERONLY public-interface library (seen in
    /// GameStateSamples, Presence, ImageProcessingAtom, Atom_Bootstrap): same role as
    /// <see cref="Api"/> (public headers only, compiles nothing) under the plugin's own naming
    /// choice rather than the modern-layout "${plugin_name}.API" name. Maps to HeaderOnly by
    /// default, same treatment Api gets.
    /// </summary>
    Headers,

    /// <summary>
    /// O3DE's `${plugin_name}.Tools.Static` - the STATIC library backing <see cref="Tools"/>
    /// (seen in LYShineToShine, Multiplayer, Shine), the same "X's static half" pattern
    /// <see cref="EditorStatic"/> is for <see cref="Editor"/>. Maps to StaticLibrary by default;
    /// classified the same as Tools (ModuleType.Runtime, matching existing unchanged behavior -
    /// Tools itself is not in the Editor bucket today, and this round doesn't change that).
    /// </summary>
    ToolsStatic,

    /// <summary>
    /// O3DE's `${plugin_name}.Tests.Static` - the STATIC library backing <see cref="Tests"/>
    /// (seen in EMotionFX, GradientSignal), the same "X's static half" pattern
    /// <see cref="EditorPrivateObject"/> is for <see cref="Editor"/>. Maps to StaticLibrary by
    /// default; classified as <see cref="Omen.Core.Configuration.ModuleType.Test"/> alongside
    /// Tests, matching the existing "static half inherits its owning tier" pattern.
    /// </summary>
    TestsStatic,

    /// <summary>
    /// Escape hatch for a target-name suffix that is genuinely one plugin's own business - not a
    /// recurring O3DE concept (e.g. ScriptCanvas's second "${plugin_name}Debugger" product family,
    /// or Atom_RHI's one-off ".Profiler"/".Profiler.RenderDoc"/".Profiler.PIX" trio). A plugin
    /// author never constructs this directly - use the <c>DefineFlavor(string, PluginModuleRole)</c>
    /// overload instead, which sets <see cref="PluginFlavor.CustomName"/> and stores the flavor in
    /// <see cref="PluginRules.CustomFlavors"/> (keyed by the literal suffix, not this one shared
    /// enum value - a single plugin can define multiple Custom flavors, which is why they can't
    /// live in the enum-keyed <see cref="PluginRules.Flavors"/> dictionary the way every other kind
    /// does). See PluginFlavorNaming's flavor-aware ModuleName overload for how the name is built.
    /// </summary>
    Custom
}

/// <summary>
/// The build-tier a <see cref="PluginFlavorKind.Custom"/> flavor belongs to, since PluginFlavorModuleRules
/// can't switch on a real PluginFlavorKind for it the way it does for every named kind. Every named
/// kind's tier is baked into PluginFlavorModuleRules's switch already; this only covers the escape
/// hatch. Mirrors the three buckets that switch already produces (see
/// <see cref="Omen.Core.Configuration.ModuleType.Editor"/>/<see cref="Omen.Core.Configuration.ModuleType.Test"/>/
/// default-Runtime), not a new concept.
/// </summary>
public enum PluginModuleRole
{
    Runtime,
    Editor,
    Test
}

/// <summary>
/// Single source of truth for the module name a PluginFlavorKind expands to. Most kinds are
/// just "{plugin}.{kind}" (matches the O3DE target name directly); EditorTests and the four
/// modern-layout kinds are where the real O3DE target name has a dot that isn't legal in a
/// C# enum member, so they're special-cased here instead of drifting between
/// PluginFlavorModuleRules and PluginAliasResolver.
/// </summary>
internal static class PluginFlavorNaming
{
    public static string ModuleName(string pluginName, PluginFlavorKind kind) => kind switch
    {
        // O3DE names a plugin's plain loadable module after the plugin itself - ly_add_target
        // NAME ${plugin_name}, not ${plugin_name}.Runtime. Without this case the fallback below
        // produced "{plugin}.Runtime", which matched no CMake target: a parity sweep found the
        // same missing/extra pair in 69 of 93 plugins, all of it this one line.
        PluginFlavorKind.Runtime => pluginName,
        PluginFlavorKind.EditorTests => $"{pluginName}.Editor.Tests",
        PluginFlavorKind.Api => $"{pluginName}.API",
        PluginFlavorKind.EditorApi => $"{pluginName}.Editor.API",
        PluginFlavorKind.PrivateObject => $"{pluginName}.Private.Object",
        PluginFlavorKind.EditorPrivateObject => $"{pluginName}.Editor.Private.Object",
        PluginFlavorKind.EditorStatic => $"{pluginName}.Editor.Static",
        PluginFlavorKind.PrivateStatic => $"{pluginName}.Private.Static",
        PluginFlavorKind.BuildersStatic => $"{pluginName}.Builders.Static",
        PluginFlavorKind.ToolsStatic => $"{pluginName}.Tools.Static",
        PluginFlavorKind.TestsStatic => $"{pluginName}.Tests.Static",
        PluginFlavorKind.Custom => throw new InvalidOperationException(
            $"PluginFlavorKind.Custom has no name from the kind alone - use the {nameof(ModuleName)}(string, PluginFlavor) overload."),
        _ => $"{pluginName}.{kind}"
    };

    /// <summary>
    /// Flavor-aware overload - the only one that can name a <see cref="PluginFlavorKind.Custom"/>
    /// flavor, since its name comes from <see cref="PluginFlavor.CustomName"/> rather than the kind.
    /// Every other kind delegates straight to <see cref="ModuleName(string, PluginFlavorKind)"/>, so
    /// existing behavior for every already-shipped kind is unchanged.
    /// </summary>
    public static string ModuleName(string pluginName, PluginFlavor flavor) =>
        flavor.Kind == PluginFlavorKind.Custom
            ? (flavor.CustomNameIsAbsolute ? flavor.CustomName! : $"{pluginName}{flavor.CustomName}")
            : ModuleName(pluginName, flavor.Kind);
}

/// <summary>
/// One buildable variant of a plugin, configured like a ModuleRules block but sharing the
/// plugin-level public dependencies pulled from plugin.json.
/// </summary>
public sealed class PluginFlavor
{
    public required PluginFlavorKind Kind { get; init; }

    /// <summary>
    /// True when <see cref="CustomName"/> is the module's WHOLE name rather than a suffix
    /// appended to the plugin name. O3DE plugins do contain targets whose names have nothing to do
    /// with the plugin's own - MiniAudio declares `O3DEMiniAudio`, OpenParticleSystem declares
    /// `SimuCore.Static` - and a suffix-only model cannot express those at all. Ignored for
    /// every non-Custom kind, whose name is fixed by the kind.
    /// </summary>
    public bool CustomNameIsAbsolute { get; init; }

    /// <summary>
    /// The literal string appended directly after the plugin name for a
    /// <see cref="PluginFlavorKind.Custom"/> flavor's module name - unused (and always null) for
    /// every other kind, whose suffix is fixed by <see cref="PluginFlavorKind"/> itself. No dot is
    /// inserted automatically (unlike the dotted PluginFlavorKind special cases in
    /// PluginFlavorNaming): real O3DE suffixes aren't consistently dotted (ScriptCanvas's second
    /// product is literally "${plugin_name}Debugger", no dot, while Atom_RHI's is
    /// "${plugin_name}.Profiler") - the caller supplies ".Profiler" or "Debugger" as needed. Set by
    /// the <c>DefineFlavor(string, PluginModuleRole)</c> overload, never by hand.
    /// </summary>
    public string? CustomName { get; init; }

    /// <summary>
    /// The build tier a <see cref="PluginFlavorKind.Custom"/> flavor belongs to - see
    /// <see cref="PluginModuleRole"/>. Ignored for every other kind: their tier is already baked
    /// into PluginFlavorModuleRules's own switch, unchanged by this property's existence.
    /// </summary>
    public PluginModuleRole Role { get; set; } = PluginModuleRole.Runtime;

    public string? SourceDirectory { get; set; }

    /// <summary>
    /// Explicit source file list for this flavor (relative to <see cref="SourceDirectory"/>),
    /// mirroring an O3DE target's FILES_CMAKE list. When non-empty, only these files are
    /// compiled - <see cref="SourceDirectory"/> is not globbed. This is what lets several
    /// flavors of the same plugin (e.g. Static/Runtime/Editor) share one Source/ directory
    /// without pulling each other's translation units in; see ModuleRules.SourceFiles.
    /// </summary>
    public List<string> SourceFiles { get; } = [];

    public List<string> PublicDependencies { get; } = [];
    public List<string> PrivateDependencies { get; } = [];
    public List<string> PublicIncludePaths { get; } = [];
    public List<string> PrivateIncludePaths { get; } = [];
    public List<string> PublicDefinitions { get; } = [];
    public List<string> PrivateDefinitions { get; } = [];
    public TargetType? BinaryType { get; set; }

    /// <summary>
    /// O3DE's `set_target_properties(... PROPERTIES O3DE_PRIVATE_TARGET TRUE)` - marks a
    /// target as an internal implementation detail never installed/exported publicly. In the
    /// real trees this is set on exactly one pair of flavors (<see cref="PluginFlavorKind.PrivateObject"/>/
    /// <see cref="PluginFlavorKind.EditorPrivateObject"/>) with exactly one value (TRUE), never
    /// as a general property bag - so it's modeled as this one bool, defaulted by
    /// <see cref="PluginRules.DefineFlavor(PluginFlavorKind)"/> for those two kinds (same pattern as BinaryType's
    /// default), not as an open-ended TARGET_PROPERTIES passthrough. Forwarded onto the
    /// synthesized module (see PluginFlavorModuleRules) and, when true, emitted back out as the
    /// same set_target_properties call by ProjectGenerationOrchestrator.GenerateCMakeAsync -
    /// Omen has no install/export step of its own, so today that emitted line has no other
    /// consumer, but it round-trips.
    /// </summary>
    public bool IsPrivateTarget { get; set; }

    /// <summary>
    /// O3DE PAL trait names (e.g. "PAL_TRAIT_BUILD_HOST_TOOLS", "PAL_TRAIT_ARCHIVE_TEST_SUPPORTED")
    /// this flavor's target was declared under a CMake `if(...)` for. Empty means unconditional
    /// (today's behavior, unchanged). Non-empty means "only expand this flavor into a module
    /// when every named trait holds" - see <see cref="TraitGating.IsSatisfied"/> for the
    /// exact rule, since Omen has no PAL file reader and most traits are therefore "unknown"
    /// rather than affirmatively true or false.
    /// </summary>
    public List<string> RequiredTraits { get; } = [];

    /// <summary>
    /// O3DE's PLATFORM_INCLUDE_FILES entries this flavor's target was declared with (e.g.
    /// "${pal_dir}/platform_${PAL_PLATFORM_NAME_LOWERCASE}.cmake") - per-platform CMake
    /// fragments O3DE `include()`s into the target's own CMakeLists.txt at configure time.
    /// Recorded-only, same treatment as <see cref="ThirdPartyDependencies"/> and for the same
    /// reason: real fragments range from empty (the common case) to a handful of recurring
    /// `set(LY_BUILD_DEPENDENCIES/LY_COMPILE_OPTIONS/LY_COMPILE_DEFINITIONS/...)` blocks to
    /// genuinely open-ended CMake (dynamic `find_library` results, custom code-generation
    /// scripts, PAL_TRAIT variable definitions) - Omen has no CMake evaluator, so it cannot
    /// safely execute a fragment and fold its settings into this flavor's own lists without
    /// risking a silently wrong translation. Forwarded onto the synthesized module's matching
    /// property unchanged; nothing reads or executes the fragment's contents.
    /// </summary>
    public List<string> PlatformIncludeFiles { get; } = [];

    /// <summary>
    /// O3DE's target_link_options for this flavor (e.g. "/DELAYLOAD:x.dll"). Forwarded onto the
    /// synthesized module's <see cref="ModuleRules.AdditionalLinkerFlags"/>; only that module's link
    /// sees them.
    /// </summary>
    public List<string> AdditionalLinkerFlags { get; } = [];

    /// <summary>
    /// Unity build for this flavor's module alone; null follows the target. Forwarded onto
    /// <see cref="ModuleRules.UseUnityBuild"/>, so one plugin can go unity without rebuilding the
    /// rest of a target's closure.
    /// </summary>
    public bool? UseUnityBuild { get; set; }

    /// <summary>Sources kept out of the unity files; see <see cref="ModuleRules.UnityBuildExclusions"/>.</summary>
    public List<string> UnityBuildExclusions { get; } = [];

    /// <summary>
    /// O3DE's AUTOGEN_RULES for this flavor, in ly_add_autogen's "glob,template.jinja,output"
    /// spelling. The templates must be listed in <see cref="SourceFiles"/> (a path outside the
    /// plugin is fine); applied through <see cref="ModuleRules.AddAutoGen"/> on the synthesized
    /// module once its SourceFiles list is complete.
    /// </summary>
    public List<string> AutoGenRules { get; } = [];

    /// <summary>
    /// O3DE's RUNTIME_DEPENDENCIES - targets/plugins this flavor needs present at runtime (loaded,
    /// or sitting next to the output) but never links against. Forwarded onto the synthesized
    /// module's <see cref="ModuleRules.RuntimeDependencies"/> unchanged; see that property's doc
    /// comment for what Omen actually does with it.
    /// </summary>
    public List<string> RuntimeDependencies { get; } = [];

    /// <summary>
    /// O3DE's `3rdParty::` BUILD_DEPENDENCIES entries this flavor's target was declared with
    /// (e.g. "3rdParty::Lua", "3rdParty::zstd" - anything but the "3rdParty::Qt::*" family,
    /// which goes through QtVersion/QtModules below instead). Forwarded onto the synthesized
    /// module's <see cref="ModuleRules.ThirdPartyDependencies"/> unchanged; see that property's
    /// doc comment for why this is recorded-only rather than resolved to real include/link
    /// inputs.
    /// </summary>
    public List<string> ThirdPartyDependencies { get; } = [];

    /// <summary>
    /// Qt version this flavor's target was declared with (O3DE's AUTOMOC/AUTORCC/AUTOUIC +
    /// find_package(Qt) target properties). QtVersion.None (the default) means "not a Qt
    /// target" - same as a hand-written ModuleRules that never calls UseQt. Forwarded onto the
    /// synthesized module's matching Qt properties; see ModuleRules's Qt section for which
    /// generator paths actually consume them.
    /// </summary>
    public QtVersion QtVersion { get; set; } = QtVersion.None;
    public List<string> QtModules { get; } = [];
    public bool EnableMoc { get; set; } = true;
    public bool EnableUic { get; set; } = true;
    public bool EnableRcc { get; set; } = true;
}

/// <summary>
/// Evaluates a RequiredTraits gate (declared on either <see cref="PluginFlavor"/> or
/// <see cref="ModuleRules"/> - both expose the same shape) against
/// <see cref="BuildContext.PlatformTraits"/>. Kept as a tiny stateless helper (like
/// PluginFlavorNaming) rather than a method on either owner type, since the only callers are
/// CompiledRules.CreateModuleRules deciding whether to expand a PluginFlavor into a module at all,
/// and whether to keep a hand-written ModuleRules in the compiled set at all. Named for the
/// concept it evaluates (a trait gate) rather than either caller, since both are equally real
/// consumers - see the ModuleRules per-target gating gap this generalization closed.
/// </summary>
internal static class TraitGating
{
    /// <summary>
    /// True when every trait in requiredTraits is satisfied. A trait BuildContext has no entry
    /// for is "unknown" (Omen doesn't read O3DE's per-platform PAL files) and defaults to
    /// satisfied rather than failing the gate: the pre-existing behavior for every flavor/module
    /// today is unconditional expansion, and PAL_TRAIT_BUILD_HOST_TOOLS/PAL_TRAIT_BUILD_TESTS_SUPPORTED
    /// (the overwhelming majority of real gates, see the residue report) are true on essentially
    /// every desktop dev build - defaulting unknown to "not satisfied" would silently delete the
    /// Editor/Tools/Tests flavor (or gated standalone target) from every build until something
    /// populates PlatformTraits, which is a worse and more surprising regression than the status
    /// quo this change is additive on top of. A trait BuildContext explicitly records as false (a
    /// real PAL-file reader populated it and it came back false) does fail the gate - that
    /// direction is safe because it only ever removes a module a generator explicitly told Omen
    /// the current platform/config doesn't build, never one nothing said anything about.
    /// </summary>
    public static bool IsSatisfied(IReadOnlyList<string> requiredTraits, BuildContext context)
    {
        foreach (var trait in requiredTraits)
        {
            if (context.PlatformTraits.TryGetValue(trait, out var satisfied) && !satisfied)
                return false;
        }
        return true;
    }
}

/// <summary>
/// Base class for a plugin's build description (a `&lt;PluginName&gt;.plugin.cs` file). plugin.json
/// stays authoritative for identity and dependencies (it's read by O3DE tooling outside
/// the build); this only declares which flavors the plugin builds and how they alias to the
/// symbolic names O3DE targets reference (Clients/Servers/Unified/Tools/Builders).
///
/// A PluginRules subclass may declare `public static TargetMatrix SupportedTargets => ...`,
/// but as of this writing it has NO EFFECT - GetSupportedTargets is only ever called with
/// FindTargetType()'s result (a TargetRules subclass) or a module's own GetType() (which for
/// a plugin flavor is always the shared internal PluginFlavorModuleRules type, never this class).
/// A plugin's build/generate matrix is entirely driven by its owning TargetRules's own
/// SupportedTargets. Per-plugin or per-flavor matrix control is a known gap, tracked for a
/// follow-up - see docs/superpowers/plans/2026-08-11-build-dimension-matrix.md's "Known
/// limitations" note.
/// </summary>
public abstract class PluginRules
{
    protected BuildContext Context { get; }
    public string Name { get; private set; }
    public PluginManifest? Manifest { get; private set; }
    public Dictionary<PluginFlavorKind, PluginFlavor> Flavors { get; } = new();
    public Dictionary<string, PluginFlavorKind> Aliases { get; } = new();

    /// <summary>
    /// <see cref="PluginFlavorKind.Custom"/> flavors, keyed by their literal suffix (e.g.
    /// "Debugger", "Profiler.RenderDoc") rather than by kind - unlike every other flavor, a
    /// single plugin can define more than one of these, which is exactly why they can't share
    /// <see cref="Flavors"/>'s enum-keyed dictionary (every Custom flavor would collide on the
    /// same key). Populated by the <c>DefineFlavor(string, PluginModuleRole)</c> overload;
    /// CompiledRules.CreateModuleRules expands both this and <see cref="Flavors"/> the same way.
    /// </summary>
    public Dictionary<string, PluginFlavor> CustomFlavors { get; } = new();

    /// <summary>
    /// Aliases backed by a <see cref="PluginFlavorKind.Custom"/> flavor (keyed by the custom
    /// flavor's suffix, same as <see cref="CustomFlavors"/>) - the Custom counterpart to
    /// <see cref="Aliases"/>, which can only ever name a real <see cref="PluginFlavorKind"/>.
    /// Populated by <see cref="CreateCustomAlias"/>.
    /// </summary>
    public Dictionary<string, string> CustomAliases { get; } = new();

    /// <summary>
    /// Aliases backed by 2+ targets - O3DE's `ly_create_alias(... TARGETS a b c)`. Each entry
    /// is a raw target reference in the same syntax TargetRules.ExtraModules already uses:
    /// "Plugin::&lt;PluginName&gt;" (bare - that plugin's Runtime flavor), "Plugin::&lt;PluginName&gt;.&lt;Alias&gt;"
    /// (that plugin's alias, recursively - itself possibly multi-target), or a plain module name
    /// passed through unchanged. Real multi-target aliases mix same-plugin and cross-plugin entries
    /// in the same list (e.g. ['Plugin::${plugin_name}', 'Plugin::AudioSystem']), which is why targets
    /// are free-form strings here rather than PluginFlavorKind - a PluginFlavorKind can only ever
    /// name a flavor of THIS plugin, but the real data routinely aliases into other plugins too. See
    /// PluginAliasResolver.ResolveAliasTargets for how this list expands to concrete module names.
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> MultiTargetAliases { get; } = new();

    protected PluginRules(BuildContext context)
    {
        Context = context;
        Name = GetType().Name.Replace("Plugin", "");
    }

    /// <summary>
    /// Loads plugin.json from &lt;pluginDirectory&gt;/plugin.json and adopts its plugin_name as this plugin's
    /// Name. The path is relative to this plugin's own *.plugin.cs file, which is why every generated
    /// plugin passes "." - see BuildContext.ResolveRulePath.
    /// </summary>
    protected void LoadManifest(string pluginDirectory)
    {
        var manifestPath = Path.Combine(Context.ResolveRulePath(GetType(), pluginDirectory), "plugin.json");
        Manifest = PluginManifest.Load(manifestPath);
        Name = Manifest.PluginName;
    }

    protected PluginFlavor DefineFlavor(PluginFlavorKind kind)
    {
        var flavor = new PluginFlavor { Kind = kind };
        // A "Static"/"PrivateObject"/"EditorPrivateObject" flavor that isn't a static library
        // is never what a plugin author meant - default it so it links independently instead of
        // silently folding into whatever aggregate happens to consume it. Likewise "Api"/
        // "EditorApi" are always INTERFACE (HeaderOnly) targets in O3DE - a generator emitting
        // these mechanically shouldn't also have to remember to set BinaryType every time.
        // Other flavor kinds keep BinaryType unset by default.
        flavor.BinaryType = kind switch
        {
            PluginFlavorKind.Static or PluginFlavorKind.PrivateObject or PluginFlavorKind.EditorPrivateObject
                or PluginFlavorKind.EditorStatic => TargetType.StaticLibrary,
            PluginFlavorKind.Api or PluginFlavorKind.EditorApi => TargetType.HeaderOnly,
            // RHI-backend-family "always STATIC in every real instance" kinds - same reasoning
            // as Static/PrivateObject above (see each kind's own doc comment for the CMakeLists
            // evidence). Private/Public/Headless/Builders/Edit are deliberately NOT here: their
            // real BinaryType varies per plugin/platform (PAL_TRAIT-driven), same as Runtime/Editor
            // today - a generator sets those explicitly, same as it already must for Runtime.
            PluginFlavorKind.Reflect or PluginFlavorKind.PrivateStatic or PluginFlavorKind.Interface
                or PluginFlavorKind.BuildersStatic or PluginFlavorKind.ToolsStatic or PluginFlavorKind.TestsStatic
                => TargetType.StaticLibrary,
            PluginFlavorKind.Mocks or PluginFlavorKind.Headers => TargetType.HeaderOnly,
            _ => null
        };
        flavor.IsPrivateTarget = kind is PluginFlavorKind.PrivateObject or PluginFlavorKind.EditorPrivateObject;
        Flavors[kind] = flavor;
        return flavor;
    }

    /// <summary>
    /// Alias names that are the module's WHOLE name, not a suffix under the plugin. O3DE plugins
    /// renamed at some point keep back-compat aliases under their OLD name -
    /// CommonFeaturesAtom declares `AtomLyIntegration_CommonFeatures.Public`, which shares no
    /// prefix with the plugin at all - so a suffix-only alias model cannot express them.
    /// Mirrors <see cref="PluginFlavor.CustomNameIsAbsolute"/> for flavors.
    /// </summary>
    public HashSet<string> AbsoluteAliasNames { get; } = [];

    protected void CreateAlias(string aliasName, PluginFlavorKind backedBy, bool absoluteName = false)
    {
        if (absoluteName) AbsoluteAliasNames.Add(aliasName);
        if (!Flavors.ContainsKey(backedBy))
            throw new InvalidOperationException($"Plugin '{Name}' cannot alias '{aliasName}' to undefined flavor '{backedBy}'.");
        Aliases[aliasName] = backedBy;
    }

    /// <summary>
    /// Defines a <see cref="PluginFlavorKind.Custom"/> flavor for a target-name suffix that is
    /// genuinely this plugin's own business, not a recurring O3DE concept a first-class
    /// PluginFlavorKind should be added for - see that kind's doc comment. <paramref name="suffix"/>
    /// is appended directly after the plugin name with no dot inserted automatically - pass
    /// "Debugger" for "${plugin_name}Debugger" or ".Profiler.RenderDoc" for
    /// "${plugin_name}.Profiler.RenderDoc" (see <see cref="PluginFlavor.CustomName"/>).
    /// <paramref name="role"/> classifies the flavor's build tier the same way a real kind's
    /// tier is baked into PluginFlavorModuleRules's switch, since Custom has no fixed one.
    /// </summary>
    protected PluginFlavor DefineFlavor(
        string suffix, PluginModuleRole role = PluginModuleRole.Runtime, bool absoluteName = false)
    {
        var flavor = new PluginFlavor
        {
            Kind = PluginFlavorKind.Custom,
            CustomName = suffix,
            Role = role,
            CustomNameIsAbsolute = absoluteName
        };
        CustomFlavors[suffix] = flavor;
        return flavor;
    }

    /// <summary>
    /// Alias overload for a <see cref="PluginFlavorKind.Custom"/> flavor - the Custom counterpart
    /// to <see cref="CreateAlias(string, PluginFlavorKind)"/>, which can't reference a Custom flavor
    /// (it can only look the name up in the enum-keyed <see cref="Flavors"/> dictionary).
    /// </summary>
    protected void CreateCustomAlias(string aliasName, string customFlavorSuffix, bool absoluteName = false)
    {
        if (absoluteName) AbsoluteAliasNames.Add(aliasName);
        if (!CustomFlavors.ContainsKey(customFlavorSuffix))
        {
            throw new InvalidOperationException(
                $"Plugin '{Name}' cannot alias '{aliasName}' to undefined custom flavor '{customFlavorSuffix}'.");
        }
        CustomAliases[aliasName] = customFlavorSuffix;
    }

    /// <summary>
    /// Multi-target alias overload - O3DE's `ly_create_alias(NAME ... TARGETS a b c)` with 2+
    /// targets. See <see cref="MultiTargetAliases"/> for the target-reference syntax. Targets
    /// aren't validated eagerly (unlike the single-flavor overload above) because a cross-plugin
    /// reference can't be checked until every plugin's rules are compiled - PluginAliasResolver
    /// validates at resolve time instead, same as TargetRules.ExtraModules already does.
    /// </summary>
    /// <summary>
    /// Multi-target alias whose NAME is absolute (see <see cref="AbsoluteAliasNames"/>).
    /// Explicit array rather than params so it stays distinct from the suffix-named overload.
    /// </summary>
    protected void CreateAlias(string aliasName, string[] targets, bool absoluteName)
    {
        if (absoluteName) AbsoluteAliasNames.Add(aliasName);
        CreateAlias(aliasName, targets);
    }

    protected void CreateAlias(string aliasName, params string[] targets)
    {
        if (targets.Length < 1)
        {
            throw new InvalidOperationException(
                $"Plugin '{Name}': CreateAlias(string, params string[]) for '{aliasName}' needs at least one " +
                $"target (got {targets.Length}). A single target is allowed here because it may be another " +
                $"plugin's - CreateAlias(string, PluginFlavorKind) can only name this plugin's own flavors.");
        }
        MultiTargetAliases[aliasName] = targets;
    }
}
