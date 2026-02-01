// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using Grpc.Core;
using Omen.Distributed.Protos;
using ProtoState = Omen.Distributed.Protos.OperationState;

namespace Omen.Distributed.Server;

/// <summary>
/// gRPC service implementation for the OmenNet coordinator.
/// </summary>
public class OmenCoordinatorService : OmenCoordinator.OmenCoordinatorBase
{
    private readonly CoordinatorState _state;

    public OmenCoordinatorService(CoordinatorState state)
    {
        _state = state;
    }

    /// <summary>
    /// Gets the current coordinator status including agent counts and cache statistics.
    /// </summary>
    public override Task<Protos.CoordinatorStatus> GetStatus(GetStatusRequest request, ServerCallContext context)
    {
        var stats = _state.GetStats();

        var status = new Protos.CoordinatorStatus
        {
            RegisteredAgents = stats.RegisteredAgents,
            ActiveAgents = stats.ActiveAgents,
            QueuedOperations = stats.QueuedOperations,
            ExecutingOperations = stats.ExecutingOperations,
            CompletedOperations = stats.CompletedOperations,
            FailedOperations = stats.FailedOperations,
            CacheHits = stats.CacheHits,
            CacheMisses = stats.CacheMisses
        };

        // Add agent info
        foreach (var agent in _state.GetAgents())
        {
            status.Agents.Add(new Protos.AgentInfo
            {
                AgentId = agent.AgentId,
                Platform = new Platform { Properties = { { "os", agent.Platform }, { "arch", agent.Architecture } } },
                IsActive = agent.IsActive,
                LastHeartbeat = agent.LastHeartbeat.Ticks
            });
        }

        return Task.FromResult(status);
    }

    /// <summary>
    /// Executes an action by queueing it and streaming operation status updates until completion.
    /// </summary>
    public override async Task Execute(ExecuteRequest request, IServerStreamWriter<OperationStatus> responseStream, ServerCallContext context)
    {
        var operationId = Guid.NewGuid().ToString("N")[..16];

        _state.QueueOperation(operationId, request.Action);

        // Send queued status
        await responseStream.WriteAsync(new OperationStatus
        {
            Name = operationId,
            Done = false,
            State = ProtoState.Queued
        });

        // Wait for completion or cancellation
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var opStatus = _state.GetOperationStatus(operationId);
            if (opStatus == null) break;

            await responseStream.WriteAsync(opStatus);

            if (opStatus.Done)
            {
                break;
            }

            await Task.Delay(100, context.CancellationToken);
        }
    }

    /// <summary>
    /// Retrieves a cached ActionResult for a given action digest.
    /// Throws NotFound if the action result is not present in the cache.
    /// </summary>
    public override Task<ActionResult> GetActionResult(GetActionResultRequest request, ServerCallContext context)
    {
        var result = _state.GetCachedResult(request.ActionDigest);
        if (result == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Action result not found"));
        }
        return Task.FromResult(result);
    }

    /// <summary>
    /// Updates the action result cache with the provided ActionResult for the specified digest.
    /// </summary>
    public override Task<UpdateActionResultResponse> UpdateActionResult(UpdateActionResultRequest request, ServerCallContext context)
    {
        _state.CacheResult(request.ActionDigest, request.ActionResult);
        return Task.FromResult(new UpdateActionResultResponse());
    }
}

/// <summary>
/// gRPC service for agent registration and task dequeuing.
/// </summary>
public class OmenAgentService : OmenAgent.OmenAgentBase
{
    private readonly CoordinatorState _state;

    public OmenAgentService(CoordinatorState state)
    {
        _state = state;
    }

    /// <summary>
    /// Registers an agent with the coordinator and returns an assigned ID.
    /// </summary>
    public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        var agentId = _state.RegisterAgent(request);

