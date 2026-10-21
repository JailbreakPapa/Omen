// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Executors.Orchestration;
using Omen.Executors.Server;

namespace Omen.Executors.Tests;

/// <summary>
/// The server's correctness rule: after any edit, a warm build decides exactly what a cold build of the same tree
/// decides. Two copies of one project; each edit is applied to both; one builds cold, one warm.
/// </summary>
public class WarmColdEquivalenceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(WarmColdEquivalenceTests), Guid.NewGuid().ToString("N"));
    private string Cold => Path.Combine(_base, "cold");
    private string Warm => Path.Combine(_base, "warm");
    private readonly BuildWarmState _warm = new();

    public WarmColdEquivalenceTests()
    {
        TestProjects.WriteOneFileProject(Cold, sources: ["A.cpp", "B.cpp"]);
        TestProjects.WriteOneFileProject(Warm, sources: ["A.cpp", "B.cpp"]);
        _warm.WatchedRoot = Warm;
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private static async Task<List<string>> Run(string root, BuildWarmState? warm)
    {
        var executor = new FakeExecutor();
        await new BuildOrchestrator(new BuildServices { Warm = warm, ExecutorFactory = (_, _) => executor }).BuildAsync(new BuildOrchestratorRequest
        {
            TargetFile = Path.Combine(root, "App.target.cs"),
            Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64, Configuration = BuildConfiguration.Development,
        }, null, null);
        return executor.Ran.OrderBy(s => s).ToList();
    }

    private async Task Edit(Action<string> edit, params (string Relative, FileChangeKind Kind)[] warmChanges)
    {
        edit(Cold);
        edit(Warm);
        foreach (var (relative, kind) in warmChanges)
            _warm.Enqueue(new FileChange(Path.Combine(Warm, relative), kind));
        _warm.ApplyPendingChanges();
        var cold = await Run(Cold, null);
        var warm = await Run(Warm, _warm);
        warm.Should().Equal(cold);
    }

    [Fact(Timeout = 60_000)]
    public async Task EveryEditDecidesTheSameWarmAndCold()
    {
        await Edit(_ => { });                                                     // first build
        await Edit(_ => { });                                                     // no-op
        await Edit(r => File.SetLastWriteTimeUtc(Path.Combine(r, "Source/Core/A.cpp"), DateTime.UtcNow.AddMinutes(1)),
            ("Source/Core/A.cpp", FileChangeKind.Changed));
        await Edit(r => File.WriteAllText(Path.Combine(r, "Source/Core/C.cpp"), "int C() { return 0; }\n"),
            ("Source/Core/C.cpp", FileChangeKind.Created));
        await Edit(r => File.Move(Path.Combine(r, "Source/Core/B.cpp"), Path.Combine(r, "Source/Core/D.cpp")),
            ("Source/Core/B.cpp", FileChangeKind.Renamed), ("Source/Core/D.cpp", FileChangeKind.Created));
        await Edit(r => File.AppendAllText(Path.Combine(r, "App.target.cs"), "\n// edited\n"),
            ("App.target.cs", FileChangeKind.Changed));
    }
}
