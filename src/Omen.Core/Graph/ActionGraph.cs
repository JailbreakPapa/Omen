// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using Omen.Core.Interfaces;

namespace Omen.Core.Graph;

/// <summary>
/// Represents the directed acyclic graph (DAG) of build actions.
/// Provides dependency resolution, topological ordering, and scheduling.
/// </summary>
public sealed class ActionGraph
{
    private readonly ConcurrentDictionary<string, BuildAction> _actions = new();
    private readonly object _lock = new();
    private List<BuildAction>? _cachedTopologicalOrder;
    
    /// <summary>
    /// All actions in the graph.
    /// </summary>
    public IReadOnlyCollection<BuildAction> Actions => _actions.Values.ToList();
    
    /// <summary>
    /// Total number of actions.
    /// </summary>
    public int Count => _actions.Count;
    
    /// <summary>
    /// Adds an action to the graph.
    /// </summary>
    public void AddAction(BuildAction action)
    {
        if (!_actions.TryAdd(action.Id, action))
            throw new InvalidOperationException($"Action with ID '{action.Id}' already exists.");
        
        lock (_lock)
        {
            _cachedTopologicalOrder = null;
        }
    }
    
    /// <summary>
    /// Gets an action by ID.
    /// </summary>
    public BuildAction? GetAction(string id) => _actions.GetValueOrDefault(id);
    
    /// <summary>
    /// Adds a dependency between two actions.
    /// </summary>
    public void AddDependency(string dependentId, string dependencyId)
    {
        if (!_actions.TryGetValue(dependentId, out var dependent))
            throw new InvalidOperationException($"Action '{dependentId}' not found.");
        if (!_actions.TryGetValue(dependencyId, out var dependency))
            throw new InvalidOperationException($"Action '{dependencyId}' not found.");
        
        if (!dependent.Dependencies.Contains(dependency))
        {
            dependent.Dependencies.Add(dependency);
            dependency.Dependents.Add(dependent);
            
            lock (_lock)
            {
                _cachedTopologicalOrder = null;
            }
        }
    }
    
    /// <summary>
    /// Gets actions in topological order (dependencies first).
    /// </summary>
    public IReadOnlyList<BuildAction> GetTopologicalOrder()
    {
        lock (_lock)
        {
            if (_cachedTopologicalOrder != null)
                return _cachedTopologicalOrder;
            
            var result = new List<BuildAction>();
            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();
            
            foreach (var action in _actions.Values)
            {
                if (!visited.Contains(action.Id))
                {
                    TopologicalSort(action, visited, visiting, result);
                }
            }
            
            _cachedTopologicalOrder = result;
            return result;
        }
    }
    
    private void TopologicalSort(BuildAction action, HashSet<string> visited, HashSet<string> visiting, List<BuildAction> result)
    {
        if (visited.Contains(action.Id))
            return;
        
        if (visiting.Contains(action.Id))
            throw new InvalidOperationException($"Circular dependency detected involving action '{action.Id}'.");
        
        visiting.Add(action.Id);
        
        foreach (var dep in action.Dependencies)
        {
            TopologicalSort(dep, visited, visiting, result);
        }
        
        visiting.Remove(action.Id);
        visited.Add(action.Id);
        result.Add(action);
    }
    
    /// <summary>
    /// Gets actions that are ready to execute (all dependencies completed).
    /// </summary>
    public IReadOnlyList<BuildAction> GetReadyActions()
    {
        return _actions.Values
            .Where(a => a.Status == ActionStatus.Pending && 
                        a.Dependencies.All(d => d.Status is ActionStatus.Completed or ActionStatus.Cached or ActionStatus.Skipped))
            .ToList();
    }
    
    /// <summary>
    /// Checks if all actions are complete.
    /// </summary>
    public bool IsComplete => _actions.Values.All(a => 
        a.Status is ActionStatus.Completed or ActionStatus.Failed or ActionStatus.Skipped or ActionStatus.Cached);
    
