// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;

namespace Omen.Distributed.Coordinator;

/// <summary>
/// Manages registered agents and their status.
/// </summary>
public sealed class AgentManager
{
    private readonly ConcurrentDictionary<string, RegisteredAgent> _agents = new();
    private readonly TimeSpan _heartbeatTimeout;
    
    public AgentManager(TimeSpan? heartbeatTimeout = null)
    {
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromMinutes(2);
    }
    
    /// <summary>
    /// Registers a new agent.
    /// </summary>
    public string Register(string requestedId, AgentCapabilities capabilities)
    {
        var agentId = string.IsNullOrEmpty(requestedId) 
            ? $"agent-{Guid.NewGuid():N}"[..16] 
            : requestedId;
        
        var agent = new RegisteredAgent
        {
            AgentId = agentId,
            Capabilities = capabilities,
            Status = new AgentRuntimeStatus(),
            RegisteredAt = DateTimeOffset.UtcNow,
            LastHeartbeat = DateTimeOffset.UtcNow
        };
        
        _agents[agentId] = agent;
        return agentId;
    }
    
    /// <summary>
    /// Unregisters an agent.
    /// </summary>
    public bool Unregister(string agentId)
    {
        return _agents.TryRemove(agentId, out _);
    }
    
    /// <summary>
    /// Updates agent status from heartbeat.
    /// </summary>
    public bool Heartbeat(string agentId, AgentRuntimeStatus status)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            agent.Status = status;
            agent.LastHeartbeat = DateTimeOffset.UtcNow;
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Gets an agent by ID.
    /// </summary>
    public RegisteredAgent? GetAgent(string agentId)
    {
        return _agents.GetValueOrDefault(agentId);
    }
    
    /// <summary>
    /// Gets all registered agents.
    /// </summary>
    public IReadOnlyList<RegisteredAgent> GetAllAgents()
    {
        return _agents.Values.ToList();
    }
    
    /// <summary>
    /// Gets active agents (within heartbeat timeout).
    /// </summary>
    public IReadOnlyList<RegisteredAgent> GetActiveAgents()
    {
        var cutoff = DateTimeOffset.UtcNow - _heartbeatTimeout;
        return _agents.Values.Where(a => a.LastHeartbeat > cutoff).ToList();
    }
    
    /// <summary>
    /// Selects the best agent for an operation.
    /// </summary>
    public RegisteredAgent? SelectAgent(Dictionary<string, string> requiredPlatform)
    {
        var activeAgents = GetActiveAgents();
        
        // Filter by platform requirements
        var matchingAgents = activeAgents.Where(a => MatchesPlatform(a.Capabilities, requiredPlatform)).ToList();
        
        if (matchingAgents.Count == 0)
            return null;
        
        // Select agent with lowest load
        return matchingAgents
            .OrderBy(a => (double)a.Status.ActiveActions / Math.Max(1, a.Capabilities.MaxConcurrentActions))
            .ThenByDescending(a => a.Capabilities.CpuCores)
            .FirstOrDefault();
    }
    
    /// <summary>
    /// Removes stale agents that haven't sent heartbeats.
    /// </summary>
    public int RemoveStaleAgents()
    {
        var cutoff = DateTimeOffset.UtcNow - _heartbeatTimeout * 2;
        var removed = 0;
        
        foreach (var (agentId, agent) in _agents)
        {
            if (agent.LastHeartbeat < cutoff)
            {
                if (_agents.TryRemove(agentId, out _))
                    removed++;
            }
        }
        
        return removed;
    }
    
    /// <summary>
    /// Gets manager statistics.
    /// </summary>
    public AgentManagerStatistics GetStatistics()
    {
        var agents = _agents.Values.ToList();
        var activeAgents = GetActiveAgents();
        
        return new AgentManagerStatistics
        {
            TotalAgents = agents.Count,
            ActiveAgents = activeAgents.Count,
            TotalCapacity = activeAgents.Sum(a => a.Capabilities.MaxConcurrentActions),
            CurrentLoad = activeAgents.Sum(a => a.Status.ActiveActions)
        };
    }
    
    private static bool MatchesPlatform(AgentCapabilities capabilities, Dictionary<string, string> required)
    {
        foreach (var (key, value) in required)
        {
            if (!capabilities.Properties.TryGetValue(key, out var agentValue) || agentValue != value)
                return false;
        }
        
        return true;
    }
}

/// <summary>
/// A registered agent.
/// </summary>
public sealed class RegisteredAgent
{
    public required string AgentId { get; init; }
    public required AgentCapabilities Capabilities { get; init; }
    public required AgentRuntimeStatus Status { get; set; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastHeartbeat { get; set; }
    public int CompletedActions { get; set; }
    public int FailedActions { get; set; }
}

/// <summary>
/// Agent manager statistics.
/// </summary>
public sealed class AgentManagerStatistics
{
    public int TotalAgents { get; init; }
    public int ActiveAgents { get; init; }
    public int TotalCapacity { get; init; }
    public int CurrentLoad { get; init; }
    public double LoadPercentage => TotalCapacity > 0 ? (double)CurrentLoad / TotalCapacity * 100 : 0;
}
