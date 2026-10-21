// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Omen.Core.Configuration;

public sealed class OmenPreset
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Hidden { get; init; }
    public List<string> Inherits { get; init; } = [];
    public List<string> HostPlatforms { get; init; } = [];
    public Dictionary<string, List<string>> Commands { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string?> Environment { get; init; } = new(StringComparer.Ordinal);
}

public sealed class OmenWorkflowPreset
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<List<string>> Steps { get; init; } = [];
}

public sealed class OmenPresetsFile
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }
    public int Version { get; init; }
    public List<string> Include { get; init; } = [];
    public List<OmenPreset> Presets { get; init; } = [];
    public List<OmenWorkflowPreset> Workflows { get; init; } = [];
}

public sealed record ResolvedPreset(string Name, string? Description, bool Hidden, IReadOnlyList<string> HostPlatforms,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Commands, IReadOnlyDictionary<string, string?> Environment);

/// <summary>Versioned shared/user presets with includes, inheritance, macro expansion and environment isolation.</summary>
public sealed class ProjectPresets
{
    private static readonly StringComparer EnvironmentNames = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string root;
    private readonly Dictionary<string, OmenPreset> presets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OmenWorkflowPreset> workflows = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static string HostPlatform => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Darwin" : "Linux";

    public ProjectPresets(string projectRoot)
    {
        root = Path.GetFullPath(projectRoot);
        var loaded = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var visiting = new HashSet<string>(loaded.Comparer);
        foreach (string name in new[] { "OmenPresets.json", "OmenUserPresets.json" })
        {
            string path = Path.Combine(root, name);
            if (File.Exists(path)) Load(path);
        }

        void Load(string path)
        {
            path = Path.GetFullPath(path);
            if (loaded.Contains(path)) return;
            if (!visiting.Add(path)) throw new InvalidOperationException($"Preset include cycle at '{path}'.");
            if (visiting.Count > 32) throw new InvalidOperationException("Preset includes exceed 32 levels.");
            var file = JsonSerializer.Deserialize<OmenPresetsFile>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException($"Empty preset file '{path}'.");
            if (file.Version != 1) throw new InvalidOperationException($"Unsupported Omen preset version {file.Version} in '{path}'. Expected 1.");
            string Origin(string value) => value.Replace("${fileDir}", Path.GetDirectoryName(path));
            foreach (var include in file.Include)
                Load(Path.GetFullPath(Origin(include).Replace("${sourceDir}", root), Path.GetDirectoryName(path)!));
            foreach (var preset in file.Presets)
            {
                if (string.IsNullOrWhiteSpace(preset.Name)) throw new InvalidOperationException("Preset names cannot be empty.");
                if (preset.Commands.Values.SelectMany(v => v).Any(v => v == "--preset" || v.StartsWith("--preset=")))
                    throw new InvalidOperationException($"Preset '{preset.Name}' cannot recursively select a preset in its arguments.");
                var original = new OmenPreset
                {
                    Name = preset.Name, Description = preset.Description, Hidden = preset.Hidden, Inherits = preset.Inherits,
                    HostPlatforms = preset.HostPlatforms,
                    Commands = preset.Commands.ToDictionary(c => c.Key, c => c.Value.Select(Origin).ToList(), StringComparer.Ordinal),
                    Environment = preset.Environment.ToDictionary(e => e.Key, e => e.Value == null ? null : Origin(e.Value), StringComparer.Ordinal)
                };
                if (!presets.TryAdd(preset.Name, original)) throw new InvalidOperationException($"Duplicate preset '{preset.Name}'.");
            }
            foreach (var workflow in file.Workflows)
            {
                if (string.IsNullOrWhiteSpace(workflow.Name) || workflow.Steps.Count == 0)
                    throw new InvalidOperationException("Workflows require a name and at least one step.");
                if (!workflows.TryAdd(workflow.Name, workflow)) throw new InvalidOperationException($"Duplicate workflow '{workflow.Name}'.");
            }
            visiting.Remove(path);
            loaded.Add(path);
        }
    }

    public IReadOnlyList<ResolvedPreset> List() => presets.Keys.Select(Resolve).Where(p => !p.Hidden && Available(p))
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

    public IReadOnlyList<OmenWorkflowPreset> Workflows => workflows.Values.OrderBy(w => w.Name, StringComparer.Ordinal).ToList();

    public OmenWorkflowPreset Workflow(string name) => workflows.TryGetValue(name, out var workflow) ? workflow :
        throw new InvalidOperationException($"Unknown workflow '{name}'.");

