// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;

namespace Omen.Core.Interfaces;

/// <summary>
/// Interface for executing build actions.
/// </summary>
public interface IExecutor
{
    /// <summary>
    /// Name of the executor.
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Maximum parallel actions this executor supports.
    /// </summary>
    int MaxParallelism { get; }
    
    /// <summary>
    /// Executes the build graph.
    /// </summary>
    Task<BuildResult> ExecuteAsync(
        ActionGraph graph, 
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a complete build execution.
/// </summary>
public sealed class BuildResult
{
    public required bool Success { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public required int TotalActions { get; init; }
    public required int SuccessfulActions { get; init; }
    public required int FailedActions { get; init; }
    public required int SkippedActions { get; init; }
    public required int CachedActions { get; init; }
    public IReadOnlyList<ActionResult> ActionResults { get; init; } = [];
    public IReadOnlyList<string> OutputFiles { get; init; } = [];
}

/// <summary>
/// Progress update during build execution.
/// </summary>
public sealed class BuildProgress
{
    public required int CompletedActions { get; init; }
    public required int TotalActions { get; init; }
    public required int ActiveActions { get; init; }
    public required BuildAction? CurrentAction { get; init; }
    public required ActionResult? LastResult { get; init; }
    public double PercentComplete => TotalActions > 0 ? (double)CompletedActions / TotalActions * 100 : 0;
}
