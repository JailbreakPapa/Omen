// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Diagnostics;
using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Executors;

/// <summary>
/// Executes build actions locally in parallel.
/// </summary>
public sealed class ParallelExecutor : IExecutor
{
    private readonly int _maxParallelism;
    private readonly IActionCache? _actionCache;
    
    public string Name => "Parallel Executor";
    public int MaxParallelism => _maxParallelism;
    
    public ParallelExecutor(int? maxParallelism = null, IActionCache? actionCache = null)
    {
        _maxParallelism = maxParallelism ?? Environment.ProcessorCount;
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
        var skippedCount = 0;
        
        // Mark up-to-date actions as skipped
        skippedCount = graph.MarkUpToDateActionsAsSkipped();
        
        // Compute priorities for scheduling
        graph.ComputePriorities();
        
        var totalActions = graph.Actions.Count;
        var semaphore = new SemaphoreSlim(_maxParallelism);
        var runningTasks = new List<Task>();
        var failedActions = new HashSet<string>();
        var actionLock = new object();
        
        while (!graph.IsComplete && !ct.IsCancellationRequested)
        {
            var readyActions = graph.GetReadyActions()
                .Where(a => !failedActions.Contains(a.Id))
                .OrderBy(a => a.Priority)
                .ToList();
            
            if (readyActions.Count == 0 && runningTasks.Count > 0)
            {
                // Wait for any running task to complete
                await Task.WhenAny(runningTasks);
                runningTasks.RemoveAll(t => t.IsCompleted);
                continue;
            }
            
            if (readyActions.Count == 0)
                break;
            
            foreach (var action in readyActions)
            {
                if (ct.IsCancellationRequested)
                    break;
                
                await semaphore.WaitAsync(ct);
                
                action.Status = ActionStatus.Executing;
                
                var task = Task.Run(async () =>
                {
                    try
                    {
                        var result = await ExecuteActionAsync(action, ct);
                        
                        lock (actionLock)
                        {
                            results.Add(result);
                            
                            if (result.Success)
                            {
                                action.Status = result.WasCached ? ActionStatus.Cached : ActionStatus.Completed;
                                if (result.WasCached)
                                    Interlocked.Increment(ref cachedCount);
                            }
                            else
                            {
                                action.Status = ActionStatus.Failed;
                                failedActions.Add(action.Id);
                                
                                // Mark all dependents as failed too
                                MarkDependentsFailed(action, failedActions);
                            }
                            
                            Interlocked.Increment(ref completedCount);
                        }
                        
                        progress?.Report(new BuildProgress
                        {
                            CompletedActions = completedCount + skippedCount,
                            TotalActions = totalActions,
                            ActiveActions = _maxParallelism - semaphore.CurrentCount,
                            CurrentAction = action,
                            LastResult = result
                        });
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct);
                
                runningTasks.Add(task);
            }
        }
        
        // Wait for all remaining tasks
        await Task.WhenAll(runningTasks);
        
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
                .Where(a => a.Status == ActionStatus.Completed || a.Status == ActionStatus.Cached)
                .SelectMany(a => a.Outputs.Select(o => o.Path))
                .ToList()
        };
    }
    
    private async Task<ActionResult> ExecuteActionAsync(BuildAction action, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // Check cache
        if (_actionCache != null && action.ActionDigest.HasValue)
        {
            var cached = await _actionCache.GetAsync(action.ActionDigest.Value, ct);
            if (cached != null)
            {
                // Restore outputs from cache
                // (In a full implementation, we'd download from CAS)
                
                stopwatch.Stop();
                return new ActionResult
                {
                    Action = action,
                    Success = cached.Success,
                    Duration = stopwatch.Elapsed,
                    StandardOutput = cached.StandardOutput,
                    StandardError = cached.StandardError,
                    ExitCode = cached.ExitCode,
                    WasCached = true
                };
            }
        }
        
        // Ensure output directories exist
        foreach (var output in action.Outputs)
        {
            var dir = Path.GetDirectoryName(output.Path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        
        // Execute the command
        var startInfo = new ProcessStartInfo
        {
            FileName = GetShellExecutable(),
            Arguments = GetShellArguments(action.CommandLine),
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
        
        var success = process.ExitCode == 0;
        
        // Cache successful results
        if (success && _actionCache != null && action.ActionDigest.HasValue)
        {
            await _actionCache.StoreAsync(action.ActionDigest.Value, new CachedActionResult
            {
                Success = true,
                OutputDigests = [],
                OutputPaths = action.Outputs.Select(o => o.Path).ToList(),
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
                ExitCode = process.ExitCode
            }, ct);
        }
        
        return new ActionResult
        {
            Action = action,
            Success = success,
            Duration = stopwatch.Elapsed,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            ExitCode = process.ExitCode,
            WasCached = false
        };
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
    
    private static string GetShellExecutable()
    {
        return OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
    }
    
    private static string GetShellArguments(string commandLine)
    {
        return OperatingSystem.IsWindows() 
            ? $"/C \"{commandLine}\"" 
            : $"-c \"{commandLine.Replace("\"", "\\\"")}\"";
    }
}
