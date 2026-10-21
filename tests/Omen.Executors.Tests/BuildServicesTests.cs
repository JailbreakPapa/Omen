// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public class BuildServicesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(BuildServicesTests), Guid.NewGuid().ToString("N"));

    public BuildServicesTests() => TestProjects.WriteOneFileProject(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private BuildOrchestratorRequest Request() => new()
    {
        TargetFile = Path.Combine(_root, "App.target.cs"),
        Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64, Configuration = BuildConfiguration.Development,
    };

    [Fact(Timeout = 60_000)]
    public async Task ExecutorFactory_RunsTheGraph()
    {
        var used = 0;
        var services = new BuildServices
        {
            ExecutorFactory = (jobs, isUpToDate) => { used++; return new FakeExecutor(); },
        };

        var result = await new BuildOrchestrator(services).BuildAsync(Request(), null, null);

        result.Should().NotBeNull();
        used.Should().Be(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task CallerHoldsIntermediateLock_StillWaitsOutAnInProcessBuildAlreadyRunning()
    {
        var lockDir = Path.Combine(_root, "Intermediate", "Windows_Development", ".buildtool");
        Directory.CreateDirectory(lockDir);
        var held = new FileStream(Path.Combine(lockDir, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var services = new BuildServices { CallerHoldsIntermediateLock = true, ExecutorFactory = (_, _) => new FakeExecutor() };

        var build = new BuildOrchestrator(services).BuildAsync(Request(), null, null);
        await Task.Delay(1_000);
        build.IsCompleted.Should().BeFalse();

        held.Dispose();
        (await Task.WhenAny(build, Task.Delay(TimeSpan.FromSeconds(60)))).Should().BeSameAs(build);
    }

    [Fact(Timeout = 60_000)]
    public async Task CallerHoldsIntermediateLock_DoesNotWaitOnAnotherServerBuildsCheck()
    {
        // Another server request is in the middle of its own "is an in-process build running?" check.
        var lockDir = Path.Combine(_root, "Intermediate", "Windows_Development", ".buildtool");
        Directory.CreateDirectory(lockDir);
        using var concurrentCheck = new FileStream(Path.Combine(lockDir, "build.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var events = new List<OrchestratorEvent>();
        var services = new BuildServices { CallerHoldsIntermediateLock = true, ExecutorFactory = (_, _) => new FakeExecutor() };

        var result = await new BuildOrchestrator(services).BuildAsync(Request(), new Omen.Core.Diagnostics.SynchronousProgress<OrchestratorEvent>(events.Add), null);

        result.Should().NotBeNull();
        events.Should().NotContain(e => e.Message.StartsWith("Another Omen build holds"));
    }

    [Fact(Timeout = 60_000)]
    public async Task CallerHoldsIntermediateLock_DoesNotKeepIt()
    {
        var services = new BuildServices { CallerHoldsIntermediateLock = true, ExecutorFactory = (_, _) => new FakeExecutor() };
        var slow = new TaskCompletionSource();
        var blocking = new BuildServices
        {
            CallerHoldsIntermediateLock = true,
            ExecutorFactory = (_, _) => { slow.Task.Wait(); return new FakeExecutor(); },
        };

        var first = Task.Run(() => new BuildOrchestrator(blocking).BuildAsync(Request(), null, null));
        await Task.Delay(1_000);
        var second = new BuildOrchestrator(services).BuildAsync(Request(), null, null);

        (await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(60)))).Should().BeSameAs(second, "two server builds run side by side");
        slow.SetResult();
        await first;
    }
}