        return Task.FromResult(new RegisterResponse
        {
            Success = true,
            AssignedId = agentId,
            Message = "Agent registered successfully"
        });
    }

    /// <summary>
    /// Handles heartbeat updates from agents and updates their current job count.
    /// </summary>
    public override Task<HeartbeatResponse> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        _state.UpdateAgentHeartbeat(request.AgentId, request.Status?.ActiveActions ?? 0);

        return Task.FromResult(new HeartbeatResponse
        {
            Acknowledged = true
        });
    }

    /// <summary>
    /// Dequeues the next operation for an agent, or returns an empty operation if none available.
    /// </summary>
    public override Task<QueuedOperation> DequeueOperation(DequeueOperationRequest request, ServerCallContext context)
    {
        var operation = _state.DequeueOperation(request.AgentId);

        if (operation == null)
        {
            return Task.FromResult(new QueuedOperation());
        }

        return Task.FromResult(new QueuedOperation
        {
            OperationName = operation.OperationId,
            Action = operation.Action,
            ActionDigest = operation.ActionDigest ?? new Digest()
        });
    }

    /// <summary>
    /// Receives an operation result from an agent and marks the operation as complete.
    /// </summary>
    public override Task<ReportResultResponse> ReportResult(ReportResultRequest request, ServerCallContext context)
    {
        _state.CompleteOperation(request.OperationName, request.Result, request.Success);

        return Task.FromResult(new ReportResultResponse { Acknowledged = true });
    }

    /// <summary>
    /// Unregisters an agent from the coordinator.
    /// </summary>
    public override Task<UnregisterResponse> Unregister(UnregisterRequest request, ServerCallContext context)
    {
        _state.UnregisterAgent(request.AgentId);

        return Task.FromResult(new UnregisterResponse { Success = true });
    }
}

/// <summary>
/// Shared state for the coordinator.
/// </summary>
public class CoordinatorState
{
    private readonly ConcurrentDictionary<string, AgentRegistration> _agents = new();
    private readonly ConcurrentDictionary<string, QueuedOperationInfo> _operations = new();
    private readonly ConcurrentDictionary<string, ActionResult> _actionCache = new();
    private readonly ConcurrentQueue<QueuedOperationInfo> _operationQueue = new();

    private long _completedOperations;
    private long _failedOperations;
    private long _cacheHits;
    private long _cacheMisses;

    public DateTime StartTime { get; } = DateTime.UtcNow;
    public string Version { get; } = "1.0.0";

    public event EventHandler? StateChanged;

    /// <summary>
    /// Gets coordinator statistics such as registered agents and cache metrics.
    /// </summary>
    public CoordinatorStats GetStats()
    {
        var activeAgents = _agents.Values.Count(a =>
            (DateTime.UtcNow - a.LastHeartbeat).TotalSeconds < 30);

        var cacheHits = _cacheHits;
        var cacheMisses = _cacheMisses;

        return new CoordinatorStats
        {
            RegisteredAgents = _agents.Count,
            ActiveAgents = activeAgents,
            QueuedOperations = _operationQueue.Count,
            ExecutingOperations = _operations.Values.Count(o => o.State == ProtoState.Executing),
            CompletedOperations = _completedOperations,
            FailedOperations = _failedOperations,
            CacheHits = cacheHits,
            CacheMisses = cacheMisses,
            CacheHitRate = cacheHits + cacheMisses > 0 ? (double)cacheHits / (cacheHits + cacheMisses) : 0
        };
    }

    public IReadOnlyList<AgentRegistration> GetAgents() => _agents.Values.ToList();

    public IReadOnlyList<AgentInfoDto> GetAgentInfoList()
    {
        return _agents.Values.Select(a => new AgentInfoDto
        {
            AgentId = a.AgentId,
            Name = a.Name,
            Platform = a.Platform,
            Architecture = a.Architecture,
            MaxConcurrency = a.MaxConcurrency,
            CurrentJobs = a.CurrentJobs,
            IsActive = a.IsActive,
            LastHeartbeat = a.LastHeartbeat,
            ActionsCompleted = a.ActionsCompleted,
            Uptime = DateTime.UtcNow - a.RegisteredAt
        }).ToList();
    }

    public IReadOnlyList<QueuedOperationInfo> GetActiveOperations() =>
        _operations.Values.Where(o => o.State != ProtoState.Completed).ToList();

    /// <summary>
    /// Registers a new agent and returns the assigned agent identifier.
    /// </summary>
    public string RegisterAgent(RegisterRequest request)
    {
        var agentId = request.AgentId ?? Guid.NewGuid().ToString("N")[..12];

        var platformOs = "Unknown";
        var platformArch = "Unknown";
        if (request.Capabilities?.Platform?.Properties != null)
        {
            request.Capabilities.Platform.Properties.TryGetValue("os", out platformOs);
            request.Capabilities.Platform.Properties.TryGetValue("arch", out platformArch);
        }

        var registration = new AgentRegistration
        {
            AgentId = agentId,
            Name = $"Agent-{agentId[..6]}",
            Platform = platformOs ?? "Unknown",
            Architecture = platformArch ?? "Unknown",
            MaxConcurrency = request.Capabilities?.MaxConcurrentActions ?? 4,
            CurrentJobs = 0,
            RegisteredAt = DateTime.UtcNow,
            LastHeartbeat = DateTime.UtcNow,
            ActionsCompleted = 0
        };

        _agents[agentId] = registration;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return agentId;
    }

