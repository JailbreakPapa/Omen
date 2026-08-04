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
    public bool IsUpToDate(BuildAction action)
    {
        // If any output doesn't exist, not up-to-date
        if (action.Outputs.Any(o => !File.Exists(o.Path)))
            return false;
        
        // Get the oldest output timestamp
        var oldestOutput = action.Outputs
            .Select(o => File.GetLastWriteTimeUtc(o.Path))
            .Min();
        
        // Get the newest input timestamp
        var newestInput = action.Inputs
            .Where(i => File.Exists(i.Path))
            .Select(i => File.GetLastWriteTimeUtc(i.Path))
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        
        // Also check dependency outputs
        foreach (var dep in action.Dependencies)
        {
            foreach (var output in dep.Outputs)
            {
                if (File.Exists(output.Path))
                {
                    var depTime = File.GetLastWriteTimeUtc(output.Path);
                    if (depTime > newestInput)
                        newestInput = depTime;
                }
            }
        }
        
        return oldestOutput > newestInput;
    }
    
    /// <summary>
    /// Checks if an action is up-to-date by comparing its current command-line digest
    /// against the digest recorded for its primary output on a previous build. An edit
    /// to a rules file that changes no actual compiler flag leaves the digest unchanged
    /// and invalidates nothing.
    /// </summary>
    public bool IsUpToDate(BuildAction action, IDigestCalculator calculator, ActionDigestStore digestStore)
    {
        if (action.Outputs.Count == 0 || action.Outputs.Any(o => !File.Exists(o.Path)))
            return false;

        var currentDigest = action.ComputeDigest(calculator);
        var primaryOutput = action.Outputs[0].Path;

        return digestStore.TryGet(primaryOutput, out var previousDigest) && currentDigest.Equals(previousDigest);
    }

    /// <summary>
    /// Marks digest-up-to-date actions as skipped. Unlike the timestamp-only overload,
    /// this also records the current digest for every action that IS up-to-date, so the
    /// store stays populated even on a build where nothing needed to rebuild.
    /// </summary>
    public int MarkUpToDateActionsAsSkipped(IDigestCalculator calculator, ActionDigestStore digestStore)
    {
        var skipped = 0;
        foreach (var action in GetTopologicalOrder())
        {
            if (action.Status != ActionStatus.Pending)
                continue;

            if (IsUpToDate(action, calculator, digestStore))
            {
                action.Status = ActionStatus.Skipped;
                skipped++;
            }
        }
        return skipped;
    }

    /// <summary>
    /// Marks up-to-date actions as skipped.
    /// </summary>
    public int MarkUpToDateActionsAsSkipped()
    {
        var skipped = 0;
        foreach (var action in GetTopologicalOrder())
        {
            if (action.Status == ActionStatus.Pending && IsUpToDate(action))
            {
                action.Status = ActionStatus.Skipped;
                skipped++;
            }
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
