// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json.Serialization;

namespace Omen.Executors.Orchestration;

/// <summary>
/// One line of a build's event log (see <see cref="BuildEventLog"/>). A single flat record for
/// every kind, so a reader deserializes each line the same way and switches on <see cref="Kind"/>;
/// fields a kind does not use stay null and are omitted from the JSON.
/// </summary>
public sealed record BuildEvent
{
    public const string BuildStarted = "build-started";
    public const string GraphReady = "graph-ready";
    public const string ActionStarted = "action-started";
    public const string ActionFinished = "action-finished";
    public const string Message = "message";
    public const string BuildFinished = "build-finished";

    [JsonPropertyName("t")] public required DateTimeOffset T { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    // build-started
    [JsonPropertyName("pid")] public int? Pid { get; init; }
    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("architecture")] public string? Architecture { get; init; }
    [JsonPropertyName("configuration")] public string? Configuration { get; init; }
    [JsonPropertyName("jobs")] public int? Jobs { get; init; }
    [JsonPropertyName("intermediateDirectory")] public string? IntermediateDirectory { get; init; }

    // graph-ready / action-* / build-finished counts
    [JsonPropertyName("total")] public int? Total { get; init; }
    [JsonPropertyName("skipped")] public int? Skipped { get; init; }
    [JsonPropertyName("completed")] public int? Completed { get; init; }
    [JsonPropertyName("active")] public int? Active { get; init; }

    // action-started / action-finished
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("module")] public string? Module { get; init; }
    // action-started: why it runs (BuildAction.RebuildReason). graph-ready: ActionGraph.SummarizeRebuilds.
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    // action-started on a build server: the request whose run of this action this build waits on.
    [JsonPropertyName("sharedWith")] public string? SharedWith { get; init; }
    [JsonPropertyName("success")] public bool? Success { get; init; }
    [JsonPropertyName("cached")] public bool? Cached { get; init; }
    [JsonPropertyName("remote")] public bool? Remote { get; init; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; init; }
    [JsonPropertyName("exitCode")] public int? ExitCode { get; init; }
    [JsonPropertyName("stdout")] public string? Stdout { get; init; }
    [JsonPropertyName("stderr")] public string? Stderr { get; init; }

    // message
    [JsonPropertyName("level")] public string? Level { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }

    // build-finished
    [JsonPropertyName("succeeded")] public int? Succeeded { get; init; }
    [JsonPropertyName("failed")] public int? Failed { get; init; }
    [JsonPropertyName("cachedCount")] public int? CachedCount { get; init; }
}
