// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Interfaces;
using Omen.Executors.Server;

namespace Omen.Executors.Tests;

public class ServerControlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ServerControlTests), Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _server;

    public ServerControlTests()
    {
        TestProjects.WriteOneFileProject(_root);
        _server = new BuildServer(_root, "Development", "v1", 2, TimeSpan.FromMinutes(5), HeldRun).RunAsync(_stop.Token);
    }

    public void Dispose()
    {
        _gate.TrySetResult();
        _stop.Cancel();
        _server.Wait(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 20 && Directory.Exists(_root); i++)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { Thread.Sleep(100); }
        }
    }

    /// <summary>Every action waits for the test to open the gate, so a build stays visibly "running".</summary>
    private async Task<ActionResult> HeldRun(BuildAction action, CancellationToken ct)
    {
        await _gate.Task.WaitAsync(ct);
        return await BuildServerTests.WriteOutputs(action, ct);
    }

    private string Pipe => ServerProtocol.PipeName(_root, "Development");

    private Task<(bool, ResultDto?)> StartBuild(bool background = false) =>
        BuildServerClient.BuildAsync(_root, "Development", "v1",
            new BuildRequestDto(Path.Combine(_root, "App.target.cs"), "Windows", "X64", "Development", null, background, null),
            (_, _) => { }, _ => { }, () => Task.CompletedTask, default);

    private async Task<ServerStatusDto> WaitForStatus(Func<ServerStatusDto, bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await ServerControl.QueryStatusAsync(Pipe, default) is { } status && condition(status))
                return status;
            await Task.Delay(50);
        }
        throw new TimeoutException("status never matched");
    }

    [Fact(Timeout = 60_000)]
    public async Task Directory_FindsTheRunningServer()
    {
        await WaitForStatus(_ => true);
        ServerDirectory.ListPipes().Should().Contain(Pipe);
    }

    [Fact(Timeout = 60_000)]
    public async Task Status_DescribesTheServerAndItsRunningBuild()
    {
        var build = StartBuild(background: true);

        var status = await WaitForStatus(s => s.Builds.Count == 1 && s.Builds[0].Total > 0);

        status.Root.Should().Be(Path.GetFullPath(_root));
        status.Configuration.Should().Be("Development");
        status.Version.Should().Be("v1");
        status.Slots.Should().Be(2);
        status.Builds[0].Target.Should().EndWith("App.target.cs");
        status.Builds[0].Background.Should().BeTrue();

        _gate.SetResult();
        (await build).Item2!.Success.Should().BeTrue();
        var after = await WaitForStatus(s => s.Builds.Count == 0);
        after.RulesWarm.Should().BeTrue();
        after.LastFinished!.Target.Should().EndWith("App.target.cs");
        after.LastFinished.Success.Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task Cancel_EndsThatBuildWithAFailedResult()
    {
        var build = StartBuild();
        var id = (await WaitForStatus(s => s.Builds.Count == 1)).Builds[0].Id;

        await ServerControl.CancelAsync(Pipe, id, default);

        var (completed, result) = await build;
        completed.Should().BeTrue("a cancelled build answers its client; it does not look like a dead server");
        result.Should().BeNull();
        await WaitForStatus(s => s.Builds.Count == 0);
    }

    [Fact(Timeout = 60_000)]
    public async Task ControlMessages_FromAnotherVersion_DoNotRestartTheServer()
    {
        await WaitForStatus(_ => true);
        await ServerControl.QueryStatusAsync(Pipe, default, version: "some-other-build");
        await Task.Delay(300);
        _server.IsCompleted.Should().BeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task Stop_EndsTheServer()
    {
        await WaitForStatus(_ => true);
        await ServerControl.StopAsync(Pipe, default);
        (await Task.WhenAny(_server, Task.Delay(10_000))).Should().BeSameAs(_server);
    }
}
