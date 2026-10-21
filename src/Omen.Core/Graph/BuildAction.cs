// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Interfaces;

namespace Omen.Core.Graph;

/// <summary>
/// Type of build action.
/// </summary>
public enum ActionType
{
    Compile,
    Link,
    Archive,
    Copy,
    Custom,
    GeneratePCH
}

/// <summary>
/// Status of an action during execution.
/// </summary>
public enum ActionStatus
{
    Pending,
    Ready,
    Executing,
    Completed,
    Failed,
    Skipped,
    Cached
}

/// <summary>
/// Represents a single build action in the action graph.
/// </summary>
public sealed class BuildAction
{
    public required string Id { get; init; }
    public required ActionType Type { get; init; }
    public required string Description { get; init; }
    public required string CommandLine { get; init; }
    public required string WorkingDirectory { get; init; }
    
    /// <summary>
    /// Input files this action depends on.
    /// </summary>
    public IReadOnlyList<FileItem> Inputs { get; init; } = [];
    
    /// <summary>
    /// Output files this action produces.
    /// </summary>
    public IReadOnlyList<FileItem> Outputs { get; init; } = [];
    
    /// <summary>
    /// Actions that must complete before this action can execute.
    /// </summary>
    public List<BuildAction> Dependencies { get; } = [];
    
    /// <summary>
    /// Actions that depend on this action.
    /// </summary>
    public List<BuildAction> Dependents { get; } = [];
    
    /// <summary>
    /// Environment variables for this action.
    /// </summary>
    public Dictionary<string, string> Environment { get; init; } = [];
    
    /// <summary>
    /// Module this action belongs to.
    /// </summary>
    public string? ModuleName { get; init; }
    
    /// <summary>
    /// Whether this action can be executed remotely.
    /// </summary>
    public bool CanExecuteRemotely { get; init; } = true;
    
    /// <summary>
    /// Estimated execution time (for scheduling).
    /// </summary>
    public TimeSpan EstimatedDuration { get; init; } = TimeSpan.FromSeconds(5);
    
    /// <summary>
    /// Priority for scheduling (lower = higher priority).
    /// </summary>
    public int Priority { get; set; } = 0;
    
    /// <summary>
    /// Current status of this action.
    /// </summary>
    public ActionStatus Status { get; set; } = ActionStatus.Pending;
    
    /// <summary>
    /// Digest of the action for caching.
    /// </summary>
    public ContentDigest? ActionDigest { get; set; }
    
    /// <summary>
    /// Computes the action digest based on inputs, command, and environment.
    /// </summary>
    public ContentDigest ComputeDigest(IDigestCalculator calculator)
    {
        var content = $"{Type}|{CommandLine}|{InputsText()}|{EnvironmentText()}";
        ActionDigest = calculator.ComputeDigest(content);
        return ActionDigest.Value;
    }

    /// <summary>
    /// Separate digests of the command line, the input set and the environment ("flags:inputs:env"),
    /// recorded beside <see cref="ComputeDigest"/> so a changed digest can say which part changed.
    /// </summary>
    public string ComputeDigestParts(IDigestCalculator calculator) =>
        $"{calculator.ComputeDigest($"{Type}|{CommandLine}")}:{calculator.ComputeDigest(InputsText())}:{calculator.ComputeDigest(EnvironmentText())}";

    private string InputsText() => string.Join(",", Inputs.Select(i => $"{i.Path}:{i.Digest}"));

    private string EnvironmentText() => string.Join(",", Environment.OrderBy(e => e.Key).Select(e => $"{e.Key}={e.Value}"));

    /// <summary>Why this action will run ("input-newer C:/x.h", "command-changed flags", ...); null when it won't.</summary>
    public string? RebuildReason { get; set; }

    /// <summary>
    /// The reason at the top of the chain: this action's own <see cref="RebuildReason"/>, or for one that runs only
    /// because a dependency does, that dependency's root cause.
    /// </summary>
    public string? RootCause { get; set; }

    /// <summary>
    /// On a shared build server: the request that started the job this action joined instead of running itself;
    /// null when this request ran it.
    /// </summary>
    public string? SharedWith { get; set; }
    
    public override string ToString() => $"[{Type}] {Description}";
}

/// <summary>
/// Represents a file in the build system.
/// </summary>
public sealed class FileItem
{
    public required string Path { get; init; }
    public ContentDigest? Digest { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public long Size { get; set; }
    
    public bool Exists => File.Exists(Path);
}

/// <summary>
/// Result of executing a single action.
/// </summary>
public sealed class ActionResult
{
    public required BuildAction Action { get; init; }
    public required bool Success { get; init; }
    public required TimeSpan Duration { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public int ExitCode { get; init; }
    public bool WasCached { get; init; } = false;
    public bool WasRemote { get; init; } = false;
    public string? RemoteAgentId { get; init; }
}