    /// <summary>
    /// Checks if any action has failed.
    /// </summary>
    public bool HasFailures => _actions.Values.Any(a => a.Status == ActionStatus.Failed);
    
    /// <summary>
    /// Gets the critical path (longest chain of dependencies).
    /// </summary>
    public IReadOnlyList<BuildAction> GetCriticalPath()
    {
        if (_actions.Count == 0)
            return Array.Empty<BuildAction>();

        var longestPath = new Dictionary<string, (double Length, BuildAction? Next)>();
        
        // Initialize with estimated durations
        foreach (var action in _actions.Values)
        {
            longestPath[action.Id] = (action.EstimatedDuration.TotalSeconds, null);
        }
        
        // Process in reverse topological order
        var order = GetTopologicalOrder().Reverse().ToList();
        foreach (var action in order)
        {
            foreach (var dep in action.Dependencies)
            {
                var newLength = longestPath[action.Id].Length + dep.EstimatedDuration.TotalSeconds;
                if (newLength > longestPath[dep.Id].Length)
                {
                    longestPath[dep.Id] = (newLength, action);
                }
            }
        }
        
        // Find the starting point (action with longest path)
        var start = longestPath.OrderByDescending(x => x.Value.Length).First();
        
        // Build the path
        var path = new List<BuildAction>();
        var current = _actions.GetValueOrDefault(start.Key);
        while (current != null)
        {
            path.Add(current);
            current = longestPath[current.Id].Next;
        }
        
        return path;
    }
    
    /// <summary>
    /// Assigns priority to actions based on critical path analysis.
    /// Lower priority number = should execute sooner.
    /// </summary>
    public void ComputePriorities()
    {
        var criticalPath = new HashSet<string>(GetCriticalPath().Select(a => a.Id));
        var depths = new Dictionary<string, int>();
        
        // Compute depths (distance from roots)
        foreach (var action in GetTopologicalOrder())
        {
            var maxDepth = action.Dependencies.Count > 0 
                ? action.Dependencies.Max(d => depths.GetValueOrDefault(d.Id, 0)) + 1 
                : 0;
            depths[action.Id] = maxDepth;
        }
        
        // Assign priorities: critical path gets lowest (highest priority), then by depth
        foreach (var action in _actions.Values)
        {
            action.Priority = criticalPath.Contains(action.Id) 
                ? -1000 + depths[action.Id] 
                : depths[action.Id];
        }
    }
    
    /// <summary>
    /// Checks if an action is up-to-date based on input/output timestamps.
    /// </summary>
    public bool IsUpToDate(BuildAction action) => Record(action, WhyNotUpToDate(action));

    /// <summary>Why <paramref name="action"/> is out of date by timestamps; null when it is up to date.</summary>
    public string? WhyNotUpToDate(BuildAction action)
    {
        // If any output doesn't exist, not up-to-date
        var oldestOutput = DateTime.MaxValue;
        foreach (var output in action.Outputs)
        {
            var stamp = Stamp(output.Path);
            if (stamp == null)
                return $"missing-output {output.Path}";
            if (stamp.Value < oldestOutput)
                oldestOutput = stamp.Value;
        }

        // Get the newest input timestamp
        var newestInput = DateTime.MinValue;
        string? newest = null;
        foreach (var input in action.Inputs)
        {
            if (Stamp(input.Path) is { } stamp && stamp > newestInput)
            {
                newestInput = stamp;
                newest = $"input-newer {input.Path}";
            }
        }

        // Also check dependency outputs
        foreach (var dep in action.Dependencies)
        {
            foreach (var output in dep.Outputs)
            {
                if (Stamp(output.Path) is { } depTime && depTime > newestInput)
                {
                    newestInput = depTime;
                    newest = $"dependency-output-newer {output.Path}";
                }
            }
        }
        // >= not >: a Copy action preserves the source's timestamp, so output == input is
        // the steady state of a correct copy - strict > made every dll-staging copy rerun
        // on every build. (Make has the same equal-means-up-to-date rule.)
        return oldestOutput >= newestInput ? null : newest;
    }

