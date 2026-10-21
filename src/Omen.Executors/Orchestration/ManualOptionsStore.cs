// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;
using Omen.Core.Options;

namespace Omen.Executors.Orchestration;

/// <summary>
/// Persists build options added manually through the GUI's Add Entry button (no backing
/// BuildOptions.Declare call in any rules file). OptionsOrchestrator.DiscoverAsync merges
/// these into its result after real discovery, so an added entry survives a Configure
/// re-run - unlike a plain in-memory addition, which real discovery would silently drop
/// since it only returns what rules files actually declared.
/// </summary>
public sealed class ManualOptionsStore
{
    private readonly string _path;
    private readonly List<BuildOptionDeclaration> _entries;

    public ManualOptionsStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public IReadOnlyList<BuildOptionDeclaration> All => _entries;

    public void Add(BuildOptionDeclaration declaration)
    {
        _entries.RemoveAll(d => d.Name == declaration.Name);
        _entries.Add(declaration);
    }

    public void Remove(string name) => _entries.RemoveAll(d => d.Name == name);

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A save failure shouldn't crash a Configure that otherwise succeeded; silently
            // not persisting is an acceptable degradation here, matching OptionCacheStore.
        }
    }

    private static List<BuildOptionDeclaration> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<BuildOptionDeclaration>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
