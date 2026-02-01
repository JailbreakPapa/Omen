// OmenNet Dashboard
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace OmenNet.Dashboard.Services;

/// <summary>
/// Service for aggregating dashboard data.
/// </summary>
public interface IDashboardService
{
    /// <summary>
    /// Event raised when dashboard data changes.
    /// </summary>
    event EventHandler? DataChanged;
    
    /// <summary>
    /// Gets the current coordinator status.
    /// </summary>
    CoordinatorStatus? CurrentStatus { get; }
    
    /// <summary>
    /// Gets the current statistics snapshot.
    /// </summary>
    DashboardStats GetCurrentStats();
    
    /// <summary>
    /// Gets all agents.
    /// </summary>
    IReadOnlyList<AgentInfo> GetAgents();
    
    /// <summary>
    /// Gets active build jobs.
    /// </summary>
    IReadOnlyList<BuildJobInfo> GetActiveJobs();
    
    /// <summary>
    /// Gets build history.
    /// </summary>
    IReadOnlyList<BuildHistoryEntry> GetHistory();
    
    /// <summary>
    /// Gets CAS statistics.
    /// </summary>
    CasStats? GetCasStats();
    
    /// <summary>
    /// Gets throughput metrics over time.
    /// </summary>
    IReadOnlyList<ThroughputSample> GetThroughputHistory();
}

public record DashboardStats(
    bool CoordinatorOnline,
    int TotalAgents,
    int ActiveAgents,
    int ActiveJobs,
    int QueuedActions,
    long TotalActionsToday,
    double CacheHitRatio,
    TimeSpan AverageBuildTime);

public record ThroughputSample(
    DateTime Timestamp,
    int ActionsPerMinute,
    int ActiveAgents);