    private static bool Record(BuildAction action, string? reason)
    {
        action.RebuildReason = reason;
        action.RootCause = reason;
        return reason == null;
    }
    
    /// <summary>
    /// A file's last write time, or null when it does not exist. During the whole-graph pass
    /// (MarkUpToDateActionsAsSkipped) every path is stat'ed once and remembered: a header is
    /// an input to hundreds of actions, and stat'ing it twice per action (Exists, then the
    /// write time) was 13 seconds of a 2,200-action no-op build. Outside the pass - the
    /// executor re-checking an action once its dependencies have run - nothing is cached,
    /// because those dependencies just wrote the outputs being checked.
    /// </summary>
    private Dictionary<string, DateTime?>? _stampCache;

    /// <summary>
    /// Stamps kept current by the caller between builds (a build server's file watcher); when set, used
    /// instead of stat'ing.
    /// </summary>
    public IFileStamps? Stamps { get; set; }

    private DateTime? Stamp(string path)
    {
        if (Stamps != null)
            return Stamps.Get(path);
        if (_stampCache == null)
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

        if (!_stampCache.TryGetValue(path, out var stamp))
        {
            var info = new FileInfo(path);
            stamp = info.Exists ? info.LastWriteTimeUtc : null;
            _stampCache[path] = stamp;
        }
        return stamp;
    }

    /// <summary>
    /// Checks if an action is up-to-date by comparing its current command-line digest
    /// against the digest recorded for its primary output on a previous build. An edit
    /// to a rules file that changes no actual compiler flag leaves the digest unchanged
    /// and invalidates nothing.
    /// </summary>
    public bool IsUpToDate(BuildAction action, IDigestCalculator calculator, ActionDigestStore digestStore) =>
        Record(action, WhyNotUpToDate(action, calculator, digestStore));

    /// <summary>
    /// Why <paramref name="action"/> must run, by timestamps and then by digest; null when it is up to date.
    /// A digest mismatch names the parts that changed ("command-changed flags,input-set").
    /// </summary>
    public string? WhyNotUpToDate(BuildAction action, IDigestCalculator calculator, ActionDigestStore digestStore)
    {
        if (WhyNotUpToDate(action) is { } byTime)
            return byTime;

        var currentDigest = action.ComputeDigest(calculator);
        if (!digestStore.TryGet(action.Outputs[0].Path, out var previousDigest, out var previousParts))
            return "no-previous-digest";
        if (currentDigest.Equals(previousDigest))
            return null;
        if (previousParts == null)
            return "command-changed";

        var now = action.ComputeDigestParts(calculator).Split(':');
        var before = previousParts.Split(':');
        var names = new[] { "flags", "input-set", "environment" };
        var changed = names.Where((_, i) => i >= before.Length || now[i] != before[i]);
        return $"command-changed {string.Join(",", changed)}";
    }

    /// <summary>Returns every action to how the graph builder left it, so a build server can build the graph again.</summary>
    public void ResetForRebuild()
    {
        foreach (var action in Actions)
        {
            action.Status = ActionStatus.Pending;
            action.RebuildReason = action.RootCause = action.SharedWith = null;
            action.ActionDigest = null;
        }
    }

