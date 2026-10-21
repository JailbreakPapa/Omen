// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using Omen.Core.Graph;
using Omen.Core.Rules;

namespace Omen.Executors.Server;

public enum FileChangeKind { Changed, Created, Deleted, Renamed }

public readonly record struct FileChange(string Path, FileChangeKind Kind);

internal sealed record ClosureEntry(TargetRules TargetRules, ModuleClosureResult Closure);

/// <summary>
/// What a build server keeps between builds, and the rules for throwing it away. Changes arrive on the watcher
/// thread through <see cref="Enqueue"/> but only take effect in <see cref="ApplyPendingChanges"/>, which the
/// server calls at the start of each request - a build never sees its inputs change under it.
/// </summary>
public sealed class BuildWarmState
{
    private static readonly string[] RuleFileSuffixes = [".target.cs", ".module.cs", ".plugin.cs", ".omenproject.cs"];
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cpp", ".c", ".cc", ".cxx", ".h", ".hpp", ".hxx", ".inl" };

    private readonly ConcurrentQueue<FileChange> _pending = new();
    private volatile bool _overflowed;
    private readonly Dictionary<string, CompiledRules> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClosureEntry> _closures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncludeScanner> _includeScanners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActionDigestStore> _digestStores = new(StringComparer.OrdinalIgnoreCase);
    private readonly WarmStamps _stamps;
    private int _ruleCompilations;
    private int _closureComputations;

    public BuildWarmState() => _stamps = new WarmStamps(this);

    public int RuleCompilations => _ruleCompilations;

    /// <summary>Compiled rules are held (the next build skips rule compilation).</summary>
    public bool HasRules
    {
        get { lock (_rules) return _rules.Count > 0; }
    }
    public int ClosureComputations => _closureComputations;

    /// <summary>Only paths under this root are served from cached stamps; everything else is stat'ed every time.</summary>
    public string? WatchedRoot { get; set; }

