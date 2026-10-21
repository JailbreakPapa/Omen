// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Interfaces;
using Omen.Executors.Server;

namespace Omen.Executors.Orchestration;

/// <summary>
/// What a long-lived host (the build server) lends a <see cref="BuildOrchestrator"/>: state kept warm across
/// builds, the executor that runs the graph, and whether it already holds the intermediate directory. Null
/// members mean "build cold, alone, with a ParallelExecutor" - the in-process <c>omen build --no-server</c> path.
/// </summary>
public sealed class BuildServices
{
    public BuildWarmState? Warm { get; init; }

    /// <summary>(jobs, isUpToDate) -> the executor for one combination's graph.</summary>
    public Func<int, Func<BuildAction, bool>, IExecutor>? ExecutorFactory { get; init; }

    /// <summary>The caller holds the intermediate directory for its own lifetime (see the build server's lock).</summary>
    public bool CallerHoldsIntermediateLock { get; init; }
}
