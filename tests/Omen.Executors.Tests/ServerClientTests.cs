// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Executors.Server;

namespace Omen.Executors.Tests;

public class ServerClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ServerClientTests), Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stopServers = new();
    private readonly List<Task> _servers = [];

    public ServerClientTests() => TestProjects.WriteOneFileProject(_root);

    public void Dispose()
    {
        _stopServers.Cancel();
        Task.WaitAll([.. _servers], TimeSpan.FromSeconds(30));
        for (var i = 0; i < 20 && Directory.Exists(_root); i++)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { Thread.Sleep(100); }
        }
    }

    private BuildRequestDto Request() => new(Path.Combine(_root, "App.target.cs"), "Windows", "X64", "Development", null, false, null);

    private Task StartServer(string version)
    {
        _servers.Add(new BuildServer(_root, "Development", version, 2, TimeSpan.FromMinutes(5), BuildServerTests.WriteOutputs).RunAsync(_stopServers.Token));
        return Task.CompletedTask;
    }

    [Fact(Timeout = 60_000)]
    public async Task NoServer_ClientStartsOne()
    {
        var starts = 0;
        var (_, result) = await BuildServerClient.BuildAsync(_root, "Development", "v1", Request(), (_, _) => { }, _ => { },
            start: () => { starts++; return StartServer("v1"); }, default);

        result!.Success.Should().BeTrue();
        starts.Should().BeGreaterThan(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task RunningServer_IsUsedWithoutStartingAnother()
    {
        await StartServer("v1");
        await Task.Delay(200);
        var starts = 0;

        var (_, result) = await BuildServerClient.BuildAsync(_root, "Development", "v1", Request(), (_, _) => { }, _ => { },
            start: () => { starts++; return Task.CompletedTask; }, default);

        result!.Success.Should().BeTrue();
        starts.Should().Be(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task VersionMismatch_OldServerDrainsAndExits()
    {
        var old = new BuildServer(_root, "Development", "old", 2, TimeSpan.FromMinutes(5), BuildServerTests.WriteOutputs).RunAsync(default);
        await Task.Delay(200);

        var (_, result) = await BuildServerClient.BuildAsync(_root, "Development", "new", Request(), (_, _) => { }, _ => { },
            start: () => StartServer("new"), default);

        (await Task.WhenAny(old, Task.Delay(10_000))).Should().BeSameAs(old);
        result!.Success.Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerNeverComesUp_Throws()
    {
        await FluentActions.Awaiting(() => BuildServerClient.BuildAsync(_root, "Development", "v1", Request(), (_, _) => { }, _ => { },
                start: () => Task.CompletedTask, default, startupTimeout: TimeSpan.FromSeconds(2)))
            .Should().ThrowAsync<ServerUnavailableException>();
    }

    [Fact(Timeout = 60_000)]
    public async Task Events_AndProgress_ReachTheClient()
    {
        var events = new List<string>();
        var finished = 0;
        await BuildServerClient.BuildAsync(_root, "Development", "v1", Request(), (_, text) => events.Add(text), p => { if (p.Finished) finished++; },
            start: () => StartServer("v1"), default);

        events.Should().Contain(e => e.Contains("Rule compilation"));
        finished.Should().BeGreaterThan(0);
    }
}
