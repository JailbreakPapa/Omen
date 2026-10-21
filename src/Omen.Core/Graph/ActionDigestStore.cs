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
/// <remarks>
/// Thread-safe: the orchestrator records digests from executor worker threads as actions
/// complete. Saves are atomic (temp file, then rename), so a build killed mid-save keeps the
/// previous store instead of a truncated file that would load back as empty.
/// </remarks>
public sealed class ActionDigestStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _digests;
    private readonly object _saveLock = new();
    private readonly HashSet<string> _unsaved = [];

    public ActionDigestStore(string path)
    {
        _path = path;
        _digests = Load(path);
    }

    public bool TryGet(string outputPath, out ContentDigest digest) => TryGet(outputPath, out digest, out _);

    /// <summary>
    /// <paramref name="parts"/> is BuildAction.ComputeDigestParts from the recording build; null for an entry
    /// written before parts existed.
    /// </summary>
    public bool TryGet(string outputPath, out ContentDigest digest, out string? parts)
    {
        string? serialized;
        lock (_digests)
            _digests.TryGetValue(outputPath, out serialized);
        if (serialized is not null)
        {
            var space = serialized.IndexOf(' ');
            digest = ContentDigest.Parse(space < 0 ? serialized : serialized[..space]);
            parts = space < 0 ? null : serialized[(space + 1)..];
            return true;
        }
        digest = default;
        parts = null;
        return false;
    }

    public void Set(string outputPath, ContentDigest digest, string? parts = null)
    {
        lock (_digests)
        {
            _digests[outputPath] = parts is null ? digest.ToString() : $"{digest} {parts}";
            _unsaved.Add(outputPath);
        }
    }

    /// <summary>
    /// Takes in what other processes saved since this store loaded. A build server keeps one store for its
    /// lifetime, while in-process builds (--no-server, or the server unavailable) save to the same file.
    /// </summary>
    public void Refresh()
    {
        lock (_saveLock)
        {
            using var fileLock = LockFile();
            MergeFromDisk();
        }
    }

    public void Save()
    {
        lock (_saveLock)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using var fileLock = LockFile();
            MergeFromDisk();
            string json;
            lock (_digests)
            {
                json = JsonSerializer.Serialize(_digests);
                _unsaved.Clear();
            }
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
    }

    // Entries set here since the last save win; everything else takes the file's value.
    private void MergeFromDisk()
    {
        var disk = Load(_path);
        lock (_digests)
        {
            foreach (var (output, serialized) in disk)
            {
                if (!_unsaved.Contains(output))
                    _digests[output] = serialized;
            }
        }
    }

    private FileStream? LockFile()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            return null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
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
