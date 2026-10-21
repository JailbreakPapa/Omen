// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;
using System.Text.Json.Serialization;
using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Executors.Orchestration;

/// <summary>
/// Appends one JSON line per build event to
/// <c>&lt;intermediate&gt;/BuildEvents/&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;.jsonl</c> so a watcher
/// (Omen.GUI's Build Progress window) can follow any build on the machine, whoever started it, and
/// read back a build that was killed. Each line is flushed as it is written: a killed process
/// loses at most the line in flight. The log never fails a build - creation or write errors turn
/// it off for the rest of the build after one warning.
/// </summary>
public sealed class BuildEventLog : IDisposable
{
    public const string DirectoryName = "BuildEvents";
    public const int KeepFiles = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StreamWriter _writer;
    private readonly Action<string> _warn;
    private readonly object _lock = new();
    private readonly int _pid;
    private bool _disabled;
    private bool _open;

    public string FilePath { get; }

    private BuildEventLog(string filePath, StreamWriter writer, Action<string> warn, int pid)
    {
        FilePath = filePath;
        _writer = writer;
        _warn = warn;
        _pid = pid;
    }

    /// <summary>Null when the log cannot be created; <paramref name="warn"/> has been told why.</summary>
    public static BuildEventLog? TryCreate(string intermediateDirectory, int pid, DateTimeOffset now, Action<string> warn)
    {
        try
        {
            var dir = Path.Combine(intermediateDirectory, DirectoryName);
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "*.jsonl").OrderByDescending(Path.GetFileName).Skip(KeepFiles - 1))
            {
                File.Delete(old);
            }
            var path = Path.Combine(dir, $"{now:yyyyMMdd-HHmmss}-{pid}.jsonl");
            // Readers open with FileShare.ReadWrite to tail a file that is still being written.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
            return new BuildEventLog(path, new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" }, warn, pid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"Could not create the build event log under '{intermediateDirectory}': {ex.Message}");
            return null;
        }
    }

    public void BuildStarted(string target, string platform, string architecture, string configuration, int jobs)
    {
        _open = true;
        Write(new BuildEvent
        {
            T = DateTimeOffset.UtcNow, Kind = BuildEvent.BuildStarted, Pid = _pid,
            Target = target, Platform = platform, Architecture = architecture, Configuration = configuration, Jobs = jobs,
            IntermediateDirectory = Path.GetDirectoryName(Path.GetDirectoryName(FilePath)),
        });
    }

    public void GraphReady(int total, int skipped, string? summary = null) =>
        Write(new BuildEvent { T = DateTimeOffset.UtcNow, Kind = BuildEvent.GraphReady, Total = total, Skipped = skipped, Summary = summary });

    public void BuildFinished(BuildResult result)
    {
        _open = false;
        Write(new BuildEvent
        {
            T = DateTimeOffset.UtcNow, Kind = BuildEvent.BuildFinished, Success = result.Success,
            DurationMs = (long)result.TotalDuration.TotalMilliseconds, Total = result.TotalActions,
            Succeeded = result.SuccessfulActions, Failed = result.FailedActions, Skipped = result.SkippedActions, CachedCount = result.CachedActions,
        });
    }

    public IProgress<BuildProgress> WrapProgress(IProgress<BuildProgress>? inner) => new ProgressDecorator(this, inner);

    public IProgress<OrchestratorEvent> WrapEvents(IProgress<OrchestratorEvent>? inner) => new EventDecorator(this, inner);

    private void Record(BuildProgress p)
    {
        if (p.LastResult is { } result)
        {
            var action = result.Action;
            var keepOutput = !result.Success;
            Write(new BuildEvent
            {
                T = DateTimeOffset.UtcNow, Kind = BuildEvent.ActionFinished,
                Id = action.Id, Type = action.Type.ToString(), Description = action.Description, Module = action.ModuleName,
                Success = result.Success, Cached = result.WasCached ? true : null, Remote = result.WasRemote ? true : null,
                DurationMs = (long)result.Duration.TotalMilliseconds, ExitCode = result.ExitCode,
                Completed = p.CompletedActions, Total = p.TotalActions,
                Stdout = keepOutput && result.StandardOutput.Length > 0 ? result.StandardOutput : null,
                Stderr = keepOutput && result.StandardError.Length > 0 ? result.StandardError : null,
            });
        }
        else if (p.CurrentAction is { } action)
        {
            Write(new BuildEvent
            {
                T = DateTimeOffset.UtcNow, Kind = BuildEvent.ActionStarted,
                Id = action.Id, Type = action.Type.ToString(), Description = action.Description, Module = action.ModuleName,
                Reason = action.RebuildReason, SharedWith = action.SharedWith,
                Active = p.ActiveActions, Completed = p.CompletedActions, Total = p.TotalActions,
            });
        }
    }

    private void Record(OrchestratorEvent e) =>
        Write(new BuildEvent { T = DateTimeOffset.UtcNow, Kind = BuildEvent.Message, Level = e.Level.ToString().ToLowerInvariant(), Text = e.Message });

    private void Write(BuildEvent e)
    {
        lock (_lock)
        {
            if (_disabled) return;
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(e, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _disabled = true;
                _warn($"Stopped writing the build event log '{FilePath}': {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        // A started build that is disposed without a result was abandoned by an exception or an
        // early return. Close it as failed: a watcher whose pid is still alive (the GUI building
        // in-process) would otherwise show "Building" forever, since only a dead pid reads as killed.
        if (_open)
        {
            BuildFinished(new BuildResult
            {
                Success = false, TotalDuration = TimeSpan.Zero,
                TotalActions = 0, SuccessfulActions = 0, FailedActions = 0, SkippedActions = 0, CachedActions = 0,
            });
        }
        lock (_lock)
        {
            _disabled = true;
            _writer.Dispose();
        }
    }

    private sealed class ProgressDecorator(BuildEventLog log, IProgress<BuildProgress>? inner) : IProgress<BuildProgress>
    {
        public void Report(BuildProgress value)
        {
            log.Record(value);
            inner?.Report(value);
        }
    }

    private sealed class EventDecorator(BuildEventLog log, IProgress<OrchestratorEvent>? inner) : IProgress<OrchestratorEvent>
    {
        public void Report(OrchestratorEvent value)
        {
            log.Record(value);
            inner?.Report(value);
        }
    }
}
