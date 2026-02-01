// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;

namespace Omen.Distributed;

/// <summary>
/// OmenNet agent interface for worker nodes.
/// </summary>
public interface IOmenAgent : IAsyncDisposable
{
    /// <summary>
    /// Unique identifier for this agent.
    /// </summary>
    string AgentId { get; }
    
    /// <summary>
    /// Whether the agent is connected to a coordinator.
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Connects to a coordinator.
    /// </summary>
    Task ConnectAsync(string coordinatorAddress, CancellationToken ct = default);
    
    /// <summary>
    /// Disconnects from the coordinator.
    /// </summary>
    Task DisconnectAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Starts processing work from the coordinator.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Stops processing work.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets the current agent status.
    /// </summary>
    AgentRuntimeStatus GetStatus();
    
    /// <summary>
    /// Event raised when an action completes.
    /// </summary>
    event EventHandler<AgentActionCompletedEventArgs>? ActionCompleted;
}

/// <summary>
/// Configuration for an OmenNet agent.
/// </summary>
public sealed class AgentConfiguration
{
    /// <summary>
    /// Maximum number of concurrent actions.
    /// </summary>
    public int MaxConcurrentActions { get; init; } = Environment.ProcessorCount;
    
    /// <summary>
    /// Local cache directory for CAS.
    /// </summary>
    public string CacheDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "Omen", "AgentCache");
    
    /// <summary>
    /// Maximum cache size in bytes.
    /// </summary>
    public long MaxCacheSizeBytes { get; init; } = 10L * 1024 * 1024 * 1024; // 10 GB
    
    /// <summary>
    /// Heartbeat interval.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Sandbox execution (isolate processes).
    /// </summary>
    public bool UseSandbox { get; init; } = true;
    
    /// <summary>
    /// Working directory for action execution.
    /// </summary>
    public string WorkingDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "Omen", "AgentWork");
}

/// <summary>
/// Event args for action completion on agent.
/// </summary>
public sealed class AgentActionCompletedEventArgs : EventArgs
{
    public required string OperationName { get; init; }
    public required bool Success { get; init; }
    public required TimeSpan Duration { get; init; }
}
