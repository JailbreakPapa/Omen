// OmenNet Dashboard
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace OmenNet.Dashboard.Services;

/// <summary>
/// Client interface for communicating with OmenNet coordinator.
/// </summary>
public interface ICoordinatorClient
{
    /// <summary>
    /// Gets whether the client is connected to the coordinator.
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Gets the coordinator status.
    /// </summary>
    Task<CoordinatorStatus> GetStatusAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets all registered agents.
    /// </summary>
    Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets active build jobs.
    /// </summary>
    Task<IReadOnlyList<BuildJobInfo>> GetActiveJobsAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets recent build history.
    /// </summary>
    Task<IReadOnlyList<BuildHistoryEntry>> GetHistoryAsync(int count = 100, CancellationToken ct = default);
    
    /// <summary>
    /// Gets CAS (Content Addressable Storage) statistics.
    /// </summary>
    Task<CasStats> GetCasStatsAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Disconnects an agent.
    /// </summary>
    Task DisconnectAgentAsync(string agentId, CancellationToken ct = default);
    
    /// <summary>
    /// Cancels a build job.
    /// </summary>
    Task CancelJobAsync(string jobId, CancellationToken ct = default);
}

public record CoordinatorStatus(
    bool IsRunning,
    string Version,
    DateTime StartTime,
    int TotalAgents,
    int ActiveAgents,
    int QueuedJobs,
    int ActiveJobs,
    long TotalActionsProcessed,
    long TotalBytesTransferred);

public record AgentInfo(
    string Id,
    string Name,
    string Platform,
    string Architecture,
    int MaxConcurrency,
    int CurrentJobs,
    AgentState State,
    DateTime LastHeartbeat,
    long ActionsCompleted,
    TimeSpan TotalCpuTime);

public enum AgentState
{
    Idle,
    Busy,
    Offline,
    Error
}

public record BuildJobInfo(
    string JobId,
    string ProjectName,
    string Platform,
    string Configuration,
    DateTime StartTime,
    int TotalActions,
    int CompletedActions,
    int CachedActions,
    int FailedActions,
    string SubmittedBy);

public record BuildHistoryEntry(
    string JobId,
    string ProjectName,
    DateTime StartTime,
    DateTime EndTime,
    bool Success,
    int TotalActions,
    int CachedActions,
    TimeSpan Duration);

public record CasStats(
    long TotalObjects,
    long TotalSizeBytes,
    long CacheHits,
    long CacheMisses,
    double HitRatio);
