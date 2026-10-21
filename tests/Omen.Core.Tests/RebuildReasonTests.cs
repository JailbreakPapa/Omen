// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Implementations;

namespace Omen.Core.Tests;

/// <summary>
/// Every action that will run says why, so an over-eager rebuild can be traced to its cause
/// instead of guessed at.
/// </summary>
public class RebuildReasonTests : IDisposable
{
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly Sha256DigestCalculator _calculator = new();
    private readonly ActionDigestStore _store;

    public RebuildReasonTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RebuildReasonTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new ActionDigestStore(Path.Combine(_dir, "digests.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string File_(string name, DateTime stamp)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTimeUtc(path, stamp);
        return path;
    }

    private static BuildAction Compile(string id, string output, string commandLine = "cl.exe /c", params string[] inputs) => new()
    {
        Id = id,
        Type = ActionType.Compile,
        Description = $"Compile {id}",
        CommandLine = commandLine,
        WorkingDirectory = "/test",
        Inputs = inputs.Select(i => new FileItem { Path = i }).ToList(),
        Outputs = [new FileItem { Path = output }],
    };

    private void Record(BuildAction action) =>
        _store.Set(action.Outputs[0].Path, action.ComputeDigest(_calculator), action.ComputeDigestParts(_calculator));

    [Fact]
    public void MissingOutput()
    {
        var action = Compile("a", Path.Combine(_dir, "a.obj"));
        new ActionGraph().WhyNotUpToDate(action, _calculator, _store).Should().Be($"missing-output {Path.Combine(_dir, "a.obj")}");
    }

    [Fact]
    public void InputNewer_NamesTheNewestInput()
    {
        var header = File_("a.h", New);
        var action = Compile("a", File_("a.obj", Old), "cl.exe /c", File_("a.cpp", Old), header);
        Record(action);
        new ActionGraph().WhyNotUpToDate(action, _calculator, _store).Should().Be($"input-newer {header}");
    }

    [Fact]
    public void FlagsChanged()
    {
        var output = File_("a.obj", New);
        var source = File_("a.cpp", Old);
        Record(Compile("a", output, "cl.exe /c /O1", source));
        var changed = Compile("a", output, "cl.exe /c /O2", source);
        new ActionGraph().WhyNotUpToDate(changed, _calculator, _store).Should().Be("command-changed flags");
    }

    [Fact]
    public void InputSetChanged()
    {
        var output = File_("a.obj", New);
        var source = File_("a.cpp", Old);
        Record(Compile("a", output, "cl.exe /c", source));
        var changed = Compile("a", output, "cl.exe /c", source, File_("b.h", Old));
        new ActionGraph().WhyNotUpToDate(changed, _calculator, _store).Should().Be("command-changed input-set");
    }

    [Fact]
    public void DigestFromBeforeParts_ReportsCommandChangedUnsplit()
    {
        var output = File_("a.obj", New);
        var source = File_("a.cpp", Old);
        var original = Compile("a", output, "cl.exe /c /O1", source);
        _store.Set(output, original.ComputeDigest(_calculator));
        var changed = Compile("a", output, "cl.exe /c /O2", source);
        new ActionGraph().WhyNotUpToDate(changed, _calculator, _store).Should().Be("command-changed");
    }

    [Fact]
    public void DigestFromBeforeParts_StillUpToDateWhenUnchanged()
    {
        var output = File_("a.obj", New);
        var action = Compile("a", output, "cl.exe /c", File_("a.cpp", Old));
        _store.Set(output, action.ComputeDigest(_calculator));
        new ActionGraph().WhyNotUpToDate(action, _calculator, _store).Should().BeNull();
    }

    [Fact]
    public void NoPreviousDigest()
    {
        var action = Compile("a", File_("a.obj", New), "cl.exe /c", File_("a.cpp", Old));
        new ActionGraph().WhyNotUpToDate(action, _calculator, _store).Should().Be("no-previous-digest");
    }

    [Fact]
    public void UpToDate_HasNoReason()
    {
        var action = Compile("a", File_("a.obj", New), "cl.exe /c", File_("a.cpp", Old));
        Record(action);
        new ActionGraph().WhyNotUpToDate(action, _calculator, _store).Should().BeNull();
    }

    [Fact]
    public void DependencyWillRun_CarriesTheUpstreamRootCause()
    {
        var codegen = Compile("gen", Path.Combine(_dir, "gen.h"));
        var consumer = Compile("use", File_("use.obj", New), "cl.exe /c", File_("use.cpp", Old));
        consumer.Dependencies.Add(codegen);
        codegen.Dependents.Add(consumer);
        Record(consumer);
        var graph = new ActionGraph();
        graph.AddAction(codegen);
        graph.AddAction(consumer);

        graph.MarkUpToDateActionsAsSkipped(_calculator, _store);

        consumer.Status.Should().Be(ActionStatus.Pending);
        consumer.RebuildReason.Should().Be("dependency-will-run Compile gen");
        consumer.RootCause.Should().Be($"missing-output {Path.Combine(_dir, "gen.h")}");
    }

    [Fact]
    public void Summary_RanksRootCausesByHowManyActionsTheyInvalidated()
    {
        var shared = File_("shared.h", New);
        var graph = new ActionGraph();
        foreach (var id in new[] { "a", "b", "c" })
        {
            var action = Compile(id, File_($"{id}.obj", Old), "cl.exe /c", File_($"{id}.cpp", Old), shared);
            Record(action);
            graph.AddAction(action);
        }
        var lonely = Compile("d", Path.Combine(_dir, "d.obj"));
        graph.AddAction(lonely);

        graph.MarkUpToDateActionsAsSkipped(_calculator, _store);
        var summary = graph.SummarizeRebuilds();

        summary.ByKind.Should().Equal(new Dictionary<string, int> { ["input-newer"] = 3, ["missing-output"] = 1 });
        summary.TopCauses[0].Should().Be(($"input-newer {shared}", 3));
        summary.ToString().Should().StartWith("4 to run: 3 input-newer, 1 missing-output");
    }
}
