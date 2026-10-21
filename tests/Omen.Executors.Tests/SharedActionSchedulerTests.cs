// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using Omen.Core.Graph;
using Omen.Core.Interfaces;
using Omen.Executors.Server;

namespace Omen.Executors.Tests;

public class SharedActionSchedulerTests
{
    /// <summary>A controllable action runner: each action blocks until released, and every start is recorded.</summary>
    private sealed class Runner
    {
        public ConcurrentQueue<string> Started { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource<bool>> Gates { get; } = new();
        public HashSet<string> Failing { get; } = [];

        public async Task<ActionResult> Run(BuildAction action, CancellationToken ct)
        {
            // The gate exists before the start is visible: a test that sees the start and calls ReleaseAll must release it.
            var gate = Gate(action.Description);
            Started.Enqueue(action.Description);
            await gate.Task.WaitAsync(ct);
            return new ActionResult { Action = action, Success = !Failing.Contains(action.Description), Duration = TimeSpan.Zero };
        }

        private TaskCompletionSource<bool> Gate(string description) =>
            Gates.GetOrAdd(description, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Release(string description) => Gate(description).TrySetResult(true);

        public void ReleaseAll()
        {
            foreach (var gate in Gates.Values) gate.TrySetResult(true);
        }
    }

    private static BuildAction Action(string name, string? output = null, string commandLine = "cl") => new()
    {
        Id = Guid.NewGuid().ToString("N"), Type = ActionType.Compile, Description = name, CommandLine = commandLine,
        WorkingDirectory = ".", Outputs = [new FileItem { Path = output ?? $"C:/out/{name}.obj" }],
    };

    private static ActionGraph Graph(params BuildAction[] actions)
    {
        var graph = new ActionGraph();
        foreach (var action in actions) graph.AddAction(action);
        return graph;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        condition().Should().BeTrue();
    }

    private static IExecutor For(SharedActionScheduler scheduler, string id, RequestClass cls, int cap, bool upToDate = false) =>
        scheduler.For(new ScheduleRequest(id, cls, cap), _ => upToDate);

    [Fact(Timeout = 60_000)]
    public async Task SameGraphTwice_EachActionRunsOnce()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var a = For(scheduler, "A", RequestClass.Interactive, 4).ExecuteAsync(Graph(Action("x"), Action("y")));
        var b = For(scheduler, "B", RequestClass.Interactive, 4).ExecuteAsync(Graph(Action("x"), Action("y")));
        await Eventually(() => runner.Started.Count == 2);
        runner.ReleaseAll();

        (await a).Success.Should().BeTrue();
        (await b).Success.Should().BeTrue();
        runner.Started.Should().BeEquivalentTo(["x", "y"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task SameOutputDifferentCommand_RunsBothOneAfterTheOther()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var a = For(scheduler, "A", RequestClass.Interactive, 4).ExecuteAsync(Graph(Action("first", "C:/out/same.obj", "cl /O1")));
        await Eventually(() => runner.Started.Count == 1);
        var b = For(scheduler, "B", RequestClass.Interactive, 4).ExecuteAsync(Graph(Action("second", "C:/out/same.obj", "cl /O2")));
        await Task.Delay(100);
        runner.Started.Should().Equal("first");

        runner.Release("first");
        await Eventually(() => runner.Started.Count == 2);
        runner.Release("second");
        (await a).Success.Should().BeTrue();
        (await b).Success.Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task Interactive_GoesBeforeBackground()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(1, runner.Run);
        var blocker = For(scheduler, "0", RequestClass.Interactive, 1).ExecuteAsync(Graph(Action("blocker")));
        await Eventually(() => runner.Started.Count == 1);
        var background = For(scheduler, "bg", RequestClass.Background, 1).ExecuteAsync(Graph(Action("bg")));
        await Task.Delay(50);
        var interactive = For(scheduler, "ui", RequestClass.Interactive, 1).ExecuteAsync(Graph(Action("ui")));
        await Task.Delay(50);

        runner.Release("blocker");
        await Eventually(() => runner.Started.Count == 2);
        runner.Started.Should().Equal("blocker", "ui");
        runner.ReleaseAll();
        await Eventually(() => runner.Started.Count == 3);
        runner.ReleaseAll();
        await Task.WhenAll(blocker, background, interactive);
    }

    [Fact(Timeout = 60_000)]
    public async Task Background_GetsTheFirstFreeSlotAtFourOrMore()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var ui = For(scheduler, "ui", RequestClass.Interactive, 8)
            .ExecuteAsync(Graph(Enumerable.Range(0, 8).Select(i => Action($"ui{i}")).ToArray()));
        await Eventually(() => runner.Started.Count == 4);               // ui took the whole pool before bg arrived
        var bg = For(scheduler, "bg", RequestClass.Background, 8).ExecuteAsync(Graph(Action("bg0")));
        await Task.Delay(50);

        runner.Release(runner.Started.First());                          // one slot frees; 4 ui actions still queued
        await Eventually(() => runner.Started.Count == 5);
        runner.Started.Last().Should().Be("bg0", "no preemption, but the first free slot goes to the waiting background request");
        runner.ReleaseAll();
        await Eventually(() => runner.Started.Count == 9);
        runner.ReleaseAll();
        await Task.WhenAll(ui, bg);
    }

    [Fact(Timeout = 60_000)]
    public async Task RequestCap_LimitsThatRequestOnly()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(8, runner.Run);
        var capped = For(scheduler, "c", RequestClass.Interactive, 2)
            .ExecuteAsync(Graph(Enumerable.Range(0, 6).Select(i => Action($"c{i}")).ToArray()));
        await Eventually(() => runner.Started.Count == 2);
        await Task.Delay(50);
        runner.Started.Count.Should().Be(2);

        for (var i = 0; i < 3 && runner.Started.Count < 6; i++)
        {
            runner.ReleaseAll();
            await Task.Delay(50);
        }
        await Eventually(() => runner.Started.Count == 6);
        runner.ReleaseAll();
        await capped;
    }

