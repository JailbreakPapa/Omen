// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;
using Omen.Core.Interfaces;

namespace Omen.Core.Graph;

/// <summary>
/// Persists the digest recorded for each action's primary output across builds, so an
/// action can be skipped when its command line (and therefore its digest) hasn't changed,
/// rather than relying on file timestamps alone.
/// </summary>
public sealed class ActionDigestStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _digests;

    public ActionDigestStore(string path)
    {
        _path = path;
        _digests = Load(path);
    }

    public bool TryGet(string outputPath, out ContentDigest digest)
    {
        if (_digests.TryGetValue(outputPath, out var serialized))
        {
            digest = ContentDigest.Parse(serialized);
            return true;
        }
        digest = default;
        return false;
    }

    public void Set(string outputPath, ContentDigest digest) => _digests[outputPath] = digest.ToString();

    public void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(_digests));
    }

    private static Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
