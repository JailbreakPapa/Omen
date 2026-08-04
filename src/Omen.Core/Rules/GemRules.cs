// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// The build-relevant flavors an O3DE gem can produce. A gem declares 1-4 of these; each
/// becomes its own ModuleRules-shaped build unit once expanded (see Task 10).
/// </summary>
public enum GemFlavorKind
{
    Static,
    Runtime,
    Editor,
    Tools
}

/// <summary>
/// One buildable variant of a gem, configured like a ModuleRules block but sharing the
/// gem-level public dependencies pulled from gem.json.
/// </summary>
public sealed class GemFlavor
{
    public required GemFlavorKind Kind { get; init; }
    public string? SourceDirectory { get; set; }
    public List<string> PrivateDependencies { get; } = [];
    public List<string> PrivateIncludePaths { get; } = [];
    public List<string> PrivateDefinitions { get; } = [];
    public TargetType? BinaryType { get; set; }
}

/// <summary>
/// Base class for a gem's build description (a `&lt;GemName&gt;.gem.cs` file). gem.json
/// stays authoritative for identity and dependencies (it's read by O3DE tooling outside
/// the build); this only declares which flavors the gem builds and how they alias to the
/// symbolic names O3DE targets reference (Clients/Servers/Unified/Tools/Builders).
/// </summary>
public abstract class GemRules
{
    protected BuildContext Context { get; }
    public string Name { get; private set; }
    public GemManifest? Manifest { get; private set; }
    public Dictionary<GemFlavorKind, GemFlavor> Flavors { get; } = new();
    public Dictionary<string, GemFlavorKind> Aliases { get; } = new();

    protected GemRules(BuildContext context)
    {
        Context = context;
        Name = GetType().Name.Replace("Gem", "");
    }

    /// <summary>
    /// Loads gem.json from &lt;ProjectRoot&gt;/&lt;gemDirectoryRelativeToProjectRoot&gt;/gem.json
    /// and adopts its gem_name as this gem's Name.
    /// </summary>
    protected void LoadManifest(string gemDirectoryRelativeToProjectRoot)
    {
        var manifestPath = Path.Combine(Context.ProjectRoot, gemDirectoryRelativeToProjectRoot, "gem.json");
        Manifest = GemManifest.Load(manifestPath);
        Name = Manifest.GemName;
    }

    protected GemFlavor DefineFlavor(GemFlavorKind kind)
    {
        var flavor = new GemFlavor { Kind = kind };
        Flavors[kind] = flavor;
        return flavor;
    }

    protected void CreateAlias(string aliasName, GemFlavorKind backedBy)
    {
        if (!Flavors.ContainsKey(backedBy))
            throw new InvalidOperationException($"Gem '{Name}' cannot alias '{aliasName}' to undefined flavor '{backedBy}'.");
        Aliases[aliasName] = backedBy;
    }
}
