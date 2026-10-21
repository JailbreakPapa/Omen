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
    private readonly Func<BuildAction, bool>? _isUpToDate;
    
    public string Name => "Parallel Executor";
    public int MaxParallelism => _maxParallelism;
    
    public ParallelExecutor(int? maxParallelism = null, IActionCache? actionCache = null, Func<BuildAction, bool>? isUpToDate = null)
    {
        _maxParallelism = maxParallelism ?? Environment.ProcessorCount;
        _actionCache = actionCache;
        _isUpToDate = isUpToDate;
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
        
        // BuildOrchestrator already runs a digest-aware up-to-date pass (which sees
        // command-line changes, e.g. from a toggled build option) before invoking this
        // executor. Re-running the timestamp-only overload here would re-evaluate those
        // same still-Pending actions by file mtimes alone - which can't see a command-line
        // change - and wrongly flip them back to Skipped, silently discarding a real
        // rebuild. Just count what the caller already decided.
        skippedCount = graph.Actions.Count(a => a.Status == ActionStatus.Skipped);
        
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
            {
                // Nothing runnable and nothing running, with work still pending: the graph is
                // stalled, not finished. Breaking out quietly reported SUCCESS for a build that
                // executed nothing - 877 actions, 0 compiled, no artifacts, exit 0 - which is
                // worse than any failure, because it looks like it worked.
                var stalled = graph.Actions.Where(a => a.Status == ActionStatus.Pending).ToList();
                if (stalled.Count > 0)
                {
                    var example = stalled[0];
                    var blocking = example.Dependencies
                        .Where(d => d.Status is not (ActionStatus.Completed or ActionStatus.Cached or ActionStatus.Skipped))
                        .Select(d => $"{d.Id} [{d.Status}]")
                        .Take(3)
                        .ToList();

                    throw new InvalidOperationException(
                        $"Build stalled: {stalled.Count} of {totalActions} action(s) could never run because " +
                        $"their dependencies never completed. For example '{example.Id}' " +
                        $"({example.Description ?? example.Type.ToString()}) is waiting on " +
                        (blocking.Count > 0 ? string.Join(", ", blocking) : "a dependency not present in the graph") +
                        ".");
                }

                break;
            }
            
            foreach (var action in readyActions)
            {
                if (ct.IsCancellationRequested)
                    break;
                
                // The orchestrator's pre-pass cannot judge an action while a dependency is still
                // pending (that dependency's outputs weren't final), so one stale action near the
                // roots left every dependent unjudged and dragged it through the cache. Judge it
                // here, where its dependencies are done.
                if (_isUpToDate?.Invoke(action) == true)
                {
                    action.Status = ActionStatus.Skipped;
                    skippedCount++;
                    continue;
                }

                await semaphore.WaitAsync(ct);

                action.Status = ActionStatus.Executing;

                // A start report (LastResult null) is what lets a watcher show the actions running
                // now; without it a link parked on a locked DLL is invisible until it fails.
                progress?.Report(new BuildProgress
                {
                    CompletedActions = completedCount + skippedCount,
                    TotalActions = totalActions,
                    ActiveActions = _maxParallelism - semaphore.CurrentCount,
                    CurrentAction = action,
                    LastResult = null
                });

                var task = Task.Run(async () =>
                {
                    try
                    {
                        var result = await RunProcessAsync(action, _actionCache, ct);
                        
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
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Without this the exception escaped into the Task, which nothing ever
                        // observed: RemoveAll(t => t.IsCompleted) treats Faulted as completed, so
                        // the action stayed Executing forever, every dependent stalled, and the
                        // reason was lost entirely. A throwing action is a failed action.
                        lock (actionLock)
                        {
                            results.Add(new ActionResult
                            {
                                Action = action,
                                Success = false,
                                Duration = TimeSpan.Zero,
                                StandardError = exception.ToString(),
                                ExitCode = -1
                            });

                            action.Status = ActionStatus.Failed;
                            failedActions.Add(action.Id);
                            MarkDependentsFailed(action, failedActions);

                            Interlocked.Increment(ref completedCount);
                        }
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
    
    /// <summary>Runs one action's command line (or restores it from <paramref name="cache"/>); shared with the build server's scheduler.</summary>
    public static async Task<ActionResult> RunProcessAsync(BuildAction action, IActionCache? cache, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // Check cache
        if (cache != null && action.ActionDigest.HasValue)
        {
            var cached = await cache.GetAsync(action.ActionDigest.Value, ct);
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
        using var job = ProcessJob.For(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelling the wait is not cancelling the work: the compiler (a grandchild, under cmd.exe) would keep
            // writing its outputs, and under the build server nothing else ever stops it. Kill everything the action
            // started and wait for it, so a cancelled action's slot is only free once nothing is writing its outputs.
            if (job != null)
            {
                job.KillAll();
            }
            else
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        stopwatch.Stop();
        
        var success = process.ExitCode == 0;
        
        // Cache successful results
        if (success && cache != null && action.ActionDigest.HasValue)
        {
            await cache.StoreAsync(action.ActionDigest.Value, new CachedActionResult
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
