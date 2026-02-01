// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Distributed.Coordinator;

/// <summary>
/// In-memory operation queue with priority scheduling and platform matching.
/// </summary>
public sealed class OperationQueue
{
    private readonly ConcurrentDictionary<string, QueuedOperation> _operations = new();
    private readonly PriorityQueue<string, int> _readyQueue = new();
    private readonly object _lock = new();
    
    /// <summary>
    /// Enqueues an operation for execution.
    /// </summary>
    public void Enqueue(QueuedOperation operation)
    {
        if (!_operations.TryAdd(operation.OperationName, operation))
            throw new InvalidOperationException($"Operation {operation.OperationName} already exists.");
        
        lock (_lock)
        {
            _readyQueue.Enqueue(operation.OperationName, operation.Priority);
        }
    }
    
    /// <summary>
    /// Dequeues the next operation matching the platform requirements.
    /// </summary>
    public QueuedOperation? Dequeue(AgentCapabilities agentCapabilities)
    {
        lock (_lock)
        {
            // Simple FIFO with priority for now
            // A full implementation would match platform properties
            while (_readyQueue.TryDequeue(out var operationName, out _))
            {
                if (_operations.TryGetValue(operationName, out var operation))
                {
                    if (operation.State == OperationState.Queued && MatchesPlatform(operation, agentCapabilities))
                    {
                        operation.State = OperationState.Executing;
                        return operation;
                    }
                }
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Gets an operation by name.
    /// </summary>
    public QueuedOperation? Get(string operationName)
    {
        return _operations.GetValueOrDefault(operationName);
    }
    
    /// <summary>
    /// Marks an operation as completed.
    /// </summary>
    public void Complete(string operationName, RemoteActionResult result)
    {
        if (_operations.TryGetValue(operationName, out var operation))
        {
            operation.State = result.Success ? OperationState.Completed : OperationState.Failed;
            operation.Result = result;
        }
    }
    
    /// <summary>
    /// Requeues a failed operation for retry.
    /// </summary>
    public bool Requeue(string operationName)
    {
        if (_operations.TryGetValue(operationName, out var operation))
        {
            if (operation.RetryCount < operation.MaxRetries)
            {
                operation.RetryCount++;
                operation.State = OperationState.Queued;
                
                lock (_lock)
                {
                    // Lower priority on retry
                    _readyQueue.Enqueue(operationName, operation.Priority + 100);
                }
                
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Cancels an operation.
    /// </summary>
    public bool Cancel(string operationName)
    {
        if (_operations.TryGetValue(operationName, out var operation))
        {
            operation.State = OperationState.Cancelled;
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Removes completed/failed operations older than the specified age.
    /// </summary>
    public int Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var removed = 0;
        
        foreach (var (name, op) in _operations)
        {
            if (op.State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled 
                && op.CreatedAt < cutoff)
            {
                if (_operations.TryRemove(name, out _))
                    removed++;
            }
        }
        
        return removed;
    }
    
    /// <summary>
    /// Gets queue statistics.
    /// </summary>
    public QueueStatistics GetStatistics()
    {
        var operations = _operations.Values.ToList();
        return new QueueStatistics
        {
            TotalOperations = operations.Count,
            QueuedOperations = operations.Count(o => o.State == OperationState.Queued),
            ExecutingOperations = operations.Count(o => o.State == OperationState.Executing),
            CompletedOperations = operations.Count(o => o.State == OperationState.Completed),
            FailedOperations = operations.Count(o => o.State == OperationState.Failed)
        };
    }
    
    private static bool MatchesPlatform(QueuedOperation operation, AgentCapabilities capabilities)
    {
        // Match required platform properties
        foreach (var (key, value) in operation.RequiredPlatform)
        {
            if (!capabilities.Properties.TryGetValue(key, out var agentValue) || agentValue != value)
                return false;
        }
        
        return true;
    }
}

/// <summary>
/// An operation in the queue.
/// </summary>
public sealed class QueuedOperation
{
    public required string OperationName { get; init; }
    public required BuildAction Action { get; init; }
    public required ContentDigest ActionDigest { get; init; }
    public required string CommandLine { get; init; }
    public required string WorkingDirectory { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required IReadOnlyList<string> InputFiles { get; init; }
    public required IReadOnlyList<string> OutputFiles { get; init; }
    public Dictionary<string, string> RequiredPlatform { get; init; } = [];
    public int Priority { get; init; } = 0;
    public int MaxRetries { get; init; } = 2;
    public int RetryCount { get; set; } = 0;
    public OperationState State { get; set; } = OperationState.Queued;
    public RemoteActionResult? Result { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? AssignedAgentId { get; set; }
}

/// <summary>
/// Queue statistics.
/// </summary>
public sealed class QueueStatistics
{
    public int TotalOperations { get; init; }
    public int QueuedOperations { get; init; }
    public int ExecutingOperations { get; init; }
    public int CompletedOperations { get; init; }
    public int FailedOperations { get; init; }
}
