// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Diagnostics;

namespace Omen.Executors.Orchestration;

public sealed record WorkflowProcessResult(int ExitCode, bool TimedOut, string Output, string Error, TimeSpan Duration);

/// <summary>Executes argument tokens directly, drains both streams, and kills descendants on cancellation.</summary>
public static class WorkflowProcess
{
    public static async Task<WorkflowProcessResult> RunAsync(string command, IEnumerable<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null,
        double timeoutSeconds = 300, CancellationToken ct = default)
    {
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 86400)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 0 and 86400 seconds.");
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        var tokens = arguments.ToArray();
        int shellCommand = Array.FindIndex(tokens, t => t.Equals("/c", StringComparison.OrdinalIgnoreCase));
        if (OperatingSystem.IsWindows() && Path.GetFileName(command).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) &&
            shellCommand >= 0 && tokens.Length == shellCommand + 2)
        {
            // cmd's /c string uses shell quoting, not the native CommandLineToArgvW escaping
            // used by ArgumentList. /s strips only this outer pair and preserves script quotes.
            var flags = tokens.Take(shellCommand).Where(t => !t.Equals("/s", StringComparison.OrdinalIgnoreCase));
            start.Arguments = string.Join(" ", flags.Append("/s").Append("/c")) + " \"" + tokens[^1] + "\"";
        }
        else foreach (var argument in tokens) start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value == null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }
        using var process = new Process { StartInfo = start };
        var watch = Stopwatch.StartNew();
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        bool timedOut = false;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            ct.ThrowIfCancellationRequested();
            timedOut = true;
        }
        return new(process.ExitCode, timedOut, await stdout, await stderr, watch.Elapsed);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int limit = 1024 * 1024;
        var text = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            int keep = Math.Min(read, limit - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
        }
        if (text.Length == limit) text.AppendLine("\n[output truncated at 1 MiB]");
        return text.ToString();
    }
}