    public ResolvedPreset Resolve(string name)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        ResolvedPreset Inherit(string key)
        {
            if (!presets.TryGetValue(key, out var definition)) throw new InvalidOperationException($"Unknown preset '{key}'.");
            if (!visiting.Add(key)) throw new InvalidOperationException($"Preset inheritance cycle at '{key}'.");
            var commands = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var environment = new Dictionary<string, string?>(EnvironmentNames);
            IReadOnlyList<string> platforms = [];
            // Earlier parents win when several parents declare the same field.
            foreach (var parent in definition.Inherits.AsEnumerable().Reverse())
            {
                var inherited = Inherit(parent);
                foreach (var c in inherited.Commands) commands[c.Key] = c.Value;
                foreach (var e in inherited.Environment) environment[e.Key] = e.Value;
                if (inherited.HostPlatforms.Count > 0) platforms = inherited.HostPlatforms;
            }
            foreach (var c in definition.Commands) commands[c.Key] = c.Value;
            foreach (var e in definition.Environment) environment[e.Key] = e.Value;
            if (definition.HostPlatforms.Count > 0) platforms = definition.HostPlatforms;
            visiting.Remove(key);
            return new(key, definition.Description, definition.Hidden, platforms, commands, environment);
        }
        var merged = Inherit(name);
        var env = new Dictionary<string, string?>(EnvironmentNames);
        var resolving = new HashSet<string>(EnvironmentNames);
        string? Variable(string key)
        {
            if (env.TryGetValue(key, out var result)) return result;
            if (!merged.Environment.TryGetValue(key, out var value)) return Environment.GetEnvironmentVariable(key);
            if (!resolving.Add(key)) throw new InvalidOperationException($"Preset environment cycle at '{key}'.");
            result = value == null ? null : Expand(value);
            resolving.Remove(key);
            env[key] = result;
            return result;
        }
        string Expand(string value)
        {
            value = Regex.Replace(value, @"\$\{([^}]+)\}", match => match.Groups[1].Value switch
            {
                "sourceDir" => root, "presetName" => name, "hostSystemName" => HostPlatform, "dollar" => "$",
                _ => throw new InvalidOperationException($"Unknown preset macro '{match.Value}'.")
            });
            return Regex.Replace(value, @"\$(p?env)\{([^}]+)\}", match =>
                (match.Groups[1].Value == "penv" ? Environment.GetEnvironmentVariable(match.Groups[2].Value) : Variable(match.Groups[2].Value)) ?? "");
        }
        foreach (var key in merged.Environment.Keys) _ = Variable(key);
        return merged with { Environment = env, Commands = merged.Commands.ToDictionary(c => c.Key,
            c => (IReadOnlyList<string>)c.Value.Select(Expand).ToList(), StringComparer.Ordinal) };
    }

    public static bool Available(ResolvedPreset preset) => preset.HostPlatforms.Count == 0 ||
        preset.HostPlatforms.Contains(HostPlatform, StringComparer.OrdinalIgnoreCase);

    /// <summary>Explicit scalar CLI options and target arguments override preset defaults.</summary>
    public static IReadOnlyList<string> MergeArguments(IReadOnlyList<string> defaults, IReadOnlyList<string> explicitArguments)
    {
        static string Key(string token) => token.Split('=', 2)[0] switch
        {
            "-p" => "--platform", "-a" => "--arch", "-c" => "--configuration", "-j" => "--jobs",
            "-i" => "--ide", "-R" => "--filter", "-E" => "--exclude", "-L" => "--label", "-N" => "--list",
            "-D" => "--set", "-U" => "--unset", "-v" => "--verbose", "-q" => "--quiet", "-d" => "--distribute", _ => token.Split('=', 2)[0]
        };
        var flags = new HashSet<string>(["--clean", "--distribute", "--dry-run", "--no-server", "--background", "--list",
            "--allow-no-tests", "--build", "--advanced", "--verbose", "--quiet", "--no-color"], StringComparer.Ordinal);
        var repeatable = new HashSet<string>(["--dim", "--set", "--unset"], StringComparer.Ordinal);
        List<(string? Key, List<string> Tokens)> Split(IReadOnlyList<string> args)
        {
            var result = new List<(string? Key, List<string> Tokens)>();
            for (int i = 0; i < args.Count; i++)
            {
                string token = args[i];
                string? key = token.StartsWith('-') ? Key(token) : null;
                var tokens = new List<string> { token };
                if (key != null && !flags.Contains(key) && !token.Contains('=') && i + 1 < args.Count && !args[i + 1].StartsWith('-')) tokens.Add(args[++i]);
                result.Add((key, tokens));
            }
            return result;
        }
        var explicitParts = Split(explicitArguments);
        bool positional = explicitParts.Any(p => p.Key == null);
        var keys = explicitParts.Where(p => p.Key != null).Select(p => p.Key!).ToHashSet(StringComparer.Ordinal);
        string Assignment((string? Key, List<string> Tokens) part) => part.Tokens.Count > 1 ? part.Tokens[1].Split('=', 2)[0] :
            part.Tokens[0].Contains('=') ? part.Tokens[0].Split('=', 2)[1].Split('=', 2)[0] : "";
        return Split(defaults).Where(p => p.Key == null ? !positional : !keys.Contains(p.Key) ||
                (repeatable.Contains(p.Key) && !explicitParts.Any(e => e.Key == p.Key && Assignment(e) == Assignment(p))))
            .SelectMany(p => p.Tokens).Concat(explicitArguments).ToList();
    }
}
