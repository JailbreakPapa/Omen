// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omen.Executors.Server;

public sealed record BuildRequestDto(string TargetFile, string? Platform, string? Architecture, string? Configuration, int? Jobs, bool Background, string? Coordinator);

public sealed record ProgressDto(int Completed, int Total, int Active, string? Description, bool Finished, bool Success, bool Cached, double Seconds);

public sealed record ResultDto(bool Success, int Total, int Succeeded, int Failed, int Skipped, int Cached, double Seconds);

/// <summary>One build a server is running, as <c>status</c> reports it.</summary>
public sealed record BuildStatusDto(string Id, string Target, bool Background, DateTime StartedUtc, int Completed, int Total, string? Current, string? Summary);

/// <summary>The most recent build a server finished.</summary>
public sealed record LastBuildDto(string Target, bool Success, DateTime FinishedUtc);

/// <summary>A server and its running builds, as <c>status</c> reports it (the tray and <c>omen server --status</c>).</summary>
public sealed record ServerStatusDto(
    string Version, string Root, string Configuration, int Slots, int SlotsInUse, bool RulesWarm, int IdleSecondsLeft, List<BuildStatusDto> Builds)
{
    public LastBuildDto? LastFinished { get; init; }
}

/// <summary>
/// Everything that crosses the build server's pipe. Kind: "hello" (Version), "request" (Request), "event" (Level,
/// Text), "progress" (Progress), "result" (Result; null when the build failed before a graph existed), "status"
/// (reply: Text and Status), "stop", "cancel" (Text = build id), "restarting" (a server of another version is
/// draining). Only "request" is version-checked: control messages come from tools of any version.
/// </summary>
public sealed class ServerMessage
{
    public required string Kind { get; init; }
    public ServerStatusDto? Status { get; init; }
    public string? Version { get; init; }
    public BuildRequestDto? Request { get; init; }
    public string? Level { get; init; }
    public string? Text { get; init; }
    public ProgressDto? Progress { get; init; }
    public ResultDto? Result { get; init; }
}

/// <summary>One JSON object per line, UTF-8, both directions.</summary>
public static class ServerProtocol
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string PipeName(string projectRoot, string configuration) =>
        $"omen-{Path.GetFullPath(projectRoot).TrimEnd('\\', '/').Replace(':', '_').Replace('\\', '_').Replace('/', '_')}-{configuration}";

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json) + "\n");
        await stream.WriteAsync(line, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>The next message, or null when the other side closed the pipe.</summary>
    public static async Task<ServerMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(one, ct);
            }
            catch (IOException)
            {
                return null;
            }
            if (read == 0)
                return bytes.Count == 0 ? null : Parse(bytes);
            if (one[0] == (byte)'\n')
                return Parse(bytes);
            bytes.Add(one[0]);
        }
    }

    private static ServerMessage? Parse(List<byte> bytes) =>
        JsonSerializer.Deserialize<ServerMessage>(Encoding.UTF8.GetString(bytes.ToArray()), Json);
}
