// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Which family of compiler driver a toolchain speaks. Distinct from the target platform: a
/// Windows build can be MSVC or clang-cl, and a Linux build can be gcc or clang, so the flags a
/// project wants applied cannot be selected by platform alone.
/// </summary>
public enum CompilerFamily
{
    Msvc,
    Clang,
    Gcc
}

/// <summary>
/// One bundle of engine-wide flags: what to define, what to pass the compiler, what to pass the
/// linker. Held once for "every configuration" and once per configuration that adds to it.
/// </summary>
public sealed class ToolchainOptionSet
{
    public List<string> Definitions { get; } = [];
    public List<string> CompilerOptions { get; } = [];
    public List<string> LinkerOptions { get; } = [];

    public bool IsEmpty => Definitions.Count == 0 && CompilerOptions.Count == 0 && LinkerOptions.Count == 0;
}

/// <summary>
/// A project's engine-wide toolchain configuration for one compiler family - the layer O3DE keeps
/// in <c>cmake/Platform/Common/MSVC/Configurations_msvc.cmake</c> and its clang sibling, applied
/// through <c>ly_append_configurations_options</c> to every target in the build rather than to
/// any one of them.
///
/// Nothing in Omen modelled this, and it is not something per-target conversion could ever have
/// produced, because it does not live in any target's CMakeLists.txt. Its absence is why the
/// first real compile of converted O3DE rules died in AzCore/variadic.h with
/// "C1112: compiler limit: '128' too many macro arguments" - that header needs
/// <c>/Zc:preprocessor</c>, which Configurations_msvc.cmake sets for the whole engine.
/// </summary>
public sealed class GlobalToolchainOptions
{
    /// <summary>Applied to every configuration - O3DE's DEFINES / COMPILATION / LINK sections.</summary>
    public ToolchainOptionSet Common { get; } = new();

    /// <summary>Added on top of <see cref="Common"/> for one configuration - O3DE's
    /// COMPILATION_DEBUG / LINK_RELEASE / ... sections.</summary>
    public Dictionary<BuildConfiguration, ToolchainOptionSet> ByConfiguration { get; } = new();

    /// <summary>The (created-on-demand) set for one configuration, so a rule file can write
    /// <c>For(BuildConfiguration.Debug).CompilerOptions.Add("/Od")</c> without null checks.</summary>
    public ToolchainOptionSet For(BuildConfiguration configuration)
    {
        if (!ByConfiguration.TryGetValue(configuration, out var set))
        {
            set = new ToolchainOptionSet();
            ByConfiguration[configuration] = set;
        }

        return set;
    }

    /// <summary>Common plus this configuration's own additions, in that order - a
    /// configuration-specific optimization flag must come after the common one it overrides,
    /// because both compiler drivers take the last flag to win.</summary>
    public ToolchainOptionSet Resolve(BuildConfiguration configuration)
    {
        var resolved = new ToolchainOptionSet();
        Append(resolved, Common);

        if (ByConfiguration.TryGetValue(configuration, out var specific))
            Append(resolved, specific);

        return resolved;
    }

    /// <summary>
    /// The one token a global flag may carry: <c>$(ProjectRoot)</c>, replaced with the absolute
    /// project root. Some flags genuinely need an absolute path and cannot be made relative -
    /// MSVC resolves <c>/FI</c> like a quoted #include, against the *source file's* directory
    /// rather than the compiler's working directory, so a relative force-include resolves
    /// somewhere different for every .cpp. Baking the absolute path into a checked-in rule file
    /// instead would break for every other machine, which is the whole reason for the token.
    /// </summary>
    public const string ProjectRootToken = "$(ProjectRoot)";

    public static IReadOnlyList<string> ExpandTokens(IEnumerable<string> values, string projectRoot) =>
        values.Select(value => value.Replace(ProjectRootToken, projectRoot, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Put the project's engine-wide options for <paramref name="family"/> onto
    /// <paramref name="context"/>, so every compile and link action derived from it carries
    /// them. Every path that produces command lines has to call this - a build and a
    /// compile_commands.json/.vcxproj generation that disagree about the flags are the same
    /// drift <see cref="Build.CompileEnvironmentResolver"/> exists to prevent, one layer up.
    /// </summary>
    public static void ApplyTo(BuildContext context, OmenProjectRules? project, CompilerFamily family)
    {
        if (project == null || !project.ToolchainOptions.TryGetValue(family, out var familyOptions))
            return;

        var resolved = familyOptions.Resolve(context.Configuration);
        context.GlobalDefinitions.AddRange(ExpandTokens(resolved.Definitions, context.ProjectRoot));
        context.GlobalCompilerOptions = ExpandTokens(resolved.CompilerOptions, context.ProjectRoot);
        context.GlobalLinkerOptions = ExpandTokens(resolved.LinkerOptions, context.ProjectRoot);
    }

    private static void Append(ToolchainOptionSet into, ToolchainOptionSet from)
    {
        into.Definitions.AddRange(from.Definitions);
        into.CompilerOptions.AddRange(from.CompilerOptions);
        into.LinkerOptions.AddRange(from.LinkerOptions);
    }
}
