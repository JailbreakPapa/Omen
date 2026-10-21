// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.IO.Pipes;
using Omen.Core.Configuration;
using Omen.Core.Diagnostics;
using Omen.Core.Graph;
using Omen.Core.Interfaces;
using Omen.Executors.Orchestration;

namespace Omen.Executors.Server;

/// <summary>
/// The headless <c>omen server</c>: one per project root and configuration. Owns that configuration's intermediate
/// directories for its whole life, keeps <see cref="BuildWarmState"/> current from a file watcher, and serves any
/// number of concurrent <c>omen build</c> clients through one <see cref="SharedActionScheduler"/>.
/// </summary>
public sealed class BuildServer(
    string projectRoot, string configuration, string version, int slots, TimeSpan idle,
    Func<BuildAction, CancellationToken, Task<ActionResult>>? run = null)
{
    private readonly string _root = Path.GetFullPath(projectRoot).TrimEnd('\\', '/');
    private readonly BuildWarmState _warm = new();
    private readonly SharedActionScheduler _scheduler = new(slots, run ?? ((action, ct) => ParallelExecutor.RunProcessAsync(action, null, ct)));
    private readonly object _applyLock = new();
    private int _active;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    /// <summary>Returns when idle for <c>idle</c>, on "stop", when a client of another version asks, or on <paramref name="ct"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var serverLock = BuildOrchestrator.TryAcquireServerLock(_root, configuration);
        if (serverLock == null)
            return; // another server owns this root and configuration; clients connect to it

        _warm.WatchedRoot = _root;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watcher = StartWatcher();
        var name = ServerProtocol.PipeName(_root, configuration);
        var clients = new List<Task>();
        var idleLoop = IdleLoopAsync(stop);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                    break;
                }
                Touch();
                lock (clients)
                {
                    clients.RemoveAll(t => t.IsCompleted);
                    clients.Add(ServeAsync(pipe, stop));
                }
            }
        }
        finally
        {
            stop.Cancel();
            Task[] pending;
            lock (clients) pending = [.. clients, idleLoop];
            await Task.WhenAll(pending.Select(t => t.ContinueWith(_ => { })));
            _warm.DropAll(); // saves the digest stores
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    private FileSystemWatcher StartWatcher()
    {
        var watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, e) => _warm.Enqueue(new FileChange(e.FullPath, FileChangeKind.Changed));
        watcher.Created += (_, e) => _warm.Enqueue(new FileChange(e.FullPath, FileChangeKind.Created));
        watcher.Deleted += (_, e) => _warm.Enqueue(new FileChange(e.FullPath, FileChangeKind.Deleted));
        watcher.Renamed += (_, e) =>
        {
            _warm.Enqueue(new FileChange(e.OldFullPath, FileChangeKind.Renamed));
            _warm.Enqueue(new FileChange(e.FullPath, FileChangeKind.Created));
        };
        watcher.Error += (_, _) => _warm.EnqueueOverflow();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private async Task IdleLoopAsync(CancellationTokenSource stop)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(idle.TotalMilliseconds / 4, 50, 1000));
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(tick, stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (Volatile.Read(ref _active) == 0 && DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks) >= idle.Ticks)
                stop.Cancel();
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationTokenSource stop)
    {
        await using var _ = pipe;
        try
        {
            // Both of the client's messages are read before anything is written: a pipe write blocks until the other
            // side reads, and a client still writing its request would never read a reply.
            var hello = await ServerProtocol.ReadAsync(pipe, stop.Token);
            var message = await ServerProtocol.ReadAsync(pipe, stop.Token);
            if (hello?.Kind != "hello")
                return;

            switch (message?.Kind)
            {
                case "stop":
                    await ServerProtocol.WriteAsync(pipe, new ServerMessage { Kind = "status", Text = "Omen server stopping." }, default);
                    stop.Cancel();
                    return;
                case "status":
                    var status = Status();
                    await ServerProtocol.WriteAsync(pipe, new ServerMessage { Kind = "status", Text = Describe(status, _warm.RuleCompilations), Status = status }, default);
                    return;
                case "cancel" when message.Text is { } id && _builds.TryGetValue(id, out var build):
                    build.CancelledByControl = true;
                    build.Cancel.Cancel();
                    await ServerProtocol.WriteAsync(pipe, new ServerMessage { Kind = "status", Text = $"Cancelling {id}." }, default);
                    return;
                case "request" when message.Request is { } request:
                    if (hello.Version != version)
                    {
                        // Finish what is running, accept nothing new; the client starts a server of its own version.
                        await ServerProtocol.WriteAsync(pipe, new ServerMessage { Kind = "restarting", Text = $"Omen server {version} is restarting for client {hello.Version}" }, default);
                        stop.Cancel();
                        return;
                    }
                    await BuildAsync(pipe, request);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The client went away.
        }
    }

    /// <summary>A running build, as <c>status</c> reports it and <c>cancel</c> finds it.</summary>
    private sealed class RunningBuild(string id, BuildRequestDto request, DateTime started)
    {
        public string Id { get; } = id;
        public BuildRequestDto Request { get; } = request;
        public DateTime Started { get; } = started;
        public CancellationTokenSource Cancel { get; } = new();
        public volatile bool CancelledByControl;
        public volatile int Completed;
        public volatile int Total;
        public volatile string? Current;
        public volatile string? Summary;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunningBuild> _builds = new(StringComparer.Ordinal);

    private ServerStatusDto Status()
    {
        var idleLeft = Volatile.Read(ref _active) > 0
            ? idle
            : idle - TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks));
        var builds = _builds.Values.OrderBy(b => b.Started).Select(b => new BuildStatusDto(
            b.Id, b.Request.TargetFile, b.Request.Background, b.Started, b.Completed, b.Total, b.Current, b.Summary)).ToList();
        return new ServerStatusDto(version, _root, configuration, _scheduler.Slots, _scheduler.Running, _warm.HasRules,
            (int)Math.Max(0, idleLeft.TotalSeconds), builds) { LastFinished = _lastFinished };
    }

    private volatile LastBuildDto? _lastFinished;

    private static string Describe(ServerStatusDto status, int ruleCompilations) =>
        $"Omen server {status.Version}: {status.Root} ({status.Configuration}), {status.SlotsInUse}/{status.Slots} slots in use, rules {(status.RulesWarm ? "warm" : "cold")} (compiled {ruleCompilations}x)"
        + string.Concat(status.Builds.Select(b => $"{Environment.NewLine}  {b.Id} {TrayModel.TargetName(b.Target)}{(b.Background ? " [background]" : "")}: {b.Completed}/{b.Total}"));

    private async Task BuildAsync(Stream pipe, BuildRequestDto dto)
    {
        var arrived = DateTime.UtcNow;
        Interlocked.Increment(ref _active);
        var running = new RunningBuild(Guid.NewGuid().ToString("N")[..8], dto, arrived);
        _builds[running.Id] = running;
        using var clientGone = new CancellationTokenSource();
        using var buildCancel = CancellationTokenSource.CreateLinkedTokenSource(clientGone.Token, running.Cancel.Token);
        var writeLock = new SemaphoreSlim(1, 1);

        void Send(ServerMessage message)
        {
            if (clientGone.IsCancellationRequested)
                return;
            writeLock.Wait();
            try
            {
                ServerProtocol.WriteAsync(pipe, message, default).GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                clientGone.Cancel();
            }
            finally
            {
                writeLock.Release();
            }
        }

        // The client says nothing after its request; the read ending means it disconnected (Ctrl+C, VS Cancel).
        var watchClient = Task.Run(async () =>
        {
            if (await ServerProtocol.ReadAsync(pipe, clientGone.Token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null) == null)
                clientGone.Cancel();
        });

        try
        {
            // Watcher changes take effect between builds' starts only; a running build keeps what it started with.
            lock (_applyLock)
            {
                _warm.ApplyPendingChanges();
                _warm.ApplyEnvironment(BuildWarmState.PersistedEnvironment());
            }

            var schedule = new ScheduleRequest(running.Id, dto.Background ? RequestClass.Background : RequestClass.Interactive, dto.Jobs ?? _scheduler.Slots, arrived);
            var services = new BuildServices
            {
                Warm = _warm,
                CallerHoldsIntermediateLock = true,
                ExecutorFactory = (_, isUpToDate) => _scheduler.For(schedule, isUpToDate),
            };
            var events = new SynchronousProgress<OrchestratorEvent>(e =>
            {
                if (e.Message.Contains(" to run: "))
                    running.Summary = e.Message.Split('\n')[0].Trim();
                Send(new ServerMessage { Kind = "event", Level = e.Level.ToString(), Text = e.Message });
            });
            var progress = new SynchronousProgress<BuildProgress>(p =>
            {
                running.Completed = p.CompletedActions;
                running.Total = p.TotalActions;
                running.Current = p.CurrentAction?.Description;
                Send(new ServerMessage
                {
                    Kind = "progress",
                    Progress = new ProgressDto(p.CompletedActions, p.TotalActions, p.ActiveActions, (p.LastResult?.Action ?? p.CurrentAction)?.Description,
                        p.LastResult != null, p.LastResult?.Success ?? false, p.LastResult?.WasCached ?? false, p.LastResult?.Duration.TotalSeconds ?? 0),
                });
            });

            var result = await new BuildOrchestrator(services).BuildAsync(new BuildOrchestratorRequest
            {
                TargetFile = dto.TargetFile,
                Platform = dto.Platform is null ? null : Enum.Parse<TargetPlatform>(dto.Platform, ignoreCase: true),
                Architecture = dto.Architecture is null ? null : Enum.Parse<TargetArchitecture>(dto.Architecture, ignoreCase: true),
                Configuration = dto.Configuration is null ? null : Enum.Parse<BuildConfiguration>(dto.Configuration, ignoreCase: true),
                Jobs = dto.Jobs,
                CoordinatorAddress = dto.Coordinator,
            }, events, progress, buildCancel.Token);

            _lastFinished = new LastBuildDto(dto.TargetFile, result?.Success == true, DateTime.UtcNow);
            Send(new ServerMessage
            {
                Kind = "result",
                Result = result is null ? null : new ResultDto(result.Success, result.TotalActions, result.SuccessfulActions, result.FailedActions, result.SkippedActions, result.CachedActions, result.TotalDuration.TotalSeconds),
            });
        }
        catch (OperationCanceledException)
        {
            // The client left (nothing to tell it) or someone cancelled the build from the tray; either way the
            // scheduler withdrew its unshared work.
            if (running.CancelledByControl)
            {
                Send(new ServerMessage { Kind = "event", Level = nameof(OrchestratorEventLevel.Error), Text = "Build cancelled from the Omen tray." });
                Send(new ServerMessage { Kind = "result" });
            }
        }
        catch (Exception exception)
        {
            // In-process this would have reached the CLI's exception handler; say it to the client instead of
            // dropping the connection, which the client would read as a dead server.
            Send(new ServerMessage { Kind = "event", Level = nameof(OrchestratorEventLevel.Error), Text = $"Build server: {exception}" });
            Send(new ServerMessage { Kind = "result" });
        }
        finally
        {
            _builds.TryRemove(running.Id, out _);
            running.Cancel.Dispose();
            clientGone.Cancel();
            await watchClient.ContinueWith(_ => { });
            Touch();
            Interlocked.Decrement(ref _active);
        }
    }
}
