// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Options;

public enum BuildOptionType
{
    Bool,
    String,
    Int,
    Path
}

/// <summary>
/// A declared, user-configurable build option, as recorded onto BuildContext.DeclaredOptions
/// when a rules file calls BuildOptions.Declare - the GUI's Options panel and OptionsOrchestrator
/// both read this list, never a separate registry, so there is one source of truth for "what
/// options does this project have."
/// </summary>
public sealed class BuildOptionDeclaration
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required BuildOptionType Type { get; init; }
    public required string DefaultValue { get; init; }
    public required string EffectiveValue { get; init; }
}

/// <summary>
/// Declares a user-configurable build option from a rules file, in the spirit of CMake's
/// option()/set(... CACHE). A static entry point (rather than a method on ModuleRules/
/// TargetRules/GemRules) since those are three separate, unrelated base-class hierarchies -
/// this works identically from any of them, or from a plain rules file with no base class
/// dependency on this concept at all.
/// </summary>
public static class BuildOptions
{
    public static bool Declare(BuildContext context, string name, string description, bool defaultValue)
    {
        var effective = ResolveAndRecord(context, name, description, BuildOptionType.Bool, defaultValue ? "true" : "false");
        return effective.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static string Declare(BuildContext context, string name, string description, string defaultValue) =>
        ResolveAndRecord(context, name, description, BuildOptionType.String, defaultValue);

    public static int Declare(BuildContext context, string name, string description, int defaultValue)
    {
        var effective = ResolveAndRecord(context, name, description, BuildOptionType.Int, defaultValue.ToString());
        return int.TryParse(effective, out var parsed) ? parsed : defaultValue;
    }

    public static string DeclarePath(BuildContext context, string name, string description, string defaultValue) =>
        ResolveAndRecord(context, name, description, BuildOptionType.Path, defaultValue);

    private static string ResolveAndRecord(BuildContext context, string name, string description, BuildOptionType type, string defaultValue)
    {
        var existing = context.DeclaredOptions.FirstOrDefault(o => o.Name == name);
        if (existing != null)
            return existing.EffectiveValue;

        var effectiveValue = context.CachedOptionValues.TryGetValue(name, out var cached) ? cached : defaultValue;

        context.DeclaredOptions.Add(new BuildOptionDeclaration
        {
            Name = name,
            Description = description,
            Type = type,
            DefaultValue = defaultValue,
            EffectiveValue = effectiveValue
        });

        return effectiveValue;
    }
}
