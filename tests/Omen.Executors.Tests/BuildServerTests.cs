// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using System.IO.Pipes;
using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Interfaces;
using Omen.Executors.Orchestration;
using Omen.Executors.Server;

namespace Omen.Executors.Tests;

public class BuildServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(BuildServerTests), Guid.NewGuid().ToString("N"));

    public BuildServerTests() => TestProjects.WriteOneFileProject(_root, sources: ["A.cpp", "B.cpp"]);

    public void Dispose()
    {
        for (var i = 0; i < 20 && Directory.Exists(_root); i++)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { Thread.Sleep(100); }
        }
    }

    internal static Task<ActionResult> WriteOutputs(BuildAction action, CancellationToken ct)
    {
        foreach (var output in action.Outputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output.Path)!);
            File.WriteAllText(output.Path, action.Id);
        }
        return Task.FromResult(new ActionResult { Action = action, Success = true, Duration = TimeSpan.Zero });
    }

    private async Task<List<ServerMessage>> Send(string version, ServerMessage request)
    {
        await using var pipe = new NamedPipeClientStream(".", ServerProtocol.PipeName(_root, "Development"), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        await ServerProtocol.WriteAsync(pipe, new ServerMessage { Kind = "hello", Version = version }, default);
        await ServerProtocol.WriteAsync(pipe, request, default);
        var messages = new List<ServerMessage>();
        while (await ServerProtocol.ReadAsync(pipe, default) is { } message)
        {
            messages.Add(message);
            if (message.Kind is "result" or "restarting" or "status") break;
        }
        return messages;
    }

    private ServerMessage BuildRequest() => new()
    {
        Kind = "request",
        Request = new BuildRequestDto(Path.Combine(_root, "App.target.cs"), "Windows", "X64", "Development", null, false, null),
    };

    private BuildServer Server(string version, TimeSpan? idle = null, Func<BuildAction, CancellationToken, Task<ActionResult>>? run = null) =>
        new(_root, "Development", version, slots: 4, idle ?? TimeSpan.FromMinutes(5), run ?? WriteOutputs);

    [Fact]
    public void PipeName_IsStablePerRootAndConfiguration() =>
        ServerProtocol.PipeName(@"F:\engine", "Development").Should().Be("omen-F__engine-Development");

    [Fact(Timeout = 60_000)]
    public async Task Build_StreamsEventsAndAResult_AndTheSecondBuildIsWarm()
    {
        using var stop = new CancellationTokenSource();
        var running = Server("v1").RunAsync(stop.Token);

        var first = await Send("v1", BuildRequest());
        var second = await Send("v1", BuildRequest());

        first.Last().Result!.Success.Should().BeTrue();
        first.Should().Contain(m => m.Kind == "event" && m.Text!.Contains("Rule compilation"));
        second.Last().Result!.Success.Should().BeTrue();
        (await Send("v1", new ServerMessage { Kind = "status" })).Single().Text.Should().Contain("compiled 1x");
        stop.Cancel();
        await running;
    }

    [Fact(Timeout = 60_000)]
    public async Task VersionMismatch_OldServerDrainsAndExits()
    {
        var running = Server("old").RunAsync(default);

        var reply = await Send("new", BuildRequest());

        reply.Single().Kind.Should().Be("restarting");
        (await Task.WhenAny(running, Task.Delay(10_000))).Should().BeSameAs(running);
    }

    [Fact(Timeout = 60_000)]
    public async Task IdleServer_Exits()
    {
        var running = Server("v1", idle: TimeSpan.FromMilliseconds(300)).RunAsync(default);
        (await Task.WhenAny(running, Task.Delay(10_000))).Should().BeSameAs(running);
    }

    [Fact(Timeout = 60_000)]
    public async Task Stop_EndsTheServer()
    {
        var running = Server("v1").RunAsync(default);
        await Send("v1", new ServerMessage { Kind = "stop" });
        (await Task.WhenAny(running, Task.Delay(10_000))).Should().BeSameAs(running);
    }

    [Fact(Timeout = 60_000)]
    public async Task TwoClientsAtOnce_BothSucceed_SharedActionsRunOnce()
    {
        var ran = new ConcurrentBag<string>();
        Task<ActionResult> Record(BuildAction action, CancellationToken ct)
        {
            ran.Add(action.Outputs[0].Path);
            return WriteOutputs(action, ct);
        }
        using var stop = new CancellationTokenSource();
        var running = Server("v1", run: Record).RunAsync(stop.Token);

        var results = await Task.WhenAll(Send("v1", BuildRequest()), Send("v1", BuildRequest()));

        foreach (var r in results)
        {
            var log = string.Join(Environment.NewLine, r.Where(m => m.Kind == "event").Select(m => $"{m.Level}: {m.Text}"));
            r.Last().Result.Should().NotBeNull(log);
            r.Last().Result!.Success.Should().BeTrue(log);
        }
        ran.Should().OnlyHaveUniqueItems();
        stop.Cancel();
        await running;
    }

    [Fact(Timeout = 60_000)]
    public async Task SecondServerForTheSameRoot_ExitsImmediately()
    {
        using var stop = new CancellationTokenSource();
        var first = Server("v1").RunAsync(stop.Token);
        await Task.Delay(200);
        var second = Server("v1").RunAsync(default);

        (await Task.WhenAny(second, Task.Delay(5_000))).Should().BeSameAs(second);
        first.IsCompleted.Should().BeFalse();
        stop.Cancel();
        await first;
    }

    [Fact(Timeout = 60_000)]
    public async Task InProcessBuild_WaitsWhileAServerOwnsTheConfiguration()
    {
        using var stop = new CancellationTokenSource();
        var running = Server("v1").RunAsync(stop.Token);
        await Task.Delay(200);

        var inProcess = new BuildOrchestrator(new BuildServices { ExecutorFactory = (_, _) => new FakeExecutor() }).BuildAsync(new BuildOrchestratorRequest
        {
            TargetFile = Path.Combine(_root, "App.target.cs"),
            Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64, Configuration = BuildConfiguration.Development,
        }, null, null);
        await Task.Delay(2_000);
        inProcess.IsCompleted.Should().BeFalse("the server owns Intermediate/ for Development");

        stop.Cancel();
        await running;
        (await Task.WhenAny(inProcess, Task.Delay(30_000))).Should().BeSameAs(inProcess);
    }
}
