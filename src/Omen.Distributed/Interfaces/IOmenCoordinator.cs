// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Distributed;

/// <summary>
/// Distributed build coordinator interface.
/// </summary>
public interface IOmenCoordinator
{
    /// <summary>
    /// Submits an action for remote execution.
    /// </summary>
    Task<OperationHandle> ExecuteAsync(BuildAction action, CancellationToken ct = default);
    
    /// <summary>
    /// Waits for an operation to complete.
    /// </summary>
    Task<RemoteActionResult> WaitForCompletionAsync(OperationHandle handle, CancellationToken ct = default);
    
    /// <summary>
    /// Checks if an action result is cached.
    /// </summary>
    Task<RemoteActionResult?> GetCachedResultAsync(ContentDigest actionDigest, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the current coordinator status.
    /// </summary>
    Task<CoordinatorStatus> GetStatusAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Event raised when operation status changes.
    /// </summary>
    event EventHandler<OperationStatusChangedEventArgs>? OperationStatusChanged;
}

/// <summary>
/// Handle to a submitted operation.
/// </summary>
public sealed class OperationHandle
{
    public required string OperationName { get; init; }
    public required ContentDigest ActionDigest { get; init; }
    public OperationState State { get; set; } = OperationState.Queued;
}

/// <summary>
/// Result of a remotely executed action.
/// </summary>
public sealed class RemoteActionResult
{
    public required bool Success { get; init; }
    public required int ExitCode { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public IReadOnlyList<RemoteOutputFile> OutputFiles { get; init; } = [];
    public RemoteExecutionMetadata? Metadata { get; init; }
}

/// <summary>
/// Remote output file with digest.
/// </summary>
public sealed class RemoteOutputFile
{
    public required string Path { get; init; }
    public required ContentDigest Digest { get; init; }
    public bool IsExecutable { get; init; }
}

/// <summary>
/// Execution metadata from remote execution.
/// </summary>
public sealed class RemoteExecutionMetadata
{
    public string? WorkerId { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? ExecutionStartedAt { get; init; }
    public DateTimeOffset? ExecutionCompletedAt { get; init; }
    public TimeSpan QueueDuration => ExecutionStartedAt.HasValue ? ExecutionStartedAt.Value - QueuedAt : TimeSpan.Zero;
    public TimeSpan ExecutionDuration => ExecutionStartedAt.HasValue && ExecutionCompletedAt.HasValue 
        ? ExecutionCompletedAt.Value - ExecutionStartedAt.Value 
        : TimeSpan.Zero;
}

/// <summary>
/// State of an operation.
/// </summary>
public enum OperationState
{
    Unknown,
    Queued,
    Executing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Status of the coordinator.
/// </summary>
public sealed class CoordinatorStatus
{
    public int RegisteredAgents { get; init; }
    public int ActiveAgents { get; init; }
    public int QueuedOperations { get; init; }
    public int ExecutingOperations { get; init; }
    public long CompletedOperations { get; init; }
    public long FailedOperations { get; init; }
    public long CacheHits { get; init; }
    public long CacheMisses { get; init; }
    public IReadOnlyList<AgentInfo> Agents { get; init; } = [];
}

/// <summary>
/// Information about a registered agent.
/// </summary>
public sealed class AgentInfo
{
    public required string AgentId { get; init; }
    public required AgentCapabilities Capabilities { get; init; }
    public required AgentRuntimeStatus Status { get; init; }
    public DateTimeOffset LastHeartbeat { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>
/// Agent capabilities.
/// </summary>
public sealed class AgentCapabilities
{
    public string OperatingSystem { get; init; } = "";
    public string Architecture { get; init; } = "";
    public int MaxConcurrentActions { get; init; } = 4;
    public long AvailableMemoryBytes { get; init; }
    public int CpuCores { get; init; }
    public IReadOnlyList<string> SupportedToolchains { get; init; } = [];
    public Dictionary<string, string> Properties { get; init; } = [];
}

/// <summary>
/// Runtime status of an agent.
/// </summary>
public sealed class AgentRuntimeStatus
{
    public int ActiveActions { get; init; }
    public int QueuedActions { get; init; }
    public double CpuUsage { get; init; }
    public long AvailableMemoryBytes { get; init; }
    public long CacheSizeBytes { get; init; }
}

/// <summary>
/// Event args for operation status changes.
/// </summary>
public sealed class OperationStatusChangedEventArgs : EventArgs
{
    public required string OperationName { get; init; }
    public required OperationState OldState { get; init; }
    public required OperationState NewState { get; init; }
    public RemoteActionResult? Result { get; init; }
}
