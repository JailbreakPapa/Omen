// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.IO.Pipes;

namespace Omen.Executors.Server;

/// <summary>The server died mid-build or never came up; the caller builds in-process instead.</summary>
public sealed class ServerUnavailableException(string message) : Exception(message);

/// <summary>The <c>omen build</c> side of the build server's pipe.</summary>
public static class BuildServerClient
{
    /// <summary>
    /// Sends one build to the server for <paramref name="projectRoot"/>/<paramref name="configuration"/>, starting
    /// one with <paramref name="start"/> when nobody answers, and forwards its events and progress. Completed is
    /// false only if the server was replaced mid-request more than twice. Result null: the build failed before a
    /// graph existed (the events say why).
    /// </summary>
    public static async Task<(bool Completed, ResultDto? Result)> BuildAsync(
        string projectRoot, string configuration, string version, BuildRequestDto request,
        Action<string, string> onEvent, Action<ProgressDto> onProgress, Func<Task> start, CancellationToken ct,
        TimeSpan? startupTimeout = null)
    {
        var name = ServerProtocol.PipeName(projectRoot, configuration);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var pipe = await ConnectOrStartAsync(name, start, startupTimeout ?? TimeSpan.FromSeconds(60), ct);
            await ServerProtocol.WriteAsync(pipe, new ServerMessage { Kind = "hello", Version = version }, ct);
            await ServerProtocol.WriteAsync(pipe, new ServerMessage { Kind = "request", Request = request }, ct);
            var restarting = false;
            while (!restarting)
            {
                var message = await ServerProtocol.ReadAsync(pipe, ct)
                    ?? throw new ServerUnavailableException("Omen server died");
                switch (message.Kind)
                {
                    case "event":
                        onEvent(message.Level ?? "Info", message.Text ?? "");
                        break;
                    case "progress" when message.Progress is { } progress:
                        onProgress(progress);
                        break;
                    case "result":
                        return (true, message.Result);
                    case "restarting":
                        // An older server is draining; wait until it stops listening, then start ours.
                        await WaitForPipeGoneAsync(name, ct);
                        restarting = true;
                        break;
                }
            }
        }
        return (false, null);
    }

    private static async Task<NamedPipeClientStream> ConnectOrStartAsync(string name, Func<Task> start, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var nextStart = DateTime.UtcNow;
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, ct);
                return pipe;
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync();
            }
            if (DateTime.UtcNow >= deadline)
                throw new ServerUnavailableException("Omen server did not start");
            // A server started while an old one still held the root exits at once; ask again every 2 s.
            if (DateTime.UtcNow >= nextStart)
            {
                await start();
                nextStart = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            }
        }
    }

    /// <summary>The draining server stops listening; wait until a connect attempt times out (at most 30 s).</summary>
    private static async Task WaitForPipeGoneAsync(string name, CancellationToken ct)
    {
        for (var i = 0; i < 150; i++)
        {
            await using var probe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await probe.ConnectAsync(100, ct);
            }
            catch (TimeoutException)
            {
                return;
            }
            await Task.Delay(100, ct);
        }
    }
}
