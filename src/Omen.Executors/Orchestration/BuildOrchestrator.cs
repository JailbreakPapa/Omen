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
    public TargetPlatform? Platform { get; init; }
    public TargetArchitecture? Architecture { get; init; }
    public BuildConfiguration? Configuration { get; init; }
    /// <summary>Allowed values per custom dimension - a dimension absent from this map is
    /// unconstrained. Null means no custom-dimension filter at all.</summary>
    public IReadOnlyDictionary<Type, IReadOnlyList<Enum>>? DimensionFilter { get; init; }
    public int? Jobs { get; init; }
}

/// <summary>
/// Runs the same build sequence BuildCommand's CLI handler runs, callable from any
/// front end. Returns null when the request fails before a graph could be built (rule
/// compilation, missing toolchain, missing target); returns a zero-action successful
/// BuildResult for "nothing to build" cases; returns the real ParallelExecutor result
/// otherwise. When the target declares a SupportedTargets matrix, resolves every matching
/// combination and builds each in turn, returning one aggregated BuildResult.
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
        var cachedOptionValues = optionCacheStore.Load();

        var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));
        CompiledRules compiledRules;
        try
        {
            events?.Report(new OrchestratorEvent("Compiling build rules...", OrchestratorEventLevel.Info));
            compiledRules = await ruleCompiler.CompileRulesAsync(workingDir, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            events?.Report(new OrchestratorEvent($"Error compiling rules: {ex.Message}", OrchestratorEventLevel.Error));
            return null;
        }

        var targetType = compiledRules.FindTargetType();
        if (targetType == null)
        {
            events?.Report(new OrchestratorEvent("No target found.", OrchestratorEventLevel.Error));
            return null;
        }

        var matrix = compiledRules.GetSupportedTargets(targetType);
        IReadOnlyList<ResolvedTarget> resolvedTargets;

        if (matrix == null)
        {
            // Today's exact behavior: one combination, defaulted the same way BuildCommand's
            // ParsePlatform/ParseArchitecture/ParseConfiguration always have.
            var platform = request.Platform ?? DefaultPlatformForHostOS();
            var architecture = request.Architecture ?? DefaultArchitectureForHostOS();
            var configuration = request.Configuration ?? BuildConfiguration.Development;
            resolvedTargets = [ResolvedTarget.Create(new Dictionary<Type, Enum>
            {
                [typeof(TargetPlatform)] = platform,
                [typeof(TargetArchitecture)] = architecture,
                [typeof(BuildConfiguration)] = configuration
            })];
        }
        else
        {
            var all = matrix.Resolve();
            if (all.Count == 0)
            {
                events?.Report(new OrchestratorEvent(
                    $"'{targetType.Name}'s SupportedTargets matrix resolved to zero combinations - check its Exclude rules.",
                    OrchestratorEventLevel.Error));
                return null;
            }

            resolvedTargets = all.Where(rt => MatchesFilter(rt, request)).ToList();
            if (resolvedTargets.Count == 0)
            {
                events?.Report(new OrchestratorEvent("No targets matched your filter.", OrchestratorEventLevel.Error));
                return null;
            }
        }

        // A dimension only needs to appear in the directory name if it actually varies across
        // the resolved set - Architecture included. Excluding Architecture unconditionally (an
        // earlier draft of this code did) breaks the "own IntermediateDirectory/OutputDirectory
        // so per-combination outputs never collide" requirement the moment a matrix declares more
        // than one Architecture value: two combinations differing only by Architecture would
        // otherwise compute the identical {Platform}_{Configuration} directory name and overwrite
        // each other's output. For a target without a matrix (resolvedTargets.Count == 1), no
        // dimension varies by definition, so this computes empty and the directory name stays
        // exactly today's {Platform}_{Configuration} - unchanged.
        var varyingDimensions = resolvedTargets[0].Values.Keys
            .Where(t => t != typeof(TargetPlatform) && t != typeof(BuildConfiguration))
            .Where(t => resolvedTargets.Select(r => r.Values[t]).Distinct().Count() > 1)
            .ToHashSet();

        var results = new List<BuildResult>();
        foreach (var resolved in resolvedTargets)
        {
            if (resolvedTargets.Count > 1)
                events?.Report(new OrchestratorEvent($"=== Building {resolved} ===", OrchestratorEventLevel.Info));

            var platform = resolved.Get<TargetPlatform>();
            var configuration = resolved.Get<BuildConfiguration>();
            var suffix = string.Join("_", resolved.Values
                .Where(kv => varyingDimensions.Contains(kv.Key))
                .OrderBy(kv => kv.Key.Name)
                .Select(kv => kv.Value.ToString()));
            var dirName = suffix.Length > 0 ? $"{platform}_{configuration}_{suffix}" : $"{platform}_{configuration}";

            var context = new BuildContext
            {
                Platform = platform,
                Architecture = resolved.Get<TargetArchitecture>(),
                Configuration = configuration,
                AdditionalDimensions = resolved.Values
                    .Where(kv => kv.Key != typeof(TargetPlatform) && kv.Key != typeof(TargetArchitecture) && kv.Key != typeof(BuildConfiguration))
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                ProjectRoot = workingDir,
                IntermediateDirectory = Path.Combine(workingDir, "Intermediate", dirName),
                OutputDirectory = Path.Combine(workingDir, "Binaries", dirName),
                ParallelJobs = request.Jobs ?? Environment.ProcessorCount,
                CachedOptionValues = cachedOptionValues
            };

            // An unavailable SDK/toolchain for ONE combination in a multi-combination build must
            // not abort the others - the same "one combination's problem doesn't stop the rest"
            // principle already applies to an actual build failure (see the
            // BuildAsync_OneCombinationFails_OthersStillRunAndAggregateReflectsBoth test below).
            // A dev machine typically has only one or two platform toolchains installed even when
            // a target's declared matrix spans more; skip that combination with a warning and keep
            // going, rather than making the whole multi-platform build fail because of a platform
            // nobody asked to build for right now. For a target WITHOUT a declared matrix
            // (resolvedTargets.Count == 1), this is unchanged from today: a missing SDK/toolchain
            // for the one combination being built is a hard error.
            var sdk = PlatformFactory.GetSDK(context.Platform);
            if (sdk == null)
            {
                if (resolvedTargets.Count > 1)
                {
                    events?.Report(new OrchestratorEvent($"Skipping {resolved}: no SDK found for platform {context.Platform}.", OrchestratorEventLevel.Warning));
                    continue;
                }
                events?.Report(new OrchestratorEvent($"No SDK found for platform {context.Platform}", OrchestratorEventLevel.Error));
                return null;
            }
            var toolchain = PlatformFactory.CreateToolchain(context.Platform, context.Architecture);
            if (toolchain == null)
            {
                if (resolvedTargets.Count > 1)
                {
                    events?.Report(new OrchestratorEvent($"Skipping {resolved}: could not create toolchain for {context.Platform}/{context.Architecture}.", OrchestratorEventLevel.Warning));
                    continue;
                }
                events?.Report(new OrchestratorEvent($"Could not create toolchain for {context.Platform}/{context.Architecture}", OrchestratorEventLevel.Error));
                return null;
            }

            var targets = compiledRules.CreateTargetRules(context);
            var targetRules = targets.FirstOrDefault();
            if (targetRules == null)
            {
                events?.Report(new OrchestratorEvent("No target found.", OrchestratorEventLevel.Error));
                return null;
            }

            var modules = compiledRules.CreateModuleRules(context);
            // Gating is always silent, never an error - even when the gated-out module is the
            // one LaunchModuleName names. Most real dependencies are referenced unconditionally
            // (ExtraModules.Add with no platform check), so a module reference is never a
            // promise that the module builds everywhere; a combination left with zero included
            // modules this way falls through to the existing "no modules to build" warning path
            // a few lines below, exactly as it already does for a target with no modules at all.
            var includedModules = modules.Where(m => IsModuleIncluded(compiledRules, m, resolved)).ToList();

            try
            {
                LayeringValidator.Validate(includedModules);
            }
            catch (LayeringViolationException ex)
            {
                events?.Report(new OrchestratorEvent($"Layering violation: {ex.Message}", OrchestratorEventLevel.Error));
                return null;
            }

            events?.Report(new OrchestratorEvent($"Found 1 target(s), {includedModules.Count} module(s)", OrchestratorEventLevel.Info));

            if (includedModules.Count == 0)
            {
                events?.Report(new OrchestratorEvent("No modules to build.", OrchestratorEventLevel.Warning));
                results.Add(ZeroActionResult());
                continue;
            }

            var combinationResult = await BuildOneCombinationAsync(context, targetRules, includedModules, toolchain, request.Jobs, events, buildProgress, ct);
            if (combinationResult == null)
                return null;
            results.Add(combinationResult);
        }

        if (results.Count == 0)
        {
            // Every resolved combination was skipped (unavailable SDK/toolchain on this
            // machine) - nothing was actually buildable, which is a real error, not a vacuous
            // success over zero results.
            events?.Report(new OrchestratorEvent("No resolved combination had an available SDK/toolchain on this machine.", OrchestratorEventLevel.Error));
            return null;
        }

        return AggregateResults(results);
    }

    private async Task<BuildResult?> BuildOneCombinationAsync(
        BuildContext context,
        TargetRules targetRules,
        IReadOnlyList<ModuleRules> modules,
        IToolchain toolchain,
        int? jobs,
        IProgress<OrchestratorEvent>? events,
        IProgress<BuildProgress>? buildProgress,
        CancellationToken ct)
    {
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
        var executor = new ParallelExecutor(jobs, actionCache);

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

    private static bool IsModuleIncluded(CompiledRules compiledRules, ModuleRules module, ResolvedTarget resolved)
    {
        var moduleMatrix = compiledRules.GetSupportedTargets(module.GetType());
        if (moduleMatrix == null) return true; // no gate declared - participates in everything, as today.

        return moduleMatrix.Resolve().Any(candidate =>
            candidate.Values.All(kv => resolved.Values.TryGetValue(kv.Key, out var value) && Equals(value, kv.Value)));
    }

    private static bool MatchesFilter(ResolvedTarget resolved, BuildOrchestratorRequest request)
    {
        if (request.Platform is { } p && (!resolved.TryGet<TargetPlatform>(out var rp) || rp != p)) return false;
        if (request.Architecture is { } a && (!resolved.TryGet<TargetArchitecture>(out var ra) || ra != a)) return false;
        if (request.Configuration is { } c && (!resolved.TryGet<BuildConfiguration>(out var rc) || rc != c)) return false;
        if (request.DimensionFilter == null) return true;

        foreach (var (dimensionType, allowedValues) in request.DimensionFilter)
        {
            if (resolved.Values.TryGetValue(dimensionType, out var value) && !allowedValues.Contains(value))
                return false;
        }
        return true;
    }

    private static BuildResult AggregateResults(IReadOnlyList<BuildResult> results) => results.Count == 1
        ? results[0]
        : new BuildResult
        {
            Success = results.All(r => r.Success),
            TotalDuration = TimeSpan.FromTicks(results.Sum(r => r.TotalDuration.Ticks)),
            TotalActions = results.Sum(r => r.TotalActions),
            SuccessfulActions = results.Sum(r => r.SuccessfulActions),
            FailedActions = results.Sum(r => r.FailedActions),
            SkippedActions = results.Sum(r => r.SkippedActions),
            CachedActions = results.Sum(r => r.CachedActions),
            ActionResults = results.SelectMany(r => r.ActionResults).ToList(),
            OutputFiles = results.SelectMany(r => r.OutputFiles).ToList()
        };

    private static TargetPlatform DefaultPlatformForHostOS()
    {
        if (OperatingSystem.IsWindows()) return TargetPlatform.Windows;
        if (OperatingSystem.IsLinux()) return TargetPlatform.Linux;
        if (OperatingSystem.IsMacOS()) return TargetPlatform.iOS;
        if (OperatingSystem.IsFreeBSD()) return TargetPlatform.FreeBSD;
        return TargetPlatform.Windows;
    }

    private static TargetArchitecture DefaultArchitectureForHostOS() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
            System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
            System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.ARM64,
            _ => TargetArchitecture.X64
        };

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
