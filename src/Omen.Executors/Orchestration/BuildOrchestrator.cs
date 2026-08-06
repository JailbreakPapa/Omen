// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Implementations;
using Omen.Core.Interfaces;
using Omen.Core.Options;
using Omen.Core.Rules;
using Omen.Distributed.Cache;
using Omen.Platforms;

namespace Omen.Executors.Orchestration;

/// <summary>
/// A build/rebuild request. Unlike the CLI's BuildCommand, the target file is required
/// and already-resolved - callers (CLI, GUI) each own their own "which target file"
/// discovery (search-by-name for the CLI, file picker for the GUI) rather than this
/// class guessing at Environment.CurrentDirectory, which has no meaningful value in a GUI.
/// </summary>
public sealed class BuildOrchestratorRequest
{
    public required string TargetFile { get; init; }
    public required TargetPlatform Platform { get; init; }
    public required TargetArchitecture Architecture { get; init; }
    public required BuildConfiguration Configuration { get; init; }
    public int? Jobs { get; init; }
}

/// <summary>
/// Runs the same build sequence BuildCommand's CLI handler runs, callable from any
/// front end. Returns null when the request fails before a graph could be built (rule
/// compilation, missing toolchain, missing target); returns a zero-action successful
/// BuildResult for "nothing to build" cases; returns the real ParallelExecutor result
/// otherwise.
/// </summary>
public sealed class BuildOrchestrator
{
    public async Task<BuildResult?> BuildAsync(
        BuildOrchestratorRequest request,
        IProgress<OrchestratorEvent>? events,
        IProgress<BuildProgress>? buildProgress,
        CancellationToken ct = default)
    {
        var workingDir = Path.GetDirectoryName(request.TargetFile) ?? Path.GetPathRoot(request.TargetFile) ?? request.TargetFile;

        var optionCacheStore = new OptionCacheStore(Path.Combine(workingDir, "Intermediate", "omen-cache.json"));

        var context = new BuildContext
        {
            Platform = request.Platform,
            Architecture = request.Architecture,
            Configuration = request.Configuration,
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate", $"{request.Platform}_{request.Configuration}"),
            OutputDirectory = Path.Combine(workingDir, "Binaries", $"{request.Platform}_{request.Configuration}"),
            ParallelJobs = request.Jobs ?? Environment.ProcessorCount,
            CachedOptionValues = optionCacheStore.Load()
        };

        var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));

        CompiledRules compiledRules;
        try
        {
            events?.Report(new OrchestratorEvent("Compiling build rules...", OrchestratorEventLevel.Info));
            compiledRules = await ruleCompiler.CompileRulesAsync(workingDir, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            events?.Report(new OrchestratorEvent($"Error compiling rules: {ex.Message}", OrchestratorEventLevel.Error));
            return null;
        }

        var sdk = PlatformFactory.GetSDK(request.Platform);
        if (sdk == null)
        {
            events?.Report(new OrchestratorEvent($"No SDK found for platform {request.Platform}", OrchestratorEventLevel.Error));
            return null;
        }

        var toolchain = PlatformFactory.CreateToolchain(request.Platform, request.Architecture);
        if (toolchain == null)
        {
            events?.Report(new OrchestratorEvent($"Could not create toolchain for {request.Platform}/{request.Architecture}", OrchestratorEventLevel.Error));
            return null;
        }

        var targets = compiledRules.CreateTargetRules(context);
        var modules = compiledRules.CreateModuleRules(context);

        events?.Report(new OrchestratorEvent($"Found {targets.Count} target(s), {modules.Count} module(s)", OrchestratorEventLevel.Info));

        try
        {
            LayeringValidator.Validate(modules);
        }
        catch (LayeringViolationException ex)
        {
            events?.Report(new OrchestratorEvent($"Layering violation: {ex.Message}", OrchestratorEventLevel.Error));
            return null;
        }

        if (modules.Count == 0)
        {
            events?.Report(new OrchestratorEvent("No modules to build.", OrchestratorEventLevel.Warning));
            return ZeroActionResult();
        }

        var targetRules = targets.FirstOrDefault();
        if (targetRules == null)
        {
            events?.Report(new OrchestratorEvent("No target found.", OrchestratorEventLevel.Error));
            return null;
        }

        var digestCalculator = new Sha256DigestCalculator();
        var graphBuilder = new ActionGraphBuilder(context, toolchain, digestCalculator);
        var graph = graphBuilder.Build(targetRules, modules);

        var digestStore = new ActionDigestStore(Path.Combine(context.IntermediateDirectory, ".buildtool", "digests.json"));
        var skipped = graph.MarkUpToDateActionsAsSkipped(digestCalculator, digestStore);
        if (skipped > 0)
        {
            events?.Report(new OrchestratorEvent($"{skipped} action(s) already up to date (unchanged command line), skipped.", OrchestratorEventLevel.Info));
        }

        events?.Report(new OrchestratorEvent($"Created action graph with {graph.Actions.Count} actions", OrchestratorEventLevel.Info));

        if (graph.Actions.Count == 0)
        {
            events?.Report(new OrchestratorEvent("Nothing to build - up to date!", OrchestratorEventLevel.Success));
            return ZeroActionResult();
        }

        var actionCache = new LocalActionCache(Path.Combine(context.IntermediateDirectory, ".cache"));
        var executor = new ParallelExecutor(request.Jobs, actionCache);

        var result = await executor.ExecuteAsync(graph, buildProgress, ct);

        // ParallelExecutor cancels cooperatively - its loop just exits when ct is cancelled,
        // so a cancelled build has zero *failed* actions and would otherwise fall through to
        // the success branch below. Throw here so callers' existing OperationCanceledException
        // handling reports "cancelled" instead of "succeeded".
        ct.ThrowIfCancellationRequested();

        // ParallelExecutor's own skip counter can't see actions this method already flipped
        // from Pending to Skipped via the digest-based pre-pass above (its internal
        // MarkUpToDateActionsAsSkipped() only counts actions still Pending) - recompute the
        // true count from the graph, which (unlike callers) this method still has access to.
        var trueSkippedCount = graph.Actions.Count(a => a.Status == ActionStatus.Skipped);
        result = new BuildResult
        {
            Success = result.Success,
            TotalDuration = result.TotalDuration,
            TotalActions = result.TotalActions,
            SuccessfulActions = result.SuccessfulActions,
            FailedActions = result.FailedActions,
            SkippedActions = trueSkippedCount,
            CachedActions = result.CachedActions,
            ActionResults = result.ActionResults,
            OutputFiles = result.OutputFiles
        };

        if (result.Success)
        {
            foreach (var action in graph.Actions.Where(a => a.Status is ActionStatus.Completed or ActionStatus.Skipped))
            {
                if (action.Outputs.Count == 0 || !File.Exists(action.Outputs[0].Path)) continue;
                digestStore.Set(action.Outputs[0].Path, action.ComputeDigest(digestCalculator));
            }
            digestStore.Save();

            events?.Report(new OrchestratorEvent("Build completed successfully!", OrchestratorEventLevel.Success));
        }
        else
        {
            events?.Report(new OrchestratorEvent("BUILD FAILED", OrchestratorEventLevel.Error));

            foreach (var actionResult in result.ActionResults.Where(r => !r.Success))
            {
                events?.Report(new OrchestratorEvent($"Failed: {actionResult.Action.Description}", OrchestratorEventLevel.Error));
                events?.Report(new OrchestratorEvent($"Command: {actionResult.Action.CommandLine}", OrchestratorEventLevel.Info));
                events?.Report(new OrchestratorEvent($"Exit code: {actionResult.ExitCode}", OrchestratorEventLevel.Info));

                if (!string.IsNullOrEmpty(actionResult.StandardOutput))
                    events?.Report(new OrchestratorEvent($"stdout: {actionResult.StandardOutput}", OrchestratorEventLevel.Info));

                if (!string.IsNullOrEmpty(actionResult.StandardError))
                    events?.Report(new OrchestratorEvent($"stderr: {actionResult.StandardError}", OrchestratorEventLevel.Error));
            }
        }

        return result;
    }

    private static BuildResult ZeroActionResult() => new()
    {
        Success = true,
        TotalDuration = TimeSpan.Zero,
        TotalActions = 0,
        SuccessfulActions = 0,
        FailedActions = 0,
        SkippedActions = 0,
        CachedActions = 0
    };
}