    /// <summary>
    /// Marks digest-up-to-date actions as skipped. Unlike the timestamp-only overload,
    /// this also records the current digest for every action that IS up-to-date, so the
    /// store stays populated even on a build where nothing needed to rebuild.
    /// </summary>
    public int MarkUpToDateActionsAsSkipped(IDigestCalculator calculator, ActionDigestStore digestStore)
    {
        var skipped = 0;
        _stampCache = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var action in GetTopologicalOrder())
            {
                if (action.Status != ActionStatus.Pending)
                    continue;

                // A Pending dependency will still execute this build and produce a fresh
                // output, which the on-disk timestamps below can't see yet - don't trust them.
                if (action.Dependencies.FirstOrDefault(d => d.Status == ActionStatus.Pending) is { } pending)
                {
                    action.RebuildReason = $"dependency-will-run {pending.Description}";
                    action.RootCause = pending.RootCause ?? pending.RebuildReason;
                    continue;
                }

                if (IsUpToDate(action, calculator, digestStore))
                {
                    action.Status = ActionStatus.Skipped;
                    skipped++;
                }
            }
        }
        finally
        {
            _stampCache = null;
        }
        return skipped;
    }

    /// <summary>
    /// Why the still-Pending actions will run: counts per reason kind (the reason's first word), and the root
    /// causes that invalidated the most actions.
    /// </summary>
    public RebuildSummary SummarizeRebuilds(int top = 10)
    {
        var pending = Actions.Where(a => a.Status == ActionStatus.Pending).ToList();
        var byKind = pending
            .GroupBy(a => (a.RebuildReason ?? "unknown").Split(' ')[0])
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());
        var topCauses = pending
            .GroupBy(a => a.RootCause ?? a.RebuildReason ?? "unknown")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(top)
            .Select(g => (g.Key, g.Count()))
            .ToList();
        return new RebuildSummary(pending.Count, byKind, topCauses);
    }

    /// <summary>
    /// Marks up-to-date actions as skipped.
    /// </summary>
    public int MarkUpToDateActionsAsSkipped()
    {
        var skipped = 0;
        _stampCache = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var action in GetTopologicalOrder())
            {
                if (action.Status != ActionStatus.Pending)
                    continue;

                // A Pending dependency will still execute this build and produce a fresh
                // output, which the on-disk timestamps below can't see yet - don't trust them.
                if (action.Dependencies.Any(d => d.Status == ActionStatus.Pending))
                    continue;

                if (IsUpToDate(action))
                {
                    action.Status = ActionStatus.Skipped;
                    skipped++;
                }
            }
        }
        finally
        {
            _stampCache = null;
        }
        return skipped;
    }
    
    /// <summary>
    /// Resets all action statuses to pending.
    /// </summary>
    public void Reset()
    {
        foreach (var action in _actions.Values)
        {
            action.Status = ActionStatus.Pending;
        }
    }
    
    /// <summary>
    /// Gets statistics about the graph.
    /// </summary>
    public GraphStatistics GetStatistics()
    {
        var actions = _actions.Values.ToList();
        return new GraphStatistics
        {
            TotalActions = actions.Count,
            CompileActions = actions.Count(a => a.Type == ActionType.Compile),
            LinkActions = actions.Count(a => a.Type == ActionType.Link),
            ArchiveActions = actions.Count(a => a.Type == ActionType.Archive),
            OtherActions = actions.Count(a => a.Type is ActionType.Copy or ActionType.Custom),
            TotalDependencies = actions.Sum(a => a.Dependencies.Count),
            MaxDepth = GetMaxDepth(),
            EstimatedSerialDuration = TimeSpan.FromSeconds(actions.Sum(a => a.EstimatedDuration.TotalSeconds)),
            EstimatedParallelDuration = TimeSpan.FromSeconds(GetCriticalPath().Sum(a => a.EstimatedDuration.TotalSeconds))
        };
    }
    
    private int GetMaxDepth()
    {
        var depths = new Dictionary<string, int>();
        foreach (var action in GetTopologicalOrder())
        {
            var maxDepth = action.Dependencies.Count > 0 
                ? action.Dependencies.Max(d => depths.GetValueOrDefault(d.Id, 0)) + 1 
                : 0;
            depths[action.Id] = maxDepth;
        }
        return depths.Values.DefaultIfEmpty(0).Max();
    }
}

/// <summary>
/// Statistics about the action graph.
/// </summary>
public sealed class GraphStatistics
{
    public int TotalActions { get; init; }
    public int CompileActions { get; init; }
    public int LinkActions { get; init; }
    public int ArchiveActions { get; init; }
    public int OtherActions { get; init; }
    public int TotalDependencies { get; init; }
    public int MaxDepth { get; init; }
    public TimeSpan EstimatedSerialDuration { get; init; }
    public TimeSpan EstimatedParallelDuration { get; init; }
}
