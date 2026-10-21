using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
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

	public bool IsConnected => _isConnected;

	public CoordinatorClient(IOptions<OmenNetOptions> options, ILogger<CoordinatorClient> logger)
	{
		_options = options.Value;
		_logger = logger;
	}

	private OmenCoordinator.OmenCoordinatorClient GetClient()
	{
		if (_client == null)
		{
			string address = $"http://{_options.CoordinatorHost}:{_options.CoordinatorPort}";
			_channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
			{
				HttpHandler = new SocketsHttpHandler
				{
					EnableMultipleHttp2Connections = true,
					KeepAlivePingDelay = TimeSpan.FromSeconds(60.0),
					KeepAlivePingTimeout = TimeSpan.FromSeconds(30.0)
				}
			});
			_client = new OmenCoordinator.OmenCoordinatorClient(_channel);
		}
		return _client;
	}

	public async Task<CoordinatorStatus> GetStatusAsync(CancellationToken ct = default(CancellationToken))
	{
		try
		{
			OmenCoordinator.OmenCoordinatorClient client = GetClient();
			Omen.Distributed.Protos.CoordinatorStatus response = await client.GetStatusAsync(new GetStatusRequest(), null, null, ct);
			_isConnected = true;
			_startTime.GetValueOrDefault();
			if (!_startTime.HasValue)
			{
				DateTime value = DateTime.UtcNow.AddSeconds(-1.0);
				_startTime = value;
			}
			return new CoordinatorStatus(IsRunning: true, "1.0.0", _startTime.Value, response.RegisteredAgents, response.ActiveAgents, response.QueuedOperations, response.ExecutingOperations, response.CompletedOperations + response.FailedOperations, 0L);
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Failed to get coordinator status, using offline state");
			_isConnected = false;
			return new CoordinatorStatus(IsRunning: false, "1.0.0", DateTime.UtcNow, 0, 0, 0, 0, 0L, 0L);
		}
	}

	public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct = default(CancellationToken))
	{
		try
		{
			OmenCoordinator.OmenCoordinatorClient client = GetClient();
			Omen.Distributed.Protos.CoordinatorStatus response = await client.GetStatusAsync(new GetStatusRequest(), null, null, ct);
			_isConnected = true;
			List<AgentInfo> agents = new List<AgentInfo>();
			foreach (Omen.Distributed.Protos.AgentInfo agent in response.Agents)
			{
				string platform = "Unknown";
				string arch = "Unknown";
				if (agent.Platform?.Properties != null)
				{
					agent.Platform.Properties.TryGetValue("os", out platform);
					agent.Platform.Properties.TryGetValue("arch", out arch);
				}
				AgentState state = ((!agent.IsActive) ? AgentState.Offline : AgentState.Idle);
				AgentStatus status = agent.Status;
				if (status != null && status.ActiveActions > 0)
				{
					state = AgentState.Busy;
				}
				agents.Add(new AgentInfo(agent.AgentId, "Agent-" + agent.AgentId.Substring(0, Math.Min(6, agent.AgentId.Length)), platform ?? "Unknown", arch ?? "Unknown", agent.Status?.ActiveActions ?? 4, agent.Status?.ActiveActions ?? 0, state, DateTime.FromBinary(agent.LastHeartbeat), 0L, TimeSpan.Zero));
			}
			return agents;
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Failed to get agents");
			_isConnected = false;
			return Array.Empty<AgentInfo>();
		}
	}

	public async Task<IReadOnlyList<BuildJobInfo>> GetActiveJobsAsync(CancellationToken ct = default(CancellationToken))
	{
		try
		{
			await Task.CompletedTask;
			return Array.Empty<BuildJobInfo>();
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Failed to get active jobs");
			return Array.Empty<BuildJobInfo>();
		}
	}

	public async Task<IReadOnlyList<BuildHistoryEntry>> GetHistoryAsync(int count = 100, CancellationToken ct = default(CancellationToken))
	{
		try
		{
			await Task.CompletedTask;
			return Array.Empty<BuildHistoryEntry>();
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Failed to get history");
			return Array.Empty<BuildHistoryEntry>();
		}
	}

	public async Task<CasStats> GetCasStatsAsync(CancellationToken ct = default(CancellationToken))
	{
		try
		{
			OmenCoordinator.OmenCoordinatorClient client = GetClient();
			Omen.Distributed.Protos.CoordinatorStatus response = await client.GetStatusAsync(new GetStatusRequest(), null, null, ct);
			_isConnected = true;
			long hits = response.CacheHits;
			long misses = response.CacheMisses;
			long total = hits + misses;
			double ratio = ((total > 0) ? ((double)hits / (double)total) : 0.0);
			return new CasStats(total, 0L, hits, misses, ratio);
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Failed to get CAS stats");
			_isConnected = false;
			return new CasStats(0L, 0L, 0L, 0L, 0.0);
		}
	}

	public async Task DisconnectAgentAsync(string agentId, CancellationToken ct = default(CancellationToken))
	{
		_logger.LogInformation("Request to disconnect agent {AgentId} - would require agent service call", agentId);
		await Task.CompletedTask;
	}

	public async Task CancelJobAsync(string jobId, CancellationToken ct = default(CancellationToken))
	{
		_logger.LogInformation("Request to cancel job {JobId} - would require coordinator extension", jobId);
		await Task.CompletedTask;
	}

	public void Dispose()
	{
		_channel?.Dispose();
	}
}
