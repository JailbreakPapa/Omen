// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Interfaces;
using Omen.Core.Rules;

namespace Omen.Executors.Orchestration;

public sealed class ToolchainProbeRequest
{
    public required BuildContext Context { get; init; }
    public required string Source { get; init; }
    public bool Link { get; init; }
    public bool Run { get; init; }
    public List<string> IncludePaths { get; init; } = [];
    public List<string> Definitions { get; init; } = [];
    public List<string> CompilerFlags { get; init; } = [];
    public List<string> LinkerFlags { get; init; } = [];
    public List<string> Libraries { get; init; } = [];
    public CppStandard Standard { get; init; } = CppStandard.Cpp20;
    public double TimeoutSeconds { get; init; } = 60;
}

public sealed record ToolchainProbeResult(bool Success, bool CompileSucceeded, bool? LinkSucceeded,
    int? RunExitCode, bool TimedOut, string Output, string Directory, string Compiler);

/// <summary>Real compile/link/run capability checks isolated from target outputs; results are never cached stale.</summary>
public sealed class ToolchainProbe
{
    public async Task<ToolchainProbeResult> RunAsync(ToolchainProbeRequest request, IToolchain toolchain, CancellationToken ct = default)
    {
        if (request.Run && (request.Context.Platform != Omen.Platforms.PlatformFactory.GetHostPlatform() ||
            request.Context.Architecture != Omen.Platforms.PlatformFactory.GetHostArchitecture()))
            throw new InvalidOperationException("A cross-compiled probe cannot run on this host. Use a compile/link check instead.");
        if (!double.IsFinite(request.TimeoutSeconds) || request.TimeoutSeconds <= 0 || request.TimeoutSeconds > 86400)
            throw new ArgumentOutOfRangeException(nameof(request.TimeoutSeconds));
        string directory = Path.Combine(request.Context.IntermediateDirectory, "Checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "probe.cpp");
        string obj = Path.Combine(directory, "probe" + toolchain.ObjectFileExtension);
        string executable = Path.Combine(directory, "probe" + toolchain.ExecutableExtension);
        await File.WriteAllTextAsync(source, request.Source, ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        string output = "";
        bool compiled = false;
        bool? linkedOk = null;
        try
        {
            var compile = await toolchain.CompileAsync(new()
            {
                SourceFile = source, OutputFile = obj, Configuration = request.Context.Configuration,
                CppStandard = request.Standard, GenerateDebugInfo = false,
                IncludePaths = request.IncludePaths.Select(p => Path.GetFullPath(p, request.Context.ProjectRoot)).ToList(),
                Definitions = request.Context.GlobalDefinitions.Concat(request.Definitions).ToList(),
                AdditionalFlags = request.Context.GlobalCompilerOptions.Concat(request.CompilerFlags).ToList()
            }, linked.Token);
            output = compile.Output + compile.ErrorOutput;
            compiled = compile.Success;
            if (!compiled) return Result(false);
            if (request.Link || request.Run)
            {
                var link = await toolchain.LinkAsync(new()
                {
                    ObjectFiles = [obj], OutputFile = executable, OutputType = TargetType.Executable,
                    Configuration = request.Context.Configuration, GenerateDebugInfo = false, IncrementalLinking = false,
                    Libraries = request.Libraries, AdditionalFlags = request.Context.GlobalLinkerOptions.Concat(request.LinkerFlags).ToList()
                }, linked.Token);
                output += link.Output + link.ErrorOutput;
                linkedOk = link.Success;
                if (!link.Success) return Result(false);
            }
            if (request.Run)
            {
                var run = await WorkflowProcess.RunAsync(executable, [], directory, timeoutSeconds: request.TimeoutSeconds, ct: linked.Token);
                output += run.Output + run.Error;
                return Result(!run.TimedOut && run.ExitCode == 0, run.ExitCode, run.TimedOut);
            }
            return Result(true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Result(false, timedOut: true);
        }
        ToolchainProbeResult Result(bool success, int? exitCode = null, bool timedOut = false) =>
            new(success, compiled, linkedOk, exitCode, timedOut, output, directory, toolchain.Name + " " + toolchain.Version);
    }
}