    private readonly ConcurrentDictionary<string, bool> _directoryWatched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the watcher reports changes to <paramref name="path"/>: under <see cref="WatchedRoot"/> and not behind
    /// a junction or symlink on the way down (FileSystemWatcher does not follow reparse points, so a path through one
    /// only looks watched).
    /// </summary>
    public bool IsWatched(string path)
    {
        if (WatchedRoot is not { } root || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        var directory = Path.GetDirectoryName(path);
        return directory != null && IsWatchedDirectory(directory, root.TrimEnd('\\', '/'));
    }

    private bool IsWatchedDirectory(string directory, string root)
    {
        if (directory.Length <= root.Length)
            return true;
        if (_directoryWatched.TryGetValue(directory, out var watched))
            return watched;
        var info = new DirectoryInfo(directory);
        watched = !(info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            && (Path.GetDirectoryName(directory) is not { } parent || IsWatchedDirectory(parent, root));
        _directoryWatched[directory] = watched;
        return watched;
    }

    /// <summary>File stamps that stay cached until a change to that path is applied.</summary>
    public IFileStamps Stamps => _stamps;

    public void Enqueue(FileChange change) => _pending.Enqueue(change with { Path = Path.GetFullPath(change.Path) });

    public void EnqueueOverflow() => _overflowed = true;

    /// <summary>
    /// A build just wrote <paramref name="path"/>. Unlike a watched change this takes effect immediately - a
    /// concurrent build must not trust a stamp cached before the file existed - and it is always safe: it only
    /// drops a cached stamp, forcing the next reader to stat.
    /// </summary>
    public void OutputWritten(string path) => _stamps.Evict(Path.GetFullPath(path));

    public void ApplyPendingChanges()
    {
        if (_overflowed)
        {
            _overflowed = false;
            _pending.Clear();
            DropAll();
            return;
        }
        while (_pending.TryDequeue(out var change))
        {
            _stamps.Evict(change.Path);
            if (change.Kind != FileChangeKind.Changed)
                _directoryWatched.Clear(); // a junction may have been created, removed or renamed
            lock (_includeScanners)
            {
                foreach (var scanner in _includeScanners.Values)
                    scanner.Forget(change.Path);
            }

            if (change.Kind is FileChangeKind.Deleted or FileChangeKind.Renamed && IsIntermediate(change.Path))
            {
                DropAll();
            }
            else if (IsRuleFile(change.Path))
            {
                // Stamps, include resolutions and digests describe files, which a rule edit does not touch.
                lock (_rules) _rules.Clear();
                lock (_closures) _closures.Clear();
                DropGraphs(_ => true);
            }
            else if (change.Kind != FileChangeKind.Changed && SourceExtensions.Contains(Path.GetExtension(change.Path))
                && !change.Path.Contains($"{Path.DirectorySeparatorChar}Intermediate{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                lock (_closures) _closures.Clear();
                DropGraphs(_ => true);
            }
            else
            {
                var changed = Path.GetFullPath(change.Path);
                DropGraphs(graph => graph.SourceInputs.Contains(changed));
            }
        }
    }

    public void DropAll()
    {
        lock (_rules) _rules.Clear();
        lock (_closures) _closures.Clear();
        DropGraphs(_ => true);
        lock (_includeScanners) _includeScanners.Clear();
        lock (_digestStores)
        {
            foreach (var store in _digestStores.Values)
                store.Save();
            _digestStores.Clear();
        }
        _stamps.Clear();
        _directoryWatched.Clear();
    }

    /// <summary>
    /// Takes environment variables that changed since the server started (rules read some, e.g. HOUDINI_ENGINE_HFS);
    /// closures and graphs built under the old values are dropped. PATH stays the launching shell's.
    /// </summary>
    public bool ApplyEnvironment(IReadOnlyDictionary<string, string> persisted)
    {
        var changed = false;
        foreach (var (name, value) in persisted)
        {
            if (name.Equals("Path", StringComparison.OrdinalIgnoreCase) || Environment.GetEnvironmentVariable(name) == value)
                continue;
            Environment.SetEnvironmentVariable(name, value);
            changed = true;
        }
        if (changed)
        {
            lock (_closures) _closures.Clear();
            DropGraphs(_ => true);
        }
        return changed;
    }

    /// <summary>
    /// The machine and user variables as a newly started process would see them (user wins). ponytail: a variable
    /// deleted from the registry keeps its old value until the server restarts.
    /// </summary>
    public static Dictionary<string, string> PersistedEnvironment()
    {
        var persisted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return persisted;
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables(target))
                persisted[(string)entry.Key] = (string?)entry.Value ?? "";
        }
        return persisted;
    }

    /// <summary>
    /// An action graph a build can use again, and the files that could change its shape: every action input no
    /// action produces (an edited source may gain an #include; an object file never changes the graph).
    /// </summary>
    public sealed class CachedGraph(ActionGraph graph)
    {
        public ActionGraph Graph { get; } = graph;
        internal HashSet<string> SourceInputs { get; } = graph.Actions.SelectMany(a => a.Inputs).Select(i => Path.GetFullPath(i.Path))
            .Except(graph.Actions.SelectMany(a => a.Outputs).Select(o => Path.GetFullPath(o.Path)), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        internal volatile bool Stale;
    }

    private readonly Dictionary<string, CachedGraph> _graphs = new(StringComparer.Ordinal);
    private readonly List<CachedGraph> _graphsInUse = [];
    private int _graphBuilds;

    public int GraphBuilds => _graphBuilds;

    /// <summary>The cached graph for <paramref name="key"/>, reset and handed to one build at a time; null to build one.</summary>
    public CachedGraph? TakeGraph(string key)
    {
        lock (_graphs)
        {
            if (!_graphs.Remove(key, out var cached))
                return null;
            _graphsInUse.Add(cached);
            cached.Graph.ResetForRebuild();
            return cached;
        }
    }

    public CachedGraph NewGraph(ActionGraph graph)
    {
        Interlocked.Increment(ref _graphBuilds);
        var cached = new CachedGraph(graph);
        lock (_graphs) _graphsInUse.Add(cached);
        return cached;
    }

    /// <summary>Back into the cache after the build, unless a change applied while it was out invalidated it.</summary>
    public void ReturnGraph(string key, CachedGraph cached)
    {
        lock (_graphs)
        {
            _graphsInUse.Remove(cached);
            if (!cached.Stale)
                _graphs[key] = cached;
        }
    }

    private void DropGraphs(Func<CachedGraph, bool> affected)
    {
        lock (_graphs)
        {
            foreach (var key in _graphs.Where(g => affected(g.Value)).Select(g => g.Key).ToList())
                _graphs.Remove(key);
            foreach (var cached in _graphsInUse.Where(affected))
                cached.Stale = true;
        }
    }

    internal async Task<CompiledRules> GetCompiledRulesAsync(string workingDir, Func<Task<CompiledRules>> compile)
    {
        lock (_rules)
        {
            if (_rules.TryGetValue(workingDir, out var warm))
                return warm;
        }
        var compiled = await compile();
        Interlocked.Increment(ref _ruleCompilations);
        lock (_rules) _rules[workingDir] = compiled;
        return compiled;
    }

    internal ClosureEntry? GetClosure(string key)
    {
        lock (_closures) return _closures.TryGetValue(key, out var entry) ? entry : null;
    }

    internal void SetClosure(string key, ClosureEntry entry)
    {
        Interlocked.Increment(ref _closureComputations);
        lock (_closures) _closures[key] = entry;
    }

    /// <summary>One scanner per intermediate directory, loaded from its cache file once.</summary>
    internal IncludeScanner GetIncludeScanner(string cachePath)
    {
        lock (_includeScanners)
        {
            if (!_includeScanners.TryGetValue(cachePath, out var scanner))
                _includeScanners[cachePath] = scanner = IncludeScanner.LoadFrom(cachePath);
            scanner.ForgetValidationWhere(file => !IsWatched(file));
            return scanner;
        }
    }

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _graphLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Held while a build constructs its action graph: the builder writes generated files (unity, registration,
    /// response files) and the annotation cache into the intermediate directory, so concurrent builds take turns.
    /// </summary>
    internal SemaphoreSlim GraphLock(string intermediateDirectory) => _graphLocks.GetOrAdd(intermediateDirectory, _ => new SemaphoreSlim(1, 1));

    /// <summary>One digest store per intermediate directory, shared by every concurrent build.</summary>
    internal ActionDigestStore GetDigestStore(string path)
    {
        lock (_digestStores)
        {
            if (_digestStores.TryGetValue(path, out var store))
                store.Refresh();
            else
                _digestStores[path] = store = new ActionDigestStore(path);
            return store;
        }
    }

    private static bool IsRuleFile(string path) =>
        RuleFileSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase)) ||
        Path.GetFileName(path).Equals("plugin.json", StringComparison.OrdinalIgnoreCase);

    // A deleted or moved Intermediate/ (omen clean, or by hand) takes objects, digests and caches with it.
    private static bool IsIntermediate(string path) =>
        Path.GetFileName(path).Equals("Intermediate", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}Intermediate{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(path).Length == 0;

    private sealed class WarmStamps(BuildWarmState owner) : IFileStamps
    {
        private readonly ConcurrentDictionary<string, DateTime?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public DateTime? Get(string path)
        {
            if (!owner.IsWatched(path))
                return Read(path);
            // Keyed like Evict's paths: an explicit SourceFiles entry reaches here as "...\Source/Mod\src/a.cpp",
            // and under that raw key the watcher's change to "...\Source\Mod\src\a.cpp" never evicted it - the edit
            // stayed "up to date" until the server restarted.
            return _cache.GetOrAdd(Path.GetFullPath(path), Read);
        }

        public void Evict(string path)
        {
            _cache.TryRemove(path, out _);
            // A deleted or renamed directory takes every cached path under it.
            var prefix = path + Path.DirectorySeparatorChar;
            foreach (var key in _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                _cache.TryRemove(key, out _);
        }

        public void Clear() => _cache.Clear();

        private static DateTime? Read(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
    }
}