    public void UnregisterAgent(string agentId)
    {
        _agents.TryRemove(agentId, out _);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateAgentHeartbeat(string agentId, int currentJobs)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            agent.LastHeartbeat = DateTime.UtcNow;
            agent.CurrentJobs = currentJobs;
        }
    }

    /// <summary>
    /// Queues an operation for execution and marks it as queued.
    /// </summary>
    public void QueueOperation(string operationId, Protos.Action action)
    {
        var op = new QueuedOperationInfo
        {
            OperationId = operationId,
            Action = action,
            ActionDigest = action.CommandDigest,
            QueuedAt = DateTime.UtcNow,
            State = ProtoState.Queued
        };

        _operations[operationId] = op;
        _operationQueue.Enqueue(op);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Dequeues the next available operation for the given agent, if any.
    /// </summary>
    public QueuedOperationInfo? DequeueOperation(string agentId)
    {
        if (_operationQueue.TryDequeue(out var op))
        {
            op.State = ProtoState.Executing;
            op.AssignedAgentId = agentId;
            op.StartedAt = DateTime.UtcNow;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return op;
        }
        return null;
    }

    /// <summary>
    /// Marks an operation as complete and records its result.
    /// </summary>
    public void CompleteOperation(string operationId, ActionResult result, bool success)
    {
        if (_operations.TryGetValue(operationId, out var op))
        {
            op.State = success ? ProtoState.Completed : ProtoState.Failed;
            op.CompletedAt = DateTime.UtcNow;
            op.Result = result;

            if (success)
            {
                Interlocked.Increment(ref _completedOperations);
            }
            else
            {
                Interlocked.Increment(ref _failedOperations);
            }

            if (_agents.TryGetValue(op.AssignedAgentId ?? "", out var agent))
            {
                agent.ActionsCompleted++;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets the status of a queued or executing operation, if present.
    /// </summary>
    public OperationStatus? GetOperationStatus(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out var op))
            return null;

        return new OperationStatus
        {
            Name = operationId,
            Done = op.State == ProtoState.Completed || op.State == ProtoState.Failed,
            State = op.State,
            Result = op.Result
        };
    }

    /// <summary>
    /// Retrieves an action result from the result cache if present.
    /// </summary>
    public ActionResult? GetCachedResult(Digest actionDigest)
    {
        var key = $"{actionDigest.Hash}:{actionDigest.SizeBytes}";
        if (_actionCache.TryGetValue(key, out var result))
        {
            Interlocked.Increment(ref _cacheHits);
            return result;
        }
        Interlocked.Increment(ref _cacheMisses);
        return null;
    }

    /// <summary>
    /// Stores an action result in the result cache keyed by digest.
    /// </summary>
    public void CacheResult(Digest actionDigest, ActionResult result)
    {
        var key = $"{actionDigest.Hash}:{actionDigest.SizeBytes}";
        _actionCache[key] = result;
    }
}

/// <summary>
/// Agent registration info.
/// </summary>
public class AgentRegistration
{
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Architecture { get; set; } = "";
    public int MaxConcurrency { get; set; }
    public int CurrentJobs { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public long ActionsCompleted { get; set; }

    public bool IsActive => (DateTime.UtcNow - LastHeartbeat).TotalSeconds < 30;
}

/// <summary>
/// DTO for agent info returned via API.
/// </summary>
public class AgentInfoDto
{
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Architecture { get; set; } = "";
    public int MaxConcurrency { get; set; }
    public int CurrentJobs { get; set; }
    public bool IsActive { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public long ActionsCompleted { get; set; }
    public TimeSpan Uptime { get; set; }
}

/// <summary>
/// Queued operation info.
/// </summary>
public class QueuedOperationInfo
{
    public string OperationId { get; set; } = "";
    public Protos.Action Action { get; set; } = new();
    public Digest? ActionDigest { get; set; }
    public DateTime QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? AssignedAgentId { get; set; }
    public ProtoState State { get; set; }
    public ActionResult? Result { get; set; }
}

/// <summary>
/// Coordinator statistics.
/// </summary>
public class CoordinatorStats
{
    public int RegisteredAgents { get; set; }
    public int ActiveAgents { get; set; }
    public int QueuedOperations { get; set; }
    public int ExecutingOperations { get; set; }
    public long CompletedOperations { get; set; }
    public long FailedOperations { get; set; }
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public double CacheHitRate { get; set; }
}
