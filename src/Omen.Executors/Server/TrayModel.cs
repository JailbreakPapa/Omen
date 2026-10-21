// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Executors.Server;

public enum TrayState { Idle, Building, Failed }

/// <summary>One line of the tray window: a server, or one of its builds (BuildId set, with a progress percentage).</summary>
public sealed record TrayRow(string Text, string Pipe, string? BuildId, int Progress);

/// <summary>What the tray shows for the servers it can see - kept out of the UI so it can be tested.</summary>
public sealed record TrayModel(TrayState State, string Tooltip, IReadOnlyList<TrayRow> Rows)
{
    public static TrayModel From(IReadOnlyList<ServerStatusDto> servers, bool lastFailed, IReadOnlyList<string>? pipes = null)
    {
        var builds = servers.Sum(s => s.Builds.Count);
        var state = builds > 0 ? TrayState.Building : lastFailed ? TrayState.Failed : TrayState.Idle;
        var tooltip = servers.Count == 0
            ? "Omen: no build server running"
            : $"Omen: {Plural(servers.Count, "server")}, " + (builds == 0 ? "idle" : $"{Plural(builds, "build")} running");

        var rows = new List<TrayRow>();
        for (var i = 0; i < servers.Count; i++)
        {
            var server = servers[i];
            var pipe = pipes is { } p && i < p.Count ? p[i] : ServerProtocol.PipeName(server.Root, server.Configuration);
            var activity = server.Builds.Count > 0 ? $"{server.Builds.Count} building" : "idle";
            var idleMinutes = Math.Max(1, (server.IdleSecondsLeft + 59) / 60);
            rows.Add(new TrayRow(
                $"{server.Root} ({server.Configuration}) - {activity}, {server.Slots} slots, rules {(server.RulesWarm ? "warm" : "cold")}, idle exit in {idleMinutes} min",
                pipe, null, 0));
            foreach (var build in server.Builds)
            {
                var progress = build.Total > 0 ? build.Completed * 100 / build.Total : 0;
                rows.Add(new TrayRow(
                    $"  {TargetName(build.Target)}{(build.Background ? " [background]" : "")} {build.Completed}/{build.Total}"
                        + (build.Current is { Length: > 0 } current ? $" - {current}" : ""),
                    pipe, build.Id, progress));
            }
        }
        return new TrayModel(state, tooltip, rows);
    }

    /// <summary>A server finished a build that failed after <paramref name="seenUtc"/> (when the window was last opened).</summary>
    public static bool FailedSince(IReadOnlyList<ServerStatusDto> servers, DateTime seenUtc) =>
        servers.Any(s => s.LastFinished is { Success: false } last && last.FinishedUtc > seenUtc);

    public static string TargetName(string targetFile)
    {
        var name = Path.GetFileName(targetFile);
        return name.EndsWith(".target.cs", StringComparison.OrdinalIgnoreCase) ? name[..^".target.cs".Length] : name;
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
