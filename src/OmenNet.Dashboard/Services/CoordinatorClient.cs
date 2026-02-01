// OmenNet Dashboard
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using Omen.Distributed.Protos;

namespace OmenNet.Dashboard.Services;

/// <summary>
/// gRPC client for communicating with OmenNet coordinator.
/// </summary>
public class CoordinatorClient : ICoordinatorClient, IDisposable
{
    private readonly OmenNetOptions _options;
    private readonly ILogger<CoordinatorClient> _logger;
    private GrpcChannel? _channel;
    private OmenCoordinator.OmenCoordinatorClient? _client;
    private bool _isConnected;
    private DateTime? _startTime;

    public CoordinatorClient(IOptions<OmenNetOptions> options, ILogger<CoordinatorClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConnected => _isConnected;

    private OmenCoordinator.OmenCoordinatorClient GetClient()
    {
        if (_client == null)
        {
            var address = $"http://{_options.CoordinatorHost}:{_options.CoordinatorPort}";
            _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
                }
            });
            _client = new OmenCoordinator.OmenCoordinatorClient(_channel);
        }
        return _client;
    }

    public async Task<CoordinatorStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var response = await client.GetStatusAsync(new GetStatusRequest(), cancellationToken: ct);

            _isConnected = true;
            _startTime ??= DateTime.UtcNow.AddSeconds(-1); // Approximate if first call

            return new CoordinatorStatus(
                IsRunning: true,
                Version: "1.0.0",
                StartTime: _startTime.Value,
                TotalAgents: response.RegisteredAgents,
                ActiveAgents: response.ActiveAgents,
                QueuedJobs: response.QueuedOperations,
                ActiveJobs: response.ExecutingOperations,
                TotalActionsProcessed: response.CompletedOperations + response.FailedOperations,
                TotalBytesTransferred: 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get coordinator status, using offline state");
            _isConnected = false;

            return new CoordinatorStatus(
                IsRunning: false,
                Version: "1.0.0",
                StartTime: DateTime.UtcNow,
                TotalAgents: 0,
                ActiveAgents: 0,
                QueuedJobs: 0,
                ActiveJobs: 0,
                TotalActionsProcessed: 0,
                TotalBytesTransferred: 0);
        }
    }

    public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var response = await client.GetStatusAsync(new GetStatusRequest(), cancellationToken: ct);

            _isConnected = true;

            var agents = new List<AgentInfo>();
            foreach (var agent in response.Agents)
            {
                var platform = "Unknown";
                var arch = "Unknown";
                if (agent.Platform?.Properties != null)
                {
                    agent.Platform.Properties.TryGetValue("os", out platform);
                    agent.Platform.Properties.TryGetValue("arch", out arch);
                }

                var state = agent.IsActive ? AgentState.Idle : AgentState.Offline;
                if (agent.Status?.ActiveActions > 0)
                {
                    state = AgentState.Busy;
                }

                agents.Add(new AgentInfo(
                    agent.AgentId,
                    $"Agent-{agent.AgentId[..Math.Min(6, agent.AgentId.Length)]}",
                    platform ?? "Unknown",
                    arch ?? "Unknown",
                    agent.Status?.ActiveActions ?? 4,
                    agent.Status?.ActiveActions ?? 0,
                    state,
                    DateTime.FromBinary(agent.LastHeartbeat),
                    0,
                    TimeSpan.Zero
                ));
            }

            return agents;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get agents");
            _isConnected = false;
            return [];
        }
    }

    public async Task<IReadOnlyList<BuildJobInfo>> GetActiveJobsAsync(CancellationToken ct = default)
    {
        try
        {
            // The current proto doesn't have a dedicated job listing endpoint
            // For now, return empty - would need to extend the proto
            await Task.CompletedTask;
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get active jobs");
            return [];
        }
    }

    public async Task<IReadOnlyList<BuildHistoryEntry>> GetHistoryAsync(int count = 100, CancellationToken ct = default)
    {
        try
        {
            // The current proto doesn't have a history endpoint
            // For now, return empty - would need to extend the proto
            await Task.CompletedTask;
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get history");
            return [];
        }
    }

    public async Task<CasStats> GetCasStatsAsync(CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var response = await client.GetStatusAsync(new GetStatusRequest(), cancellationToken: ct);

            _isConnected = true;

            var hits = response.CacheHits;
            var misses = response.CacheMisses;
            var total = hits + misses;
            var ratio = total > 0 ? (double)hits / total : 0;

            return new CasStats(
                TotalObjects: total,
                TotalSizeBytes: 0, // Would need CAS service call
                CacheHits: hits,
                CacheMisses: misses,
                HitRatio: ratio);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get CAS stats");
            _isConnected = false;
            return new CasStats(0, 0, 0, 0, 0);
        }
    }

    public async Task DisconnectAgentAsync(string agentId, CancellationToken ct = default)
    {
        _logger.LogInformation("Request to disconnect agent {AgentId} - would require agent service call", agentId);
        await Task.CompletedTask;
    }

    public async Task CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        _logger.LogInformation("Request to cancel job {JobId} - would require coordinator extension", jobId);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }
}
