// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Omen.Core.Graph;

/// <summary>
/// Resolves the transitive set of project headers a translation unit depends on, by
/// statically scanning #include directives. ActionGraphBuilder records the result as
/// additional inputs on each compile action, so editing a header actually invalidates
/// the object files compiled from sources that include it - without this, only the
/// .cpp itself (and the PCH) were inputs and a header edit rebuilt nothing.
///
/// Only includes that resolve to a real file (relative to the including file, or under
/// the given include paths) are returned - system headers that don't resolve are
/// deliberately skipped, both because they're huge and because toolchain headers don't
/// change between builds of the same machine.
/// </summary>
public sealed class IncludeScanner
{
    private static readonly Regex IncludeRegex = new(
        @"^\s*#\s*include\s*([<""])([^>""]+)[>""]", RegexOptions.Compiled);

    // ponytail: resolved-includes cached per file, ignoring that angle-bracket resolution
    // depends on the consulting module's include paths - a header shared by two modules with
    // different include paths keeps the first resolution. Key the cache by (file, include
    // paths) if that ever produces a stale dependency in practice.
    //
    // Persisted across runs (LoadFrom/SaveTo) keyed by the file's write time and size: an
    // engine build scans several thousand headers, and each unresolved angle-bracket include
    // probes every include path on disk - re-doing that for files that haven't changed was
    // most of a no-op build's wall time.
    private readonly ConcurrentDictionary<string, CachedIncludes> _resolvedIncludes =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedIncludes(long LastWriteTicks, long Length, IReadOnlyList<string> Includes);

    // Files whose cached entry has been checked against disk in this process. A header
    // reached from hundreds of translation units was being stat'ed once per unit; sources
    // do not change while the graph is being built, so once per run is enough.
    private readonly ConcurrentDictionary<string, byte> _validatedThisRun = new(StringComparer.OrdinalIgnoreCase);

    private sealed class PersistedEntry
    {
        public string File { get; set; } = "";
        public long Ticks { get; set; }
        public long Length { get; set; }
        public List<string> Includes { get; set; } = [];
    }

    /// <summary>A scanner primed with the resolutions a previous run saved to <paramref name="path"/>;
    /// an absent or unreadable file just means an empty cache.</summary>
    public static IncludeScanner LoadFrom(string path)
    {
        var scanner = new IncludeScanner();
        try
        {
            if (File.Exists(path))
            {
                var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(File.ReadAllText(path)) ?? [];
                foreach (var entry in entries)
                    scanner._resolvedIncludes[entry.File] = new CachedIncludes(entry.Ticks, entry.Length, entry.Includes);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt cache costs one full scan, nothing more.
        }
        return scanner;
    }

    /// <summary>
    /// For a scanner that outlives one build: <paramref name="path"/> changed, so check it against disk again.
    /// </summary>
    public void Forget(string path) => _validatedThisRun.TryRemove(path, out _);

    /// <summary>
    /// For a scanner that outlives one build: re-check every file <paramref name="unwatched"/> selects (nothing
    /// reports changes to them) the next time a build reaches it.
    /// </summary>
    public void ForgetValidationWhere(Func<string, bool> unwatched)
    {
        foreach (var file in _validatedThisRun.Keys)
        {
            if (unwatched(file))
                _validatedThisRun.TryRemove(file, out _);
        }
    }

    public void SaveTo(string path)
    {
        var entries = _resolvedIncludes
            .Select(kv => new PersistedEntry { File = kv.Key, Ticks = kv.Value.LastWriteTicks, Length = kv.Value.Length, Includes = kv.Value.Includes.ToList() })
            .OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }

    /// <summary>
    /// All headers (transitively) included by <paramref name="sourceFile"/> that resolve to
    /// real files. The source file itself is not included in the result.
    /// </summary>
    public IReadOnlyCollection<string> GetTransitiveIncludes(
        string sourceFile, IReadOnlyList<string> includePaths)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(sourceFile));

        while (stack.Count > 0)
        {
            var file = stack.Pop();
            foreach (var include in ResolveDirectIncludes(file, includePaths))
            {
                if (visited.Add(include))
                    stack.Push(include);
            }
        }

        // Sorted, not set order: these become action Inputs, and the action digest hashes
        // inputs in list order - a set's enumeration order varying between runs made digests
        // unstable and randomly invalidated up-to-date compiles.
        return visited.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IReadOnlyList<string> ResolveDirectIncludes(string file, IReadOnlyList<string> includePaths)
    {
        if (_validatedThisRun.ContainsKey(file) && _resolvedIncludes.TryGetValue(file, out var validated))
            return validated.Includes;

        var info = new FileInfo(file);
        if (!info.Exists)
            return [];

        var ticks = info.LastWriteTimeUtc.Ticks;
        if (!_resolvedIncludes.TryGetValue(file, out var cached) || cached.LastWriteTicks != ticks || cached.Length != info.Length)
        {
            cached = new CachedIncludes(ticks, info.Length, ScanDirectIncludes(file, includePaths));
            _resolvedIncludes[file] = cached;
        }
        _validatedThisRun[file] = 0;
        return cached.Includes;
    }

    private static IReadOnlyList<string> ScanDirectIncludes(string f, IReadOnlyList<string> includePaths)
    {
        {
            var resolved = new List<string>();
            var directory = Path.GetDirectoryName(f);

            foreach (var line in File.ReadLines(f))
            {
                var trimmed = line.TrimStart();
                // Cheap pre-filter: the regex is only worth running on candidate lines.
                if (trimmed.Length == 0 || trimmed[0] != '#')
                    continue;

                var match = IncludeRegex.Match(trimmed);
                if (!match.Success)
                    continue;

                var quoted = match.Groups[1].Value == "\"";
                var includePath = match.Groups[2].Value;

                // Quoted includes resolve relative to the including file first (matches
                // MSVC/Clang/GCC behavior); both forms then fall back to the include paths.
                if (quoted && directory != null)
                {
                    var relative = Path.Combine(directory, includePath);
                    if (File.Exists(relative))
                    {
                        resolved.Add(Path.GetFullPath(relative));
                        continue;
                    }
                }

                foreach (var searchPath in includePaths)
                {
                    var candidate = Path.Combine(searchPath, includePath);
                    if (File.Exists(candidate))
                    {
                        resolved.Add(Path.GetFullPath(candidate));
                        break;
                    }
                }
            }

            return resolved;
        }
    }
}
