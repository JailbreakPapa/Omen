// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Executors;

/// <summary>
/// Single-threaded local executor for debugging and simple builds.
/// Delegates to ParallelExecutor, which no longer does its own up-to-date pass -
/// unlike BuildOrchestrator, this class never calls one either, so any caller
/// using it directly will always re-run every action instead of skipping
/// unchanged ones. Currently unused in production; give it its own digest-aware
/// pre-pass (see BuildOrchestrator.BuildAsync) before wiring it up for real use.
/// </summary>
public sealed class LocalExecutor : IExecutor
{
    public string Name => "Local Executor";
    public int MaxParallelism => 1;
    
    public async Task<BuildResult> ExecuteAsync(
        ActionGraph graph,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Use parallel executor with parallelism of 1
        var parallelExecutor = new ParallelExecutor(maxParallelism: 1);
        return await parallelExecutor.ExecuteAsync(graph, progress, ct);
    }
}
