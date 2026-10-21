// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;
using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public class BuildEventLogTests : IDisposable
{
    private readonly string _intermediate = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(BuildEventLogTests), Guid.NewGuid().ToString("N"));
    private readonly List<string> _warnings = [];

    public void Dispose()
    {
        if (Directory.Exists(_intermediate)) Directory.Delete(_intermediate, recursive: true);
    }

    private static BuildAction Action(string id, string? module = "Core") => new()
    {
        Id = id, Type = ActionType.Compile, Description = $"Compile {id}.cpp", CommandLine = "cl", WorkingDirectory = ".", ModuleName = module
    };

    private static List<BuildEvent> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        return content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonSerializer.Deserialize<BuildEvent>(l)!)
            .ToList();
    }

    [Fact]
    public void Dispose_ClosesAStartedBuildAsFailed_ButAddsNothingAfterBuildFinished()
    {
        var abandoned = BuildEventLog.TryCreate(_intermediate, pid: 1, new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero), _warnings.Add)!;
        abandoned.BuildStarted("App.target.cs", "Windows", "X64", "Development", jobs: 1);
        abandoned.Dispose();

        var finished = BuildEventLog.TryCreate(_intermediate, pid: 2, new DateTimeOffset(2026, 9, 14, 18, 0, 1, TimeSpan.Zero), _warnings.Add)!;
        finished.BuildStarted("App.target.cs", "Windows", "X64", "Development", jobs: 1);
        finished.BuildFinished(new BuildResult { Success = true, TotalDuration = TimeSpan.Zero, TotalActions = 0, SuccessfulActions = 0, FailedActions = 0, SkippedActions = 0, CachedActions = 0 });
        finished.Dispose();

        var abandonedLines = Read(abandoned.FilePath);
        abandonedLines.Select(e => e.Kind).Should().Equal("build-started", "build-finished");
        abandonedLines[1].Success.Should().BeFalse("an exception or early return must not leave a watcher on 'Building' forever");
        Read(finished.FilePath).Select(e => e.Kind).Should().Equal("build-started", "build-finished");
    }

    [Fact]
    public void WritesTheDocumentedEventsInOrder_WithOutputOnlyOnFailure()
    {
        using var log = BuildEventLog.TryCreate(_intermediate, pid: 4242, new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero), _warnings.Add)!;
        var progress = log.WrapProgress(null);
        var events = log.WrapEvents(null);
        var a = Action("A"); var b = Action("B", module: null);

        log.BuildStarted("App.target.cs", "Windows", "X64", "Development", jobs: 8);
        events.Report(new OrchestratorEvent("Compiling build rules...", OrchestratorEventLevel.Info));
        log.GraphReady(total: 2, skipped: 0);
        progress.Report(new BuildProgress { CompletedActions = 0, TotalActions = 2, ActiveActions = 1, CurrentAction = a, LastResult = null });
        progress.Report(new BuildProgress { CompletedActions = 1, TotalActions = 2, ActiveActions = 0, CurrentAction = a,
            LastResult = new ActionResult { Action = a, Success = true, Duration = TimeSpan.FromMilliseconds(1500), StandardOutput = "A.cpp" } });
        progress.Report(new BuildProgress { CompletedActions = 2, TotalActions = 2, ActiveActions = 0, CurrentAction = b,
            LastResult = new ActionResult { Action = b, Success = false, Duration = TimeSpan.FromMilliseconds(20), StandardError = "error C2039: x", ExitCode = 2 } });
        log.BuildFinished(new BuildResult { Success = false, TotalDuration = TimeSpan.FromSeconds(3), TotalActions = 2, SuccessfulActions = 1, FailedActions = 1, SkippedActions = 0, CachedActions = 0 });

        var lines = Read(log.FilePath);
        lines.Select(e => e.Kind).Should().Equal("build-started", "message", "graph-ready", "action-started", "action-finished", "action-finished", "build-finished");
        lines[0].Pid.Should().Be(4242);
        lines[0].Target.Should().Be("App.target.cs");
        lines[0].Configuration.Should().Be("Development");
        lines[1].Level.Should().Be("info");
        lines[1].Text.Should().Be("Compiling build rules...");
        lines[2].Total.Should().Be(2);
        lines[3].Id.Should().Be("A"); lines[3].Module.Should().Be("Core"); lines[3].Active.Should().Be(1);
        lines[4].Success.Should().BeTrue(); lines[4].DurationMs.Should().Be(1500);
        lines[4].Stdout.Should().BeNull("a successful action's plain output is not kept");
        lines[5].Success.Should().BeFalse(); lines[5].ExitCode.Should().Be(2); lines[5].Stderr.Should().Be("error C2039: x"); lines[5].Module.Should().BeNull();
        lines[6].Success.Should().BeFalse(); lines[6].Failed.Should().Be(1); lines[6].DurationMs.Should().Be(3000);
        lines.Should().AllSatisfy(e => e.T.Should().NotBe(default));
        Path.GetFileName(log.FilePath).Should().Be("20260914-180000-4242.jsonl");
        Path.GetDirectoryName(log.FilePath).Should().Be(Path.Combine(_intermediate, BuildEventLog.DirectoryName));
    }

    [Fact]
    public void RebuildReasons_ReachActionStartedAndTheSummaryReachesGraphReady()
    {
        using var log = BuildEventLog.TryCreate(_intermediate, pid: 7, new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero), _warnings.Add)!;
        var a = Action("A");
        a.RebuildReason = "input-newer C:/x.h";

        log.GraphReady(total: 1, skipped: 0, summary: "1 to run: 1 input-newer");
        log.WrapProgress(null).Report(new BuildProgress { CompletedActions = 0, TotalActions = 1, ActiveActions = 1, CurrentAction = a, LastResult = null });

        var lines = Read(log.FilePath);
        lines[0].Summary.Should().Be("1 to run: 1 input-newer");
        lines[1].Reason.Should().Be("input-newer C:/x.h");
    }

    [Fact]
    public void EachLineIsOnDiskBeforeTheNextEvent()
    {
        using var log = BuildEventLog.TryCreate(_intermediate, 1, DateTimeOffset.UtcNow, _warnings.Add)!;
        log.BuildStarted("t", "Windows", "X64", "Debug", 1);

        using var reader = new FileStream(log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        new StreamReader(reader).ReadToEnd().Should().EndWith("\n").And.Contain("build-started");
    }

    [Fact]
    public void KeepsOnlyTheNewestTwentyFiles()
    {
        var dir = Path.Combine(_intermediate, BuildEventLog.DirectoryName);
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 25; i++) File.WriteAllText(Path.Combine(dir, $"20260101-0000{i:00}-1.jsonl"), "{}\n");

        using var log = BuildEventLog.TryCreate(_intermediate, 1, new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero), _warnings.Add)!;

        var files = Directory.GetFiles(dir, "*.jsonl").Select(Path.GetFileName).OrderBy(f => f).ToList();
        files.Should().HaveCount(BuildEventLog.KeepFiles);
        files.Should().Contain("20260914-000000-1.jsonl");
        files.Should().NotContain("20260101-000000-1.jsonl", "the oldest are deleted");
    }

    [Fact]
    public void WhenTheDirectoryCannotBeCreated_ReturnsNullAndWarnsOnce()
    {
        Directory.CreateDirectory(_intermediate);
        File.WriteAllText(Path.Combine(_intermediate, BuildEventLog.DirectoryName), "a file where the directory should be");

        var log = BuildEventLog.TryCreate(_intermediate, 1, DateTimeOffset.UtcNow, _warnings.Add);

        log.Should().BeNull();
        _warnings.Should().ContainSingle().Which.Should().Contain("build event log");
    }

    [Fact]
    public void WrappersForwardToTheInnerConsumer()
    {
        using var log = BuildEventLog.TryCreate(_intermediate, 1, DateTimeOffset.UtcNow, _warnings.Add)!;
        var seenEvents = new List<OrchestratorEvent>();
        var seenProgress = new List<BuildProgress>();

        log.WrapEvents(new SynchronousProgress<OrchestratorEvent>(seenEvents.Add)).Report(new OrchestratorEvent("hi", OrchestratorEventLevel.Warning));
        log.WrapProgress(new SynchronousProgress<BuildProgress>(seenProgress.Add))
            .Report(new BuildProgress { CompletedActions = 0, TotalActions = 1, ActiveActions = 1, CurrentAction = Action("A"), LastResult = null });

        seenEvents.Should().ContainSingle().Which.Message.Should().Be("hi");
        seenProgress.Should().ContainSingle();
        Read(log.FilePath).Select(e => e.Kind).Should().Equal("message", "action-started");
        Read(log.FilePath)[0].Level.Should().Be("warning");
    }
}
