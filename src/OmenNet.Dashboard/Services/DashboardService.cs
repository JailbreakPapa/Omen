// OmenNet Dashboard
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;

namespace OmenNet.Dashboard.Services;

/// <summary>
/// Implementation of the dashboard service.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly ICoordinatorClient _client;
    private readonly ILogger<DashboardService> _logger;
    
    private CoordinatorStatus? _status;
    private IReadOnlyList<AgentInfo> _agents = [];
    private IReadOnlyList<BuildJobInfo> _activeJobs = [];
    private IReadOnlyList<BuildHistoryEntry> _history = [];
    private CasStats? _casStats;
    private readonly ConcurrentQueue<ThroughputSample> _throughputHistory = new();
    
    public event EventHandler? DataChanged;
    
    public DashboardService(ICoordinatorClient client, ILogger<DashboardService> logger)
    {
        _client = client;
        _logger = logger;
    }
    
    public CoordinatorStatus? CurrentStatus => _status;
    
    public DashboardStats GetCurrentStats()
    {
        return new DashboardStats(
            CoordinatorOnline: _status?.IsRunning ?? false,
            TotalAgents: _status?.TotalAgents ?? 0,
            ActiveAgents: _status?.ActiveAgents ?? 0,
            ActiveJobs: _status?.ActiveJobs ?? 0,
            QueuedActions: _status?.QueuedJobs ?? 0,
            TotalActionsToday: _status?.TotalActionsProcessed ?? 0,
            CacheHitRatio: _casStats?.HitRatio ?? 0,
            AverageBuildTime: CalculateAverageBuildTime());
    }
    
    public IReadOnlyList<AgentInfo> GetAgents() => _agents;
    
    public IReadOnlyList<BuildJobInfo> GetActiveJobs() => _activeJobs;
    
    public IReadOnlyList<BuildHistoryEntry> GetHistory() => _history;
    
    public CasStats? GetCasStats() => _casStats;
    
    public IReadOnlyList<ThroughputSample> GetThroughputHistory() => _throughputHistory.ToList();
    
    internal async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var statusTask = _client.GetStatusAsync(ct);
            var agentsTask = _client.GetAgentsAsync(ct);
            var jobsTask = _client.GetActiveJobsAsync(ct);
            var historyTask = _client.GetHistoryAsync(100, ct);
            var casTask = _client.GetCasStatsAsync(ct);
            
            await Task.WhenAll(statusTask, agentsTask, jobsTask, historyTask, casTask);
            
            _status = await statusTask;
            _agents = await agentsTask;
            _activeJobs = await jobsTask;
            _history = await historyTask;
            _casStats = await casTask;
            
            // Record throughput sample
            var sample = new ThroughputSample(
                DateTime.UtcNow,
                CalculateActionsPerMinute(),
                _agents.Count(a => a.State == AgentState.Busy));
            
            _throughputHistory.Enqueue(sample);
            
            // Keep only last hour of samples
            while (_throughputHistory.Count > 720) // 12 per minute * 60 minutes
            {
                _throughputHistory.TryDequeue(out _);
            }
            
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh dashboard data");
        }
    }
    
    private TimeSpan CalculateAverageBuildTime()
    {
        if (_history.Count == 0) return TimeSpan.Zero;
        
        var avgTicks = (long)_history.Average(h => h.Duration.Ticks);
        return TimeSpan.FromTicks(avgTicks);
    }
    
    private int CalculateActionsPerMinute()
    {
        // Estimate based on active jobs
        return _activeJobs.Sum(j => (int)((j.CompletedActions + j.CachedActions) / 
            Math.Max(1, (DateTime.UtcNow - j.StartTime).TotalMinutes)));
    }
}
