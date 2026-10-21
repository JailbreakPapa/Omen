// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Implementations;
using Omen.Core.Interfaces;
using Omen.Core.Options;
using Omen.Core.Rules;
using Omen.Distributed.Cache;
using Omen.Executors.Server;
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

    /// <summary>GUI override for unity builds. Null leaves the target's own rules in charge.</summary>
    public bool? UseUnityBuild { get; init; }

    /// <summary>GUI override for precompiled headers. Null leaves the target's own rules in charge.</summary>
    public bool? UsePrecompiledHeaders { get; init; }

    /// <summary>
    /// Override for link-time optimization. Null leaves the target's own
    /// <see cref="TargetRules.EnableLTO"/> in charge, same as the unity/PCH overrides above.
    /// </summary>
    public bool? EnableLinkTimeOptimization { get; init; }

    /// <summary>OmenNet coordinator address. Null disables distributed builds for this request.</summary>
    public string? CoordinatorAddress { get; init; }

    /// <summary>
    /// GUI/CLI override for which plugins are enabled (see <see cref="OmenProjectRules.EnabledPlugins"/>
    /// and <see cref="PluginEnablement"/>). Null defers to the compiled project's own
    /// OmenProjectRules.EnabledPlugins; if that is also null, every plugin stays active - unchanged
    /// from before plugin enablement existed. Set this to filter a one-off build without editing the
    /// project definition, the same way UseUnityBuild/UsePrecompiledHeaders override their
    /// project-declared defaults above.
    /// </summary>
    public IReadOnlyList<string>? EnabledPlugins { get; init; }

    /// <summary>
    /// GUI/CLI override for <see cref="OmenProjectRules.Variant"/>. Only consulted when an
    /// enabled-plugins list (from this request or the project) is actually in effect.
    /// </summary>
    public PluginVariant? Variant { get; init; }
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
    private readonly BuildServices _services;

    public BuildOrchestrator(BuildServices? services = null) => _services = services ?? new BuildServices();

    /// <summary>
    /// Holds the intermediate directory for one build. The handle is exclusive and deleted on close,
    /// so a killed build releases it with its process and no stale lock file is left behind.
    /// </summary>
    private FileStream? TryAcquireBuildLock(BuildContext context)
    {
        // A build server owns this configuration's intermediate directories for its whole life; an in-process
        // build waits for it to exit (or goes through it: omen build without --no-server).
        if (!_services.CallerHoldsIntermediateLock && ServerHolds(context.ProjectRoot, context.Configuration.ToString()))
            return null;
        var dir = Path.Combine(context.IntermediateDirectory, ".buildtool");
        Directory.CreateDirectory(dir);
        try
        {
            return new FileStream(Path.Combine(dir, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>No in-process build holds this directory's build.lock (they hold it exclusively).</summary>
    private static bool BuildLockFree(BuildContext context)
    {
        var path = Path.Combine(context.IntermediateDirectory, ".buildtool", "build.lock");
        if (!File.Exists(path))
            return true;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string ServerLockPath(string projectRoot, string configuration) =>
        Path.Combine(projectRoot, "Intermediate", $".omen-server-{configuration}.lock");

    /// <summary>One build server per project root and configuration: held for the server's lifetime.</summary>
    internal static FileStream? TryAcquireServerLock(string projectRoot, string configuration)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "Intermediate"));
        try
        {
            return new FileStream(ServerLockPath(projectRoot, configuration), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool ServerHolds(string projectRoot, string configuration)
    {
        var path = ServerLockPath(projectRoot, configuration);
        if (!File.Exists(path))
            return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public async Task<BuildResult?> BuildAsync(
        BuildOrchestratorRequest request,
        IProgress<OrchestratorEvent>? events,
        IProgress<BuildProgress>? buildProgress,
        CancellationToken ct = default)
    {
        var workingDir = RuleCompiler.ResolveProjectRoot(request.TargetFile);
        var optionCacheStore = new OptionCacheStore(Path.Combine(workingDir, "Intermediate", "omen-cache.json"));
        var cachedOptionValues = optionCacheStore.Load();

        // Phase timings, reported as they complete - "which part" is the first question every
        // time a build feels slow, and at engine scale the parts are seconds each.
        var phaseClock = System.Diagnostics.Stopwatch.StartNew();
        void Phase(string name)
        {
            events?.Report(new OrchestratorEvent($"  {name}: {phaseClock.Elapsed.TotalSeconds:F1}s", OrchestratorEventLevel.Info));
            phaseClock.Restart();
        }

        var ruleCompiler = new RuleCompiler(RuleCompiler.CacheDirectoryFor(workingDir));
        CompiledRules compiledRules;
        try
        {
            events?.Report(new OrchestratorEvent("Compiling build rules...", OrchestratorEventLevel.Info));
            compiledRules = _services.Warm is { } warm
                ? await warm.GetCompiledRulesAsync(workingDir, () => ruleCompiler.CompileRulesAsync(workingDir, ct))
                : await ruleCompiler.CompileRulesAsync(workingDir, ct);
            Phase("Rule compilation");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            events?.Report(new OrchestratorEvent($"Error compiling rules: {ex.Message}", OrchestratorEventLevel.Error));
            return null;
        }

        // Which target, specifically. The request names a target FILE; the compiled assembly may
        // hold dozens of TargetRules types from all over the tree, and building "whichever one
        // reflection returned first" is not a build anybody asked for.
        var targetCandidates = compiledRules.FindTargetTypes(request.TargetFile);
        if (targetCandidates.Count == 0)
        {
            var elsewhere = compiledRules.FindTargetTypes();
            events?.Report(new OrchestratorEvent(
                elsewhere.Count == 0
                    ? "No target found."
                    : $"'{Path.GetFileName(request.TargetFile)}' declares no TargetRules subclass. " +
                      $"Targets found elsewhere in this project: {string.Join(", ", elsewhere.Select(t => t.Name))}.",
                OrchestratorEventLevel.Error));
            return null;
        }

        if (targetCandidates.Count > 1)
        {
            events?.Report(new OrchestratorEvent(
                $"'{Path.GetFileName(request.TargetFile)}' declares {targetCandidates.Count} targets " +
                $"({string.Join(", ", targetCandidates.Select(t => t.Name))}) - split them into one file each.",
                OrchestratorEventLevel.Error));
            return null;
        }

        var targetType = targetCandidates[0];

        // Null here means "no filter at all" - every plugin stays active, unchanged from before
        // PluginEnablement existed. Only an explicit list (from this request or the compiled
        // project's own OmenProjectRules.EnabledPlugins) turns filtering on; an empty list is a
        // valid, deliberate "enable nothing", not the same as null.
        var enabledPlugins = request.EnabledPlugins ?? compiledRules.Project?.EnabledPlugins;
        var variant = request.Variant ?? compiledRules.Project?.Variant ?? PluginVariant.Unified;

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
                // Request first, then the project's own declared default (its options feed
                // these), then the built-in default.
                ParallelJobs = request.Jobs ?? compiledRules.Project?.MaxParallelActions ?? Environment.ProcessorCount,
                UseUnityBuild = request.UseUnityBuild ?? compiledRules.Project?.UnityBuild ?? true,
                UsePCH = request.UsePrecompiledHeaders ?? compiledRules.Project?.PrecompiledHeaders ?? true,
                UseDistributedBuild = !string.IsNullOrWhiteSpace(request.CoordinatorAddress),
                CoordinatorAddress = request.CoordinatorAddress,
                CachedOptionValues = cachedOptionValues
            };

            // The event log is what Omen.GUI's Build Progress window tails. Open it before the
            // lock wait so "waiting for another build" is the first thing a watcher sees.
            using var eventLog = BuildEventLog.TryCreate(
                context.IntermediateDirectory, Environment.ProcessId, DateTimeOffset.Now,
                message => events?.Report(new OrchestratorEvent(message, OrchestratorEventLevel.Warning)));
            eventLog?.BuildStarted(request.TargetFile, platform.ToString(), context.Architecture.ToString(), configuration.ToString(), context.ParallelJobs);
            var combinationEvents = eventLog?.WrapEvents(events) ?? events;
            var combinationProgress = eventLog?.WrapProgress(buildProgress) ?? buildProgress;
            // Two builds sharing one intermediate directory corrupt each other's outputs (observed: a
            // half-written .obj linked by both, and codegen inputs locked mid-write). Hold the
            // directory for this build's lifetime. A second build WAITS rather than failing:
            // Visual Studio's "Build Solution" starts every launch project's `omen build` in
            // parallel, and failing all but one would fail the solution build every time. This
            // lock is what lets the generated solution drop the synthetic dependency chain that
            // used to serialize those projects - and made F5 on one tool build every other.
            FileStream? buildLock = null;
            if (_services.CallerHoldsIntermediateLock)
            {
                // A build server's builds share the directory with each other (its scheduler keeps them off each
                // other's files), so they only look - in shared mode, never blocking one another - for an
                // in-process build that took the lock before the server started.
                if (!BuildLockFree(context))
                {
                    combinationEvents?.Report(new OrchestratorEvent(
                        $"An in-process Omen build holds '{context.IntermediateDirectory}' - waiting for it to finish.",
                        OrchestratorEventLevel.Info));
                    while (!BuildLockFree(context))
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            else
            {
                buildLock = TryAcquireBuildLock(context);
                if (buildLock == null)
                {
                    combinationEvents?.Report(new OrchestratorEvent(
                        $"Another Omen build holds '{context.IntermediateDirectory}' - waiting for it to finish (two builds into one intermediate directory corrupt each other's outputs).",
                        OrchestratorEventLevel.Info));
                    while (buildLock == null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                        buildLock = TryAcquireBuildLock(context);
                    }
                }
            }
            using var _ = buildLock;

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
                    combinationEvents?.Report(new OrchestratorEvent($"Skipping {resolved}: no SDK found for platform {context.Platform}.", OrchestratorEventLevel.Warning));
                    continue;
                }
                combinationEvents?.Report(new OrchestratorEvent($"No SDK found for platform {context.Platform}", OrchestratorEventLevel.Error));
                return null;
            }
            var toolchain = PlatformFactory.CreateToolchain(context.Platform, context.Architecture);
            if (toolchain == null)
            {
                if (resolvedTargets.Count > 1)
                {
                    combinationEvents?.Report(new OrchestratorEvent($"Skipping {resolved}: could not create toolchain for {context.Platform}/{context.Architecture}.", OrchestratorEventLevel.Warning));
                    continue;
                }
                combinationEvents?.Report(new OrchestratorEvent($"Could not create toolchain for {context.Platform}/{context.Architecture}", OrchestratorEventLevel.Error));
                return null;
            }

            // PAL traits for this platform, so RequiredTraits gates actually decide something.
            // Set before any rule is instantiated: CreateModuleRules/CreatePluginRules consult
            // TraitGating while expanding, so a table arriving later would change nothing.
            if (compiledRules.Project != null &&
                compiledRules.Project.PlatformTraits.TryGetValue(context.Platform, out var platformTraits))
            {
                context.PlatformTraits = platformTraits;
            }

            // Engine-wide compiler/linker configuration - O3DE's
            // cmake/Platform/Common/<Family>/Configurations_*.cmake, which applies to every
            // target in the build and lives in no target's own rules. Resolved for this
            // toolchain's compiler family and this combination's configuration.
            GlobalToolchainOptions.ApplyTo(context, compiledRules.Project, toolchain.CompilerFamily);

            // A build server keeps this per target/combination; everything in the key is what the
            // instantiation below reads.
            var closureKey = $"{workingDir}|{targetType.FullName}|{resolved}|{string.Join(",", enabledPlugins ?? [])}|{variant}|{request.EnableLinkTimeOptimization}";
            TargetRules? targetRules;
            ModuleClosureResult closure;
            var warmClosure = _services.Warm?.GetClosure(closureKey);
            if (warmClosure != null)
            {
                targetRules = warmClosure.TargetRules;
                closure = warmClosure.Closure;
            }
            else
            {
                // Rule constructors may write into the intermediate directory (AddAutoGen's input list and
                // dry run), so a build server's concurrent builds instantiate them one at a time.
                var rulesLock = _services.Warm?.GraphLock(context.IntermediateDirectory);
                if (rulesLock != null)
                    await rulesLock.WaitAsync(ct);
                try
                {
                    // The instance of the type selected above, not just the first one created - same
                    // reason.
                    targetRules = compiledRules.CreateTargetRules(context).FirstOrDefault(t => t.GetType() == targetType);
                    if (targetRules == null)
                    {
                        combinationEvents?.Report(new OrchestratorEvent($"'{targetType.Name}' could not be instantiated.", OrchestratorEventLevel.Error));
                        return null;
                    }

                    if (request.EnableLinkTimeOptimization is { } lto)
                        targetRules.EnableLTO = lto;

                    // A project-wide link type (CMake's LY_MONOLITHIC_GAME) applies to every target.
                    if (compiledRules.Project?.LinkTypeOverride is { } projectLinkType)
                        targetRules.LinkType = projectLinkType;

                    var modules = compiledRules.CreateModuleRules(context);
                    // Gating is always silent, never an error - even when the gated-out module is the
                    // one LaunchModuleName names. Most real dependencies are referenced unconditionally
                    // (ExtraModules.Add with no platform check), so a module reference is never a
                    // promise that the module builds everywhere; a combination left with zero included
                    // modules this way falls through to the existing "no modules to build" warning path
                    // a few lines below, exactly as it already does for a target with no modules at all.
                    var candidateModules = modules.Where(m => IsModuleIncluded(compiledRules, m, resolved)).ToList();
                    var pluginRules = compiledRules.CreatePluginRules(context);
                    candidateModules = PluginEnablement.FilterEnabledModules(
                        candidateModules, enabledPlugins, variant, pluginRules).ToList();

                    // Only what this target can actually reach. Everything below - the duplicate check,
                    // layering validation, the unresolved-dependency warnings, the graph itself - is
                    // about the target's own build, and running it over every module the scan roots
                    // happened to contain produced pages of noise about modules nobody asked to build.
                    closure = ModuleClosure.Compute(
                        targetRules,
                        candidateModules,
                        pluginRules,
                        enabledPlugins == null ? null : PluginEnablement.ResolveEnabledTargets(enabledPlugins, variant, pluginRules));
                    _services.Warm?.SetClosure(closureKey, new ClosureEntry(targetRules, closure));
                }
                finally
                {
                    rulesLock?.Release();
                }
            }
            Phase(warmClosure != null ? "Rule instantiation and closure (reused)" : "Rule instantiation and closure");

            if (closure.UnresolvedSeeds.Count > 0)
            {
                // A seed is what the target explicitly asked for by name. Naming nothing is a
                // typo or a stale rule file, not something to build around.
                combinationEvents?.Report(new OrchestratorEvent(
                    $"'{targetRules.Name}' names {string.Join(", ", closure.UnresolvedSeeds.Select(s => $"'{s}'"))} " +
                    "in LaunchModuleName/ExtraModules, which match no module in this build.",
                    OrchestratorEventLevel.Error));
                return null;
            }

            var includedModules = closure.Modules.ToList();

            // Written every build, before anything can fail. The console cannot be relied on
            // for this: orchestrator events render inside a transient Spectre status context
            // that is erased when an exception unwinds, so the closure disappears exactly when
            // a failure makes you want it. A file survives.
            WriteClosureReport(context, targetRules, closure, combinationEvents);

            combinationEvents?.Report(new OrchestratorEvent(
                closure.WasUnfiltered
                    ? $"Found 1 target(s), {includedModules.Count} module(s)"
                    : $"Found 1 target(s), {includedModules.Count} module(s) in {targetRules.Name}'s dependency closure",
                OrchestratorEventLevel.Info));

            ReportUnresolvedDependencies(includedModules, combinationEvents);
            ReportMissingIncludePaths(context, includedModules, combinationEvents);

            // Two modules sharing a name is fatal downstream (LayeringValidator and the graph
            // builder both index modules by name and threw a bare "An item with the same key
            // has already been added"). Say which name, and where each one came from.
            var duplicates = includedModules
                .GroupBy(m => m.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();
            if (duplicates.Count > 0)
            {
                foreach (var duplicate in duplicates)
                {
                    combinationEvents?.Report(new OrchestratorEvent(
                        $"Module '{duplicate.Key}' is defined {duplicate.Count()} times: " +
                        string.Join(", ", duplicate.Select(m => m.SourceDirectory ?? "(no source directory)")),
                        OrchestratorEventLevel.Error));
                }
                return null;
            }

            try
            {
                LayeringValidator.Validate(includedModules);
            }
            catch (LayeringViolationException ex)
            {
                combinationEvents?.Report(new OrchestratorEvent($"Layering violation: {ex.Message}", OrchestratorEventLevel.Error));
                return null;
            }

            if (includedModules.Count == 0)
            {
                combinationEvents?.Report(new OrchestratorEvent("No modules to build.", OrchestratorEventLevel.Warning));
                var zeroResult = ZeroActionResult();
                eventLog?.BuildFinished(zeroResult);
                results.Add(zeroResult);
                continue;
            }

            var combinationResult = await BuildOneCombinationAsync(context, targetRules, includedModules, toolchain, request.Jobs ?? compiledRules.Project?.MaxParallelActions, combinationEvents, combinationProgress, eventLog, closureKey, ct);
            if (combinationResult != null)
                eventLog?.BuildFinished(combinationResult);
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

    /// <summary>
    /// A dependency naming a module that is not in the build resolves to nothing: the graph
    /// builder's include-path walk and its link step both do a TryGetValue and quietly skip a
    /// miss. The compile then fails much later with "cannot open include file", pointing at a
    /// header rather than at the missing module - which is how a plugin built with the wrong
    /// project root (its rule files discovered, the engine's AZ::AzCore not) presents. Naming
    /// the unresolved dependency here turns that into one legible warning.
    /// </summary>
    private static void ReportUnresolvedDependencies(
        IReadOnlyList<ModuleRules> modules, IProgress<OrchestratorEvent>? events)
    {
        if (events == null)
            return;

        var known = modules.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            foreach (var dep in module.AllDependencies.Distinct())
            {
                if (!known.Contains(dep))
                {
                    events.Report(new OrchestratorEvent(
                        $"'{module.Name}' depends on '{dep}', which is not part of this build - " +
                        "its headers and libraries will be missing. Check that the module's rule " +
                        "file is under one of the project's SourceRoots/ExternalSubdirectories, " +
                        "and that it is not gated out for this platform/configuration.",
                        OrchestratorEventLevel.Warning));
                }
            }
        }
    }

    /// <summary>
    /// Warns about a declared include path that does not exist on disk.
    ///
    /// The compiler ignores one silently, so the failure surfaces much later as C1083 in whatever
    /// files needed a header from it - and it names the header, never the missing root. A stale
    /// path survived the Az* rename this way: NightFoxToolsFramework still declared
    /// PublicIncludePaths.Add("AzToolsFramework"), a directory that no longer existed, and the
    /// result was a hundred unresolved includes across unrelated files.
    ///
    /// Warning rather than failing, because a path can legitimately be generated later in the
    /// build, and because a rule shared across platforms may name a root only some of them have.
    /// </summary>
    private static void ReportMissingIncludePaths(
        BuildContext context, IReadOnlyList<ModuleRules> modules, IProgress<OrchestratorEvent>? events)
    {
        if (events == null)
            return;

        foreach (var module in modules)
        {
            var baseDirectory = Path.Combine(
                context.ProjectRoot, module.SourceDirectory ?? $"Source/{module.Name}");

            foreach (var declared in module.PublicIncludePaths
                         .Concat(module.PrivateIncludePaths)
                         .Concat(module.PublicExternalIncludePaths)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var resolved = Path.IsPathRooted(declared)
                    ? declared
                    : Path.Combine(baseDirectory, declared);

                if (!Directory.Exists(resolved))
                {
                    events.Report(new OrchestratorEvent(
                        $"'{module.Name}' declares include path '{declared}', which does not exist " +
                        $"({resolved}). The compiler ignores a missing include root silently, so " +
                        "anything that needed a header from it will fail with C1083 naming the " +
                        "header rather than this path.",
                        OrchestratorEventLevel.Warning));
                }
            }
        }
    }

    // Hands a build server's action graph back once the build using it is over, however it ends.
    private sealed class GraphReturn(BuildWarmState warm, string key, BuildWarmState.CachedGraph cached) : IDisposable
    {
        public void Dispose() => warm.ReturnGraph(key, cached);
    }

    private async Task<BuildResult?> BuildOneCombinationAsync(
        BuildContext context,
        TargetRules targetRules,
        IReadOnlyList<ModuleRules> modules,
        IToolchain toolchain,
        int? jobs,
        IProgress<OrchestratorEvent>? events,
        IProgress<BuildProgress>? buildProgress,
        BuildEventLog? eventLog,
        string? graphKey,
        CancellationToken ct)
    {
        // Pre-build steps run before the graph is even built: their whole point is often to
        // generate sources the graph then compiles.
        if (!await RunBuildStepsAsync(targetRules.PreBuildSteps, "pre-build", context, events, ct))
            return FailedStepResult();

        var digestCalculator = new Sha256DigestCalculator();
        // Header resolutions from the previous run, revalidated by file stamp - the bulk of a
        // no-op build's time was re-scanning headers that had not changed.
        var includeCachePath = Path.Combine(context.IntermediateDirectory, ".buildtool", "includes.json");
        var graphBuilder = new ActionGraphBuilder(context, toolchain, digestCalculator)
        {
            IncludeScanner = _services.Warm?.GetIncludeScanner(includeCachePath) ?? IncludeScanner.LoadFrom(includeCachePath)
        };
        var annotationCachePath = Path.Combine(context.IntermediateDirectory, ".buildtool", "reflect-annotations.json");
        ActionGraph graph;
        var graphClock = System.Diagnostics.Stopwatch.StartNew();
        var cachedGraph = graphKey == null ? null : _services.Warm?.TakeGraph(graphKey);
        var reused = cachedGraph != null;
        if (reused)
        {
            graph = cachedGraph.Graph;
        }
        else
        {
            var graphLock = _services.Warm?.GraphLock(context.IntermediateDirectory);
            if (graphLock != null)
                await graphLock.WaitAsync(ct);
            try
            {
                Omen.Core.Reflect.AnnotationCache.LoadFrom(annotationCachePath);
                graph = graphBuilder.Build(targetRules, modules);
                graphBuilder.IncludeScanner.SaveTo(includeCachePath);
                Omen.Core.Reflect.AnnotationCache.SaveTo(annotationCachePath);
            }
            finally
            {
                graphLock?.Release();
            }
            if (graphKey != null)
                cachedGraph = _services.Warm?.NewGraph(graph);
        }
        using var returnGraph = cachedGraph == null ? null : new GraphReturn(_services.Warm!, graphKey!, cachedGraph);
        events?.Report(new OrchestratorEvent($"  Action graph: {graphClock.Elapsed.TotalSeconds:F1}s{(reused ? " (reused)" : "")}", OrchestratorEventLevel.Info));
        graphClock.Restart();

        graph.Stamps = _services.Warm?.Stamps;
        var digestPath = Path.Combine(context.IntermediateDirectory, ".buildtool", "digests.json");
        var digestStore = _services.Warm?.GetDigestStore(digestPath) ?? new ActionDigestStore(digestPath);
        var skipped = graph.MarkUpToDateActionsAsSkipped(digestCalculator, digestStore);
        events?.Report(new OrchestratorEvent($"  Up-to-date check: {graphClock.Elapsed.TotalSeconds:F1}s", OrchestratorEventLevel.Info));
        graphClock.Restart();
        var rebuilds = graph.SummarizeRebuilds();
        eventLog?.GraphReady(graph.Actions.Count, skipped, rebuilds.ToRun > 0 ? rebuilds.ToString() : null);
        if (skipped > 0)
        {
            events?.Report(new OrchestratorEvent($"{skipped} action(s) already up to date (unchanged command line), skipped.", OrchestratorEventLevel.Info));
        }
        if (rebuilds.ToRun > 0)
        {
            events?.Report(new OrchestratorEvent(rebuilds.ToString(), OrchestratorEventLevel.Info));
        }

        events?.Report(new OrchestratorEvent($"Created action graph with {graph.Actions.Count} actions", OrchestratorEventLevel.Info));

        if (graph.Actions.Count == 0)
        {
            events?.Report(new OrchestratorEvent("Nothing to build - up to date!", OrchestratorEventLevel.Success));
            if (!await RunBuildStepsAsync(targetRules.PostBuildSteps, "post-build", context, events, ct))
                return FailedStepResult();
            return ZeroActionResult();
        }

        var actionCache = new LocalActionCache(Path.Combine(context.IntermediateDirectory, ".cache"));
        Func<BuildAction, bool> isUpToDate = action => graph.IsUpToDate(action, digestCalculator, digestStore);
        var executor = _services.ExecutorFactory?.Invoke(jobs ?? Environment.ProcessorCount, isUpToDate)
            ?? new ParallelExecutor(jobs, actionCache, isUpToDate);

        // Record each action's digest as it completes and flush periodically, so a build that is
        // killed part-way (the process torn down, not merely an action failing) keeps the work it
        // finished. The loop after ExecuteAsync stays as the final, complete flush.
        var result = await executor.ExecuteAsync(graph, new DigestRecordingProgress(buildProgress, digestStore, digestCalculator, _services.Warm), ct);
        events?.Report(new OrchestratorEvent($"  Execution: {graphClock.Elapsed.TotalSeconds:F1}s", OrchestratorEventLevel.Info));

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

        // Record every action that completed, whether or not the build as a whole did. A
        // completed compile is just as complete when a sibling later fails or the process is
        // killed - and this used to sit inside `if (result.Success)`, so one failed link (or a
        // SIGTERM at the last step) threw away the digests of the 2,454 actions before it and the
        // next build recompiled them all. Eight failed builds in one day each paid that in full.
        foreach (var action in graph.Actions.Where(a => a.Status is ActionStatus.Completed or ActionStatus.Skipped))
        {
            if (_services.Warm is { } warmState && action.Status == ActionStatus.Completed)
            {
                foreach (var output in action.Outputs)
                    warmState.OutputWritten(output.Path);
            }
            if (action.Outputs.Count == 0 || !File.Exists(action.Outputs[0].Path)) continue;
            digestStore.Set(action.Outputs[0].Path, action.ComputeDigest(digestCalculator), action.ComputeDigestParts(digestCalculator));
        }
        digestStore.Save();

        if (result.Success)
        {
            if (!await RunBuildStepsAsync(targetRules.PostBuildSteps, "post-build", context, events, ct))
                return FailedStepResult();

            events?.Report(new OrchestratorEvent("Build completed successfully!", OrchestratorEventLevel.Success));
        }
        else
        {
            events?.Report(new OrchestratorEvent("BUILD FAILED", OrchestratorEventLevel.Error));

            foreach (var actionResult in result.ActionResults.Where(r => !r.Success))
            {
                foreach (var evt in DescribeFailure(actionResult))
                    events?.Report(evt);
            }
        }

        return result;
    }

    /// <summary>
    /// Runs a target's Pre/PostBuildSteps through the platform shell (cmd /c on Windows,
    /// /bin/sh -c elsewhere). Returns false when a step fails and doesn't declare
    /// ContinueOnError - the build stops there, like a failed add_custom_command.
    /// </summary>
    private static async Task<bool> RunBuildStepsAsync(
        IReadOnlyList<BuildStep> steps,
        string phase,
        BuildContext context,
        IProgress<OrchestratorEvent>? events,
        CancellationToken ct)
    {
        foreach (var step in steps)
        {
            events?.Report(new OrchestratorEvent($"Running {phase} step: {step.Description}", OrchestratorEventLevel.Info));

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? $"/c {step.Command}" : $"-c \"{step.Command}\"",
                WorkingDirectory = step.WorkingDirectory ?? context.ProjectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var (key, value) in step.Environment)
                startInfo.Environment[key] = value;

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                events?.Report(new OrchestratorEvent($"{phase} step '{step.Description}' failed to start.", OrchestratorEventLevel.Error));
                if (!step.ContinueOnError) return false;
                continue;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                events?.Report(new OrchestratorEvent(
                    $"{phase} step '{step.Description}' exited with code {process.ExitCode}.",
                    step.ContinueOnError ? OrchestratorEventLevel.Warning : OrchestratorEventLevel.Error));
                if (!string.IsNullOrWhiteSpace(stdout))
                    events?.Report(new OrchestratorEvent(stdout.Trim(), OrchestratorEventLevel.Info));
                if (!string.IsNullOrWhiteSpace(stderr))
                    events?.Report(new OrchestratorEvent(stderr.Trim(), OrchestratorEventLevel.Error));
                if (!step.ContinueOnError) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A failed action's output verbatim, so each compiler/linker diagnostic starts its own line in the
    /// `file(line,col): error CODE: text` form Visual Studio's Error List picks up; then how it was run.
    /// </summary>
    public static IEnumerable<OrchestratorEvent> DescribeFailure(ActionResult actionResult)
    {
        yield return new OrchestratorEvent($"Failed: {actionResult.Action.Description}", OrchestratorEventLevel.Error);
        foreach (var output in new[] { actionResult.StandardOutput, actionResult.StandardError })
        {
            if (!string.IsNullOrWhiteSpace(output))
                yield return new OrchestratorEvent(output.TrimEnd(), OrchestratorEventLevel.Error);
        }
        yield return new OrchestratorEvent($"Exit code {actionResult.ExitCode}: {actionResult.Action.CommandLine}", OrchestratorEventLevel.Info);
    }

    private static BuildResult FailedStepResult() => new()
    {
        Success = false,
        TotalDuration = TimeSpan.Zero,
        TotalActions = 0,
        SuccessfulActions = 0,
        FailedActions = 1,
        SkippedActions = 0,
        CachedActions = 0
    };

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
        if (OperatingSystem.IsMacOS()) return TargetPlatform.Mac;
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

    /// <summary>
    /// Write the target's module closure, and how each module was reached, to
    /// &lt;IntermediateDirectory&gt;/closure.txt.
    ///
    /// "What is actually in this build, and why is this module here?" is otherwise
    /// unanswerable - a module nothing appears to reference can arrive through several hops of
    /// dependencies and runtime dependencies, and reading the rule files does not show it.
    /// </summary>
    private static void WriteClosureReport(
        BuildContext context,
        TargetRules target,
        ModuleClosureResult closure,
        IProgress<OrchestratorEvent>? events)
    {
        try
        {
            Directory.CreateDirectory(context.IntermediateDirectory);
            var path = Path.Combine(context.IntermediateDirectory, "closure.txt");

            var report = new System.Text.StringBuilder();
            report.AppendLine($"Target:        {target.Name}");
            report.AppendLine($"Configuration: {context.Configuration} / {context.Platform} / {context.Architecture}");
            report.AppendLine($"Modules:       {closure.Modules.Count}{(closure.WasUnfiltered ? " (unfiltered - the target declared no seeds)" : string.Empty)}");

            if (closure.UnresolvedSeeds.Count > 0)
            {
                report.AppendLine($"Unresolved seeds: {string.Join(", ", closure.UnresolvedSeeds)}");
            }

            report.AppendLine();
            report.AppendLine("Module <- what pulled it in, back to a seed:");
            report.AppendLine();

            foreach (var module in closure.Modules.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var path2 = closure.PathToSeed(module.Name);
                report.AppendLine(path2.Count == 1
                    ? $"  {module.Name}  (seed)"
                    : $"  {string.Join(" <- ", path2)}");
            }

            File.WriteAllText(path, report.ToString());

            events?.Report(new OrchestratorEvent(
                $"Closure written to {path}", OrchestratorEventLevel.Info));
        }
        catch (IOException exception)
        {
            // Diagnostics must never be the reason a build fails.
            events?.Report(new OrchestratorEvent(
                $"Could not write the closure report: {exception.Message}", OrchestratorEventLevel.Warning));
        }
    }
}

// ponytail: flushes every 25 completions - a kill loses at most 25 actions' digests; switch to a
// time-based flush if a build of slow actions ever shows that matters.
file sealed class DigestRecordingProgress(IProgress<BuildProgress>? inner, ActionDigestStore store, IDigestCalculator calculator, BuildWarmState? warm)
    : IProgress<BuildProgress>
{
    private int _recorded;

    public void Report(BuildProgress value)
    {
        var action = value.LastResult is { Success: true } result ? result.Action : null;
        if (action is not null && warm is not null)
        {
            foreach (var output in action.Outputs)
                warm.OutputWritten(output.Path);
        }
        if (action is not null && action.Outputs.Count > 0 && File.Exists(action.Outputs[0].Path))
        {
            store.Set(action.Outputs[0].Path, action.ComputeDigest(calculator), action.ComputeDigestParts(calculator));
            if (Interlocked.Increment(ref _recorded) % 25 == 0)
                store.Save();
        }
        inner?.Report(value);
    }
}
