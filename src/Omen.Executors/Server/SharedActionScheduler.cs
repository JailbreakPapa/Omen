// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Diagnostics;
using Omen.Core.Graph;
using Omen.Core.Implementations;
using Omen.Core.Interfaces;

namespace Omen.Executors.Server;

public enum RequestClass { Interactive, Background }

/// <param name="Started">When the request arrived; jobs finished after it are reused. Null: when its graph starts executing.</param>
public sealed record ScheduleRequest(string Id, RequestClass Class, int Cap, DateTime? Started = null);

/// <summary>
/// One slot pool shared by every build the server runs. Each request walks its own graph the way ParallelExecutor
/// does, but instead of running an action it enlists in a job keyed by the action's primary output: a job with the
/// same digest that is queued, running, or finished since this request started is joined rather than rerun; one with
/// a different digest runs first and this one after it. Queued jobs start interactive-first (background keeps the
/// last free slot once there are four), then by critical-path priority, and never past a request's own cap.
/// </summary>
public sealed class SharedActionScheduler
{
    private readonly Func<BuildAction, CancellationToken, Task<ActionResult>> _run;
    private readonly Sha256DigestCalculator _digests = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, Job> _byOutput = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Job> _queue = [];
    private readonly Dictionary<string, int> _runningPerRequest = new(StringComparer.Ordinal);
    private int _running;
    private int _runningBackground;

    public SharedActionScheduler(int slots, Func<BuildAction, CancellationToken, Task<ActionResult>> run)
    {
        Slots = Math.Max(1, slots);
        _run = run;
    }

    public int Slots { get; }

    /// <summary>Slots running an action right now.</summary>
    public int Running
    {
        get { lock (_lock) return _running; }
    }

    public IExecutor For(ScheduleRequest request, Func<BuildAction, bool> isUpToDate) => new RequestExecutor(this, request, isUpToDate);