    [Fact(Timeout = 60_000)]
    public async Task SharedAction_TakesTheHigherClass()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(1, runner.Run);
        var blocker = For(scheduler, "0", RequestClass.Interactive, 1).ExecuteAsync(Graph(Action("blocker")));
        await Eventually(() => runner.Started.Count == 1);
        var bg = For(scheduler, "bg", RequestClass.Background, 1).ExecuteAsync(Graph(Action("other")));
        await Task.Delay(20);
        var bg2 = For(scheduler, "bg2", RequestClass.Background, 1).ExecuteAsync(Graph(Action("shared")));
        await Task.Delay(20);
        var ui = For(scheduler, "ui", RequestClass.Interactive, 1).ExecuteAsync(Graph(Action("shared")));
        await Task.Delay(50);

        runner.Release("blocker");
        await Eventually(() => runner.Started.Count == 2);
        runner.Started.Should().Equal("blocker", "shared");
        runner.ReleaseAll();
        await Eventually(() => runner.Started.Count == 3);
        runner.ReleaseAll();
        await Task.WhenAll(blocker, bg, bg2, ui);
    }

    [Fact(Timeout = 60_000)]
    public async Task Cancel_KeepsSharedWork()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        using var cancelA = new CancellationTokenSource();
        var a = For(scheduler, "A", RequestClass.Interactive, 4).ExecuteAsync(Graph(Action("shared"), Action("onlyA")), ct: cancelA.Token);
        var b = For(scheduler, "B", RequestClass.Interactive, 4).ExecuteAsync(Graph(Action("shared")));
        await Eventually(() => runner.Started.Count == 2);

        cancelA.Cancel();
        await FluentActions.Awaiting(() => a).Should().ThrowAsync<OperationCanceledException>();
        runner.Release("shared");

        (await b).Success.Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task Failure_ReachesEveryWaitingRequest()
    {
        var runner = new Runner();
        runner.Failing.Add("shared");
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var a = For(scheduler, "A", RequestClass.Interactive, 4).ExecuteAsync(Graph(Action("shared")));
        var b = For(scheduler, "B", RequestClass.Interactive, 4).ExecuteAsync(Graph(Action("shared"), Action("independent")));
        await Eventually(() => runner.Started.Count == 2);
        runner.ReleaseAll();

        (await a).Success.Should().BeFalse();
        var resultB = await b;
        resultB.Success.Should().BeFalse();
        resultB.SuccessfulActions.Should().Be(1, "B's independent action still ran");
    }

    [Fact(Timeout = 60_000)]
    public async Task UpToDateActions_AreSkippedWithoutASlot()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(1, runner.Run);
        var result = await For(scheduler, "A", RequestClass.Interactive, 1, upToDate: true).ExecuteAsync(Graph(Action("fresh")));

        result.SkippedActions.Should().Be(1);
        runner.Started.Should().BeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task UpToDateChain_IsSkippedEndToEnd()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var compile = Action("compile");
        var link = Action("link");
        link.Dependencies.Add(compile);
        compile.Dependents.Add(link);

        var result = await For(scheduler, "A", RequestClass.Interactive, 4, upToDate: true).ExecuteAsync(Graph(compile, link));

        result.Success.Should().BeTrue();
        result.SkippedActions.Should().Be(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task JoinedAction_RecordsWhichRequestStartedIt()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var mine = Action("x");
        var theirs = Action("x");
        var a = For(scheduler, "A", RequestClass.Interactive, 4).ExecuteAsync(Graph(theirs));
        await Eventually(() => runner.Started.Count == 1);
        var b = For(scheduler, "B", RequestClass.Interactive, 4).ExecuteAsync(Graph(mine));
        await Task.Delay(50);
        runner.ReleaseAll();
        await Task.WhenAll(a, b);

        mine.SharedWith.Should().Be("A");
        theirs.SharedWith.Should().BeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task JobFinishedAfterThisRequestStarted_IsReusedNotRerun()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var a = For(scheduler, "A", RequestClass.Interactive, 4).ExecuteAsync(Graph(Action("x")));
        var pre = Action("pre");
        var x = Action("x");
        x.Dependencies.Add(pre);
        pre.Dependents.Add(x);
        var b = For(scheduler, "B", RequestClass.Interactive, 4).ExecuteAsync(Graph(pre, x));
        await Eventually(() => runner.Started.Count == 2);

        runner.Release("x");
        (await a).Success.Should().BeTrue();
        runner.Release("pre");
        (await b).Success.Should().BeTrue();

        runner.Started.Count(s => s == "x").Should().Be(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task DependentAction_WaitsForItsDependency()
    {
        var runner = new Runner();
        var scheduler = new SharedActionScheduler(4, runner.Run);
        var compile = Action("compile");
        var link = Action("link");
        link.Dependencies.Add(compile);
        compile.Dependents.Add(link);
        var build = For(scheduler, "A", RequestClass.Interactive, 4).ExecuteAsync(Graph(compile, link));
        await Eventually(() => runner.Started.Count == 1);
        await Task.Delay(50);
        runner.Started.Should().Equal("compile");

        runner.Release("compile");
        await Eventually(() => runner.Started.Count == 2);
        runner.Release("link");
        (await build).Success.Should().BeTrue();
    }
}
