using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Omen.Core.Graph;
using Omen.Core.Interfaces;
using Omen.Distributed;

namespace Omen.Executors;

/// <summary>
/// Hybrid executor that distributes work between local and remote execution.
/// Uses dynamic execution to race local vs remote for optimal latency.
/// </summary>
public sealed class HybridExecutor : IExecutor
{
	private readonly int _localParallelism;

	private readonly IOmenCoordinator _coordinator;

	private readonly IActionCache? _actionCache;

	private readonly HybridExecutorOptions _options;

	public string Name => "Hybrid Executor";

	public int MaxParallelism => _localParallelism + _options.MaxRemoteActions;

	public HybridExecutor(IOmenCoordinator coordinator, HybridExecutorOptions? options = null, IActionCache? actionCache = null)
	{
		_coordinator = coordinator;
		_options = options ?? new HybridExecutorOptions();
		_localParallelism = _options.LocalParallelism ?? (Environment.ProcessorCount / 2);
		_actionCache = actionCache;
	}

	public async Task<BuildResult> ExecuteAsync(ActionGraph graph, IProgress<BuildProgress>? progress = null, CancellationToken ct = default(CancellationToken))
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		List<ActionResult> results = new List<ActionResult>();
		int completedCount = 0;
		int cachedCount = 0;
		int remoteCount = 0;
		int skippedCount = graph.MarkUpToDateActionsAsSkipped();
		graph.ComputePriorities();
		int totalActions = graph.Actions.Count;
		SemaphoreSlim localSemaphore = new SemaphoreSlim(_localParallelism);
		SemaphoreSlim remoteSemaphore = new SemaphoreSlim(_options.MaxRemoteActions);
		Dictionary<string, Task<ActionResult>> runningTasks = new Dictionary<string, Task<ActionResult>>();
		HashSet<string> failedActions = new HashSet<string>();
		object actionLock = new object();
		CoordinatorStatus coordinatorStatus = await TryGetCoordinatorStatusAsync(ct);
		bool useRemote = coordinatorStatus != null && coordinatorStatus.ActiveAgents > 0;
		while (!graph.IsComplete && !ct.IsCancellationRequested)
		{
			List<BuildAction> readyActions = (from a in graph.GetReadyActions()
				where !failedActions.Contains(a.Id) && !runningTasks.ContainsKey(a.Id)
				orderby a.Priority
				select a).ToList();
			if (readyActions.Count == 0)
			{
				if (runningTasks.Count <= 0)
				{
					break;
				}
				Task<ActionResult> completed = await Task.WhenAny(runningTasks.Values);
				string completedId = runningTasks.First((KeyValuePair<string, Task<ActionResult>> kvp) => kvp.Value == completed).Key;
				runningTasks.Remove(completedId);
				ActionResult result = await completed;
				ProcessResult(result, results, ref completedCount, ref cachedCount, ref remoteCount, failedActions, actionLock);
				progress?.Report(new BuildProgress
				{
					CompletedActions = completedCount + skippedCount,
					TotalActions = totalActions,
					ActiveActions = runningTasks.Count,
					CurrentAction = result.Action,
					LastResult = result
				});
				continue;
			}
			foreach (BuildAction action in readyActions)
			{
				if (!ct.IsCancellationRequested)
				{
					action.Status = ActionStatus.Executing;
					progress?.Report(new BuildProgress
					{
						CompletedActions = completedCount + skippedCount,
						TotalActions = totalActions,
						ActiveActions = runningTasks.Count + 1,
						CurrentAction = action,
						LastResult = null
					});
					bool executeRemotely = useRemote && action.CanExecuteRemotely && action.Type == ActionType.Compile && remoteSemaphore.CurrentCount > 0;
					Task<ActionResult> task;
					if (executeRemotely && _options.UseDynamicExecution)
					{
						task = ExecuteWithRaceAsync(action, localSemaphore, remoteSemaphore, ct);
					}
					else
					{
						task = ((!executeRemotely) ? ExecuteLocalAsync(action, localSemaphore, ct) : ExecuteRemoteAsync(action, remoteSemaphore, ct));
					}
					runningTasks[action.Id] = task;
					continue;
				}
				break;
			}
		}
		ActionResult[] array = await Task.WhenAll(runningTasks.Values);
		for (int num = 0; num < array.Length; num++)
		{
			ProcessResult(array[num], results, ref completedCount, ref cachedCount, ref remoteCount, failedActions, actionLock);
		}
		stopwatch.Stop();
		int successCount = results.Count((ActionResult r) => r.Success);
		int failedCount = results.Count((ActionResult r) => !r.Success);
		return new BuildResult
		{
			Success = (failedCount == 0),
			TotalDuration = stopwatch.Elapsed,
			TotalActions = totalActions,
			SuccessfulActions = successCount,
			FailedActions = failedCount,
			SkippedActions = skippedCount,
			CachedActions = cachedCount,
			ActionResults = results,
			OutputFiles = graph.Actions.Where((BuildAction a) =>
			{
				ActionStatus status = a.Status;
				return (status == ActionStatus.Completed || status == ActionStatus.Cached) ? true : false;
			}).SelectMany((BuildAction a) => a.Outputs.Select((FileItem o) => o.Path)).ToList()
		};
	}

	private async Task<ActionResult> ExecuteWithRaceAsync(BuildAction action, SemaphoreSlim localSemaphore, SemaphoreSlim remoteSemaphore, CancellationToken ct)
	{
		using CancellationTokenSource localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using CancellationTokenSource remoteCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		Task<ActionResult> localTask = ExecuteLocalAsync(action, localSemaphore, localCts.Token);
		Task<ActionResult> remoteTask = ExecuteRemoteAsync(action, remoteSemaphore, remoteCts.Token);
		Task<ActionResult> completedTask = await Task.WhenAny(localTask, remoteTask);
		if (completedTask != localTask)
		{
			await localCts.CancelAsync();
		}
		else
		{
			await remoteCts.CancelAsync();
		}
		return await completedTask;
	}

	private async Task<ActionResult> ExecuteLocalAsync(BuildAction action, SemaphoreSlim semaphore, CancellationToken ct)
	{
		await semaphore.WaitAsync(ct);
		try
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			foreach (FileItem output in action.Outputs)
			{
				string dir = Path.GetDirectoryName(output.Path);
				if (!string.IsNullOrEmpty(dir))
				{
					Directory.CreateDirectory(dir);
				}
			}
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = (OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh"),
				Arguments = (OperatingSystem.IsWindows() ? ("/C \"" + action.CommandLine + "\"") : ("-c \"" + action.CommandLine.Replace("\"", "\\\"") + "\"")),
				WorkingDirectory = action.WorkingDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			foreach (var (key, value) in action.Environment)
			{
				startInfo.Environment[key] = value;
			}
			using Process process = new Process
			{
				StartInfo = startInfo
			};
			StringBuilder stdout = new StringBuilder();
			StringBuilder stderr = new StringBuilder();
			process.OutputDataReceived += (object _, DataReceivedEventArgs e) =>
			{
				if (e.Data != null)
				{
					stdout.AppendLine(e.Data);
				}
			};
			process.ErrorDataReceived += (object _, DataReceivedEventArgs e) =>
			{
				if (e.Data != null)
				{
					stderr.AppendLine(e.Data);
				}
			};
			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			await process.WaitForExitAsync(ct);
			stopwatch.Stop();
			return new ActionResult
			{
				Action = action,
				Success = (process.ExitCode == 0),
				Duration = stopwatch.Elapsed,
				StandardOutput = stdout.ToString(),
				StandardError = stderr.ToString(),
				ExitCode = process.ExitCode,
				WasCached = false,
				WasRemote = false
			};
		}
		finally
		{
			semaphore.Release();
		}
	}

	private async Task<ActionResult> ExecuteRemoteAsync(BuildAction action, SemaphoreSlim semaphore, CancellationToken ct)
	{
		await semaphore.WaitAsync(ct);
		try
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			OperationHandle handle = await _coordinator.ExecuteAsync(action, ct);
			RemoteActionResult remoteResult = await _coordinator.WaitForCompletionAsync(handle, ct);
			stopwatch.Stop();
			_ = remoteResult.Success;
			return new ActionResult
			{
				Action = action,
				Success = remoteResult.Success,
				Duration = stopwatch.Elapsed,
				StandardOutput = remoteResult.StandardOutput,
				StandardError = remoteResult.StandardError,
				ExitCode = remoteResult.ExitCode,
				WasCached = false,
				WasRemote = true,
				RemoteAgentId = remoteResult.Metadata?.WorkerId
			};
		}
		finally
		{
			semaphore.Release();
		}
	}

	private async Task<CoordinatorStatus?> TryGetCoordinatorStatusAsync(CancellationToken ct)
	{
		try
		{
			return await _coordinator.GetStatusAsync(ct);
		}
		catch
		{
			return null;
		}
	}

	private static void ProcessResult(ActionResult result, List<ActionResult> results, ref int completedCount, ref int cachedCount, ref int remoteCount, HashSet<string> failedActions, object lockObj)
	{
		lock (lockObj)
		{
			results.Add(result);
			if (result.Success)
			{
				result.Action.Status = (result.WasCached ? ActionStatus.Cached : ActionStatus.Completed);
				if (result.WasCached)
				{
					cachedCount++;
				}
				if (result.WasRemote)
				{
					remoteCount++;
				}
			}
			else
			{
				result.Action.Status = ActionStatus.Failed;
				failedActions.Add(result.Action.Id);
				MarkDependentsFailed(result.Action, failedActions);
			}
			completedCount++;
		}
	}

	private static void MarkDependentsFailed(BuildAction action, HashSet<string> failedActions)
	{
		foreach (BuildAction dependent in action.Dependents)
		{
			if (!failedActions.Contains(dependent.Id))
			{
				failedActions.Add(dependent.Id);
				dependent.Status = ActionStatus.Failed;
				MarkDependentsFailed(dependent, failedActions);
			}
		}
	}
}
