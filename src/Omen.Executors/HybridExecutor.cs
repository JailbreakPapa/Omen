// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Diagnostics;
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
    
    public HybridExecutor(
        IOmenCoordinator coordinator,
        HybridExecutorOptions? options = null,
        IActionCache? actionCache = null)
    {
        _coordinator = coordinator;
        _options = options ?? new HybridExecutorOptions();
        _localParallelism = _options.LocalParallelism ?? Environment.ProcessorCount / 2;
        _actionCache = actionCache;
    }
    
    public async Task<BuildResult> ExecuteAsync(
        ActionGraph graph,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new List<ActionResult>();
        var completedCount = 0;
        var cachedCount = 0;
        var remoteCount = 0;
        var skippedCount = graph.MarkUpToDateActionsAsSkipped();
        
        graph.ComputePriorities();
        
        var totalActions = graph.Actions.Count;
        var localSemaphore = new SemaphoreSlim(_localParallelism);
        var remoteSemaphore = new SemaphoreSlim(_options.MaxRemoteActions);
        var runningTasks = new Dictionary<string, Task<ActionResult>>();
        var failedActions = new HashSet<string>();
        var actionLock = new object();
        
        // Check coordinator availability
        var coordinatorStatus = await TryGetCoordinatorStatusAsync(ct);
        var useRemote = coordinatorStatus != null && coordinatorStatus.ActiveAgents > 0;
        
        while (!graph.IsComplete && !ct.IsCancellationRequested)
        {
            var readyActions = graph.GetReadyActions()
                .Where(a => !failedActions.Contains(a.Id) && !runningTasks.ContainsKey(a.Id))
                .OrderBy(a => a.Priority)
                .ToList();
            
            if (readyActions.Count == 0)
            {
                if (runningTasks.Count > 0)
                {
                    var completed = await Task.WhenAny(runningTasks.Values);
                    
                    // Find and process the completed task
                    var completedId = runningTasks.First(kvp => kvp.Value == completed).Key;
                    runningTasks.Remove(completedId);
                    
                    var result = await completed;
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
                
                break;
            }
            
            foreach (var action in readyActions)
            {
                if (ct.IsCancellationRequested)
                    break;
                
                action.Status = ActionStatus.Executing;
                
                // Decide: local or remote?
                var executeRemotely = useRemote && 
                                      action.CanExecuteRemotely && 
                                      action.Type == ActionType.Compile && // Compilation is best for remote
                                      remoteSemaphore.CurrentCount > 0;
                
                Task<ActionResult> task;
                
                if (executeRemotely && _options.UseDynamicExecution)
                {
                    // Race local vs remote
                    task = ExecuteWithRaceAsync(action, localSemaphore, remoteSemaphore, ct);
                }
                else if (executeRemotely)
                {
                    // Remote only
                    task = ExecuteRemoteAsync(action, remoteSemaphore, ct);
                }
                else
                {
                    // Local only
                    task = ExecuteLocalAsync(action, localSemaphore, ct);
                }
                
                runningTasks[action.Id] = task;
            }
        }
        
        // Wait for all remaining tasks
        var remainingResults = await Task.WhenAll(runningTasks.Values);
        foreach (var result in remainingResults)
        {
            ProcessResult(result, results, ref completedCount, ref cachedCount, ref remoteCount, failedActions, actionLock);
        }
        
        stopwatch.Stop();
        
        var successCount = results.Count(r => r.Success);
        var failedCount = results.Count(r => !r.Success);
        
        return new BuildResult
        {
            Success = failedCount == 0,
            TotalDuration = stopwatch.Elapsed,
            TotalActions = totalActions,
            SuccessfulActions = successCount,
            FailedActions = failedCount,
            SkippedActions = skippedCount,
            CachedActions = cachedCount,
            ActionResults = results,
            OutputFiles = graph.Actions
                .Where(a => a.Status is ActionStatus.Completed or ActionStatus.Cached)
                .SelectMany(a => a.Outputs.Select(o => o.Path))
                .ToList()
        };
    }
    
    private async Task<ActionResult> ExecuteWithRaceAsync(
        BuildAction action,
        SemaphoreSlim localSemaphore,
        SemaphoreSlim remoteSemaphore,
        CancellationToken ct)
    {
        using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var remoteCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        var localTask = ExecuteLocalAsync(action, localSemaphore, localCts.Token);
        var remoteTask = ExecuteRemoteAsync(action, remoteSemaphore, remoteCts.Token);
        
        var completedTask = await Task.WhenAny(localTask, remoteTask);
        
        // Cancel the slower execution
        if (completedTask == localTask)
        {
            await remoteCts.CancelAsync();
        }
        else
        {
            await localCts.CancelAsync();
        }
        
        return await completedTask;
    }
    
    private async Task<ActionResult> ExecuteLocalAsync(
        BuildAction action,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        
        try
        {
            var stopwatch = Stopwatch.StartNew();
            
            // Ensure output directories exist
            foreach (var output in action.Outputs)
            {
                var dir = Path.GetDirectoryName(output.Path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() 
                    ? $"/C \"{action.CommandLine}\"" 
                    : $"-c \"{action.CommandLine.Replace("\"", "\\\"")}\"",
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
            
            using var process = new Process { StartInfo = startInfo };
            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();
            
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            
            await process.WaitForExitAsync(ct);
            stopwatch.Stop();
            
            return new ActionResult
            {
                Action = action,
                Success = process.ExitCode == 0,
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
    
    private async Task<ActionResult> ExecuteRemoteAsync(
        BuildAction action,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        
        try
        {
            var stopwatch = Stopwatch.StartNew();
            
            // Submit to coordinator
            var handle = await _coordinator.ExecuteAsync(action, ct);
            
            // Wait for completion
            var remoteResult = await _coordinator.WaitForCompletionAsync(handle, ct);
            
            stopwatch.Stop();
            
            // Download outputs
            if (remoteResult.Success)
            {
                // In a full implementation, we'd download outputs from CAS here
            }
            
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
    
    private static void ProcessResult(
        ActionResult result,
        List<ActionResult> results,
        ref int completedCount,
        ref int cachedCount,
        ref int remoteCount,
        HashSet<string> failedActions,
        object lockObj)
    {
        lock (lockObj)
        {
            results.Add(result);
            
            if (result.Success)
            {
                result.Action.Status = result.WasCached ? ActionStatus.Cached : ActionStatus.Completed;
                if (result.WasCached) cachedCount++;
                if (result.WasRemote) remoteCount++;
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
        foreach (var dependent in action.Dependents)
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

/// <summary>
/// Options for the hybrid executor.
/// </summary>
public sealed class HybridExecutorOptions
{
    /// <summary>
    /// Maximum local parallel actions. Defaults to half of processor count.
    /// </summary>
    public int? LocalParallelism { get; init; }
    
    /// <summary>
    /// Maximum concurrent remote actions.
    /// </summary>
    public int MaxRemoteActions { get; init; } = 100;
    
    /// <summary>
    /// Whether to race local vs remote execution.
    /// </summary>
    public bool UseDynamicExecution { get; init; } = false;
    
    /// <summary>
    /// Prefer remote execution for compilation.
    /// </summary>
    public bool PreferRemoteForCompilation { get; init; } = true;
}
