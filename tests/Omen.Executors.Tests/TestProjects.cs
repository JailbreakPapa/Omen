// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Executors.Tests;

/// <summary>Tiny on-disk projects for orchestrator/server tests. Nothing here invokes a compiler.</summary>
internal static class TestProjects
{
    public static void WriteOneFileProject(string root, string moduleName = "CoreModule", params string[] sources)
    {
        var sourceDir = Path.Combine(root, "Source", "Core");
        Directory.CreateDirectory(sourceDir);
        foreach (var source in sources.Length == 0 ? ["Core.cpp"] : sources)
            File.WriteAllText(Path.Combine(sourceDir, source), $"int {Path.GetFileNameWithoutExtension(source)}() {{ return 0; }}\n");
        File.WriteAllText(Path.Combine(sourceDir, "Core.module.cs"), $$"""
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class {{moduleName}} : ModuleRules
            {
                public {{moduleName}}(BuildContext context) : base(context) { SourceDirectory = "Source/Core"; }
            }
            """);
        File.WriteAllText(Path.Combine(root, "App.target.cs"), $$"""
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppTarget : TargetRules
            {
                public AppTarget(BuildContext context) : base(context) { Type = TargetType.StaticLibrary; LaunchModuleName = "{{moduleName}}"; UseUnityBuild = false; }
            }
            """);
    }
}

/// <summary>Completes every pending action by writing its outputs; records what it ran.</summary>
internal sealed class FakeExecutor : IExecutor
{
    public List<string> Ran { get; } = [];
    /// <summary>Runs before any action, with the graph the build handed over.</summary>
    public Action<ActionGraph>? OnExecute { get; init; }
    public string Name => "Fake";
    public int MaxParallelism => 1;

    public Task<BuildResult> ExecuteAsync(ActionGraph graph, IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
    {
        OnExecute?.Invoke(graph);
        foreach (var action in graph.GetTopologicalOrder().Where(a => a.Status == ActionStatus.Pending))
        {
            foreach (var output in action.Outputs)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output.Path)!);
                File.WriteAllText(output.Path, action.Id);
            }
            action.Status = ActionStatus.Completed;
            Ran.Add(action.Description);
        }
        return Task.FromResult(new BuildResult
        {
            Success = true, TotalDuration = TimeSpan.Zero, TotalActions = graph.Actions.Count,
            SuccessfulActions = Ran.Count, FailedActions = 0,
            SkippedActions = graph.Actions.Count(a => a.Status == ActionStatus.Skipped), CachedActions = 0,
        });
    }
}