    public IReadOnlyList<(string RequestId, int Running, int Queued)> Snapshot()
    {
        lock (_lock)
        {
            var queued = _queue.SelectMany(j => j.Waiters).GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.Count());
            return queued.Keys.Concat(_runningPerRequest.Where(kv => kv.Value > 0).Select(kv => kv.Key)).Distinct()
                .Select(id => (id, _runningPerRequest.GetValueOrDefault(id), queued.GetValueOrDefault(id)))
                .ToList();
        }
    }

    private sealed class Job(BuildAction action, string digest, Job? after)
    {
        public BuildAction Action { get; } = action;
        public string Digest { get; } = digest;
        public Job? After { get; } = after;
        public List<ScheduleRequest> Waiters { get; } = [];
        public string StartedBy { get; set; } = "";
        public DateTime? FinishedAt { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource<ActionResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RequestClass Class => Waiters.Count == 0 ? RequestClass.Background : Waiters.Min(w => w.Class);
        public ScheduleRequest? Owner => Waiters.OrderBy(w => w.Class).FirstOrDefault();
    }

    private Job Enlist(BuildAction action, ScheduleRequest request, DateTime requestStarted)
    {
        var key = action.Outputs.Count > 0 ? Path.GetFullPath(action.Outputs[0].Path) : action.Id;
        var digest = action.ComputeDigest(_digests).ToString();
        lock (_lock)
        {
            if (_byOutput.TryGetValue(key, out var existing) && existing.Digest == digest &&
                (!existing.Result.Task.IsCompleted || existing.FinishedAt >= requestStarted && existing.Result.Task.IsCompletedSuccessfully))
            {
                action.SharedWith = existing.StartedBy;
                if (!existing.Result.Task.IsCompleted)
                {
                    existing.Waiters.Add(request);
                    Dispatch();
                }
                return existing;
            }

            var job = new Job(action, digest, existing is { Result.Task.IsCompleted: false } ? existing : null) { StartedBy = request.Id };
            job.Waiters.Add(request);
            _byOutput[key] = job;
            _queue.Add(job);
            Dispatch();
            return job;
        }
    }

    /// <summary>A request stopped waiting (cancelled). A job nobody waits for any more is dropped, or killed if running.</summary>
    private void Withdraw(Job job, ScheduleRequest request)
    {
        lock (_lock)
        {
            if (!job.Waiters.Remove(request) || job.Waiters.Count > 0)
                return;
            if (_queue.Remove(job))
                job.Result.TrySetCanceled();
            else
                job.Cancel.Cancel();
        }
    }

    /// <summary>Starts queued jobs while slots are free. Caller holds _lock.</summary>
    private void Dispatch()
    {
        while (_running < Slots)
        {
            var startable = _queue.Where(Startable).ToList();
            if (startable.Count == 0)
                return;
            // The last free slot goes to waiting background work when none of it is running: agents keep moving
            // during a long interactive build, without preempting anything.
            if (Slots >= 4 && _runningBackground == 0 && _running == Slots - 1 && startable.Any(j => j.Class == RequestClass.Background))
                startable = startable.Where(j => j.Class == RequestClass.Background).ToList();
            Start(startable.OrderBy(j => j.Class).ThenBy(j => j.Action.Priority).First());
        }
    }

    private bool Startable(Job job) =>
        (job.After == null || job.After.Result.Task.IsCompleted) &&
        job.Owner is { } owner && _runningPerRequest.GetValueOrDefault(owner.Id) < owner.Cap;

    private void Start(Job job)
    {
        _queue.Remove(job);
        var owner = job.Owner!.Id;
        var background = job.Class == RequestClass.Background;
        _running++;
        if (background) _runningBackground++;
        _runningPerRequest[owner] = _runningPerRequest.GetValueOrDefault(owner) + 1;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _run(job.Action, job.Cancel.Token);
                lock (_lock) job.FinishedAt = DateTime.UtcNow;
                job.Result.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                job.Result.TrySetCanceled();
            }
            catch (Exception exception)
            {
                lock (_lock) job.FinishedAt = DateTime.UtcNow;
                job.Result.TrySetResult(new ActionResult { Action = job.Action, Success = false, Duration = TimeSpan.Zero, StandardError = exception.ToString(), ExitCode = -1 });
            }
            finally
            {
                lock (_lock)
                {
                    _running--;
                    if (background) _runningBackground--;
                    _runningPerRequest[owner]--;
                    Dispatch();
                }
            }
        });
    }

    private sealed class RequestExecutor(SharedActionScheduler scheduler, ScheduleRequest request, Func<BuildAction, bool> isUpToDate) : IExecutor
    {
        public string Name => "Shared scheduler";
        public int MaxParallelism => request.Cap;

        public async Task<BuildResult> ExecuteAsync(ActionGraph graph, IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
        {
            var started = request.Started ?? DateTime.UtcNow;
            var clock = Stopwatch.StartNew();
            graph.ComputePriorities();
            var results = new List<ActionResult>();
            var inFlight = new Dictionary<Task<ActionResult>, (BuildAction Action, Job Job)>();
            var skipped = graph.Actions.Count(a => a.Status == ActionStatus.Skipped);
            var failed = new HashSet<string>();
            try
            {
                while (!graph.IsComplete)
                {
                    ct.ThrowIfCancellationRequested();
                    var skippedThisPass = false;
                    foreach (var action in graph.GetReadyActions().Where(a => !failed.Contains(a.Id)).OrderBy(a => a.Priority).ToList())
                    {
                        if (isUpToDate(action))
                        {
                            action.Status = ActionStatus.Skipped;
                            skipped++;
                            skippedThisPass = true;
                            continue;
                        }
                        action.Status = ActionStatus.Executing;
                        var job = scheduler.Enlist(action, request, started);
                        inFlight[job.Result.Task] = (action, job);
                        progress?.Report(new BuildProgress { CompletedActions = results.Count + skipped, TotalActions = graph.Actions.Count, ActiveActions = inFlight.Count, CurrentAction = action, LastResult = null });
                    }
                    // A skip can make its dependents ready; look again before deciding nothing can run.
                    if (skippedThisPass && inFlight.Count == 0)
                        continue;
                    if (inFlight.Count == 0)
                    {
                        var stalled = graph.Actions.Where(a => a.Status is ActionStatus.Pending or ActionStatus.Executing && !failed.Contains(a.Id)).ToList();
                        if (stalled.Count > 0)
                        {
                            var detail = string.Join("; ", stalled.Take(5).Select(a =>
                                $"'{a.Description}' [{a.Status}] waits on {string.Join(", ", a.Dependencies.Select(d => $"'{d.Description}' [{d.Status}]"))}"));
                            throw new InvalidOperationException($"Build stalled: {stalled.Count} action(s) can never run: {detail}");
                        }
                        break;
                    }

                    var done = await Task.WhenAny(inFlight.Keys).WaitAsync(ct);
                    var (finishedAction, _) = inFlight[done];
                    inFlight.Remove(done);
                    // The job's result carries the BuildAction of whichever request started it; report this request's own.
                    var shared = await done;
                    var result = new ActionResult
                    {
                        Action = finishedAction, Success = shared.Success, Duration = shared.Duration,
                        StandardOutput = shared.StandardOutput, StandardError = shared.StandardError, ExitCode = shared.ExitCode,
                        WasCached = shared.WasCached, WasRemote = shared.WasRemote, RemoteAgentId = shared.RemoteAgentId,
                    };
                    results.Add(result);
                    finishedAction.Status = result.Success ? ActionStatus.Completed : ActionStatus.Failed;
                    if (!result.Success)
                        MarkDependentsFailed(finishedAction, failed);
                    progress?.Report(new BuildProgress { CompletedActions = results.Count + skipped, TotalActions = graph.Actions.Count, ActiveActions = inFlight.Count, CurrentAction = finishedAction, LastResult = result });
                }
            }
            catch (OperationCanceledException)
            {
                foreach (var (_, job) in inFlight.Values)
                    scheduler.Withdraw(job, request);
                throw;
            }

            return new BuildResult
            {
                Success = failed.Count == 0 && results.All(r => r.Success),
                TotalDuration = clock.Elapsed,
                TotalActions = graph.Actions.Count,
                SuccessfulActions = results.Count(r => r.Success),
                FailedActions = results.Count(r => !r.Success),
                SkippedActions = skipped,
                CachedActions = results.Count(r => r.WasCached),
                ActionResults = results,
                OutputFiles = graph.Actions.Where(a => a.Status == ActionStatus.Completed).SelectMany(a => a.Outputs.Select(o => o.Path)).ToList(),
            };
        }

        private static void MarkDependentsFailed(BuildAction action, HashSet<string> failed)
        {
            foreach (var dependent in action.Dependents)
            {
                if (failed.Add(dependent.Id))
                {
                    dependent.Status = ActionStatus.Failed;
                    MarkDependentsFailed(dependent, failed);
                }
            }
        }
    }
}
