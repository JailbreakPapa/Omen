// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.IO.Pipes;

namespace Omen.Executors.Server;

/// <summary>Finds running build servers: each one listens on an <c>omen-&lt;root&gt;-&lt;configuration&gt;</c> pipe.</summary>
public static class ServerDirectory
{
    public static IReadOnlyList<string> ListPipes()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        try
        {
            return Directory.GetFiles(@"\\.\pipe\")
                .Select(Path.GetFileName)
                .Where(name => name != null && name.StartsWith("omen-", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }
}

/// <summary>Control messages to a running server by pipe name; none of them is version-checked.</summary>
public static class ServerControl
{
    /// <summary>The server's status, or null when it is gone or does not answer within a second.</summary>
    public static async Task<ServerStatusDto?> QueryStatusAsync(string pipe, CancellationToken ct, string version = "control") =>
        (await SendAsync(pipe, new ServerMessage { Kind = "status" }, version, ct))?.Status;

    /// <summary>Cancels one build as if its client had disconnected; its client receives a failed result.</summary>
    public static Task CancelAsync(string pipe, string buildId, CancellationToken ct) =>
        SendAsync(pipe, new ServerMessage { Kind = "cancel", Text = buildId }, "control", ct);

    /// <summary>Asks the server to finish its builds and exit.</summary>
    public static Task StopAsync(string pipe, CancellationToken ct) =>
        SendAsync(pipe, new ServerMessage { Kind = "stop" }, "control", ct);

    private static async Task<ServerMessage?> SendAsync(string pipe, ServerMessage message, string version, CancellationToken ct)
    {
        await using var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(1000, ct);
            await ServerProtocol.WriteAsync(stream, new ServerMessage { Kind = "hello", Version = version }, ct);
            await ServerProtocol.WriteAsync(stream, message, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            return await ServerProtocol.ReadAsync(stream, timeout.Token);
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
