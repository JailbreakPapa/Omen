// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;

namespace Omen.Core.Options;

/// <summary>
/// Persists edited build-option values across Configure runs - the Omen equivalent of
/// CMakeCache.txt, minus CMake's type-suffix-in-the-key convention (BuildOptionDeclaration
/// already carries the type, so the cache file itself only needs name -> string value).
/// </summary>
public sealed class OptionCacheStore(string path)
{
    public IReadOnlyDictionary<string, string> Load()
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public void Save(IReadOnlyDictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(values));
    }
}
