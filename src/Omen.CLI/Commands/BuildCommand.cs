// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Omen.Core.Configuration;
using Omen.Core.Diagnostics;
using Omen.Core.Graph;
using Omen.Core.Implementations;
using Omen.Core.Interfaces;
using Omen.Core.Rules;
using Omen.Executors;
using Omen.Executors.Orchestration;
using Omen.Executors.Server;
using Omen.Platforms;
using Spectre.Console;

namespace Omen.CLI.Commands;

/// <summary>
/// Provides the `build` command for the CLI, which builds targets and modules.
/// </summary>
public static class BuildCommand
{
    /// <summary>
    /// Creates the `build` command with options and handler.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("build", "Build a target or module");
        
        var targetArgument = new Argument<string?>(
            "target",
            () => null,
            "The target to build (uses .target.cs if not specified)");
        
        var platformOption = new Option<string?>(
            ["--platform", "-p"],
            "Target platform (Windows, Linux, FreeBSD, Android, iOS)");
        
        var configOption = new Option<string?>(
            ["--configuration", "-c"],
            "Build configuration - omitted means unconstrained across a declared matrix, or Development for a target without one (Debug, Development, Release, Shipping)");
        
        var archOption = new Option<string?>(
            ["--arch", "-a"],
            "Target architecture (x64, x86, ARM64)");
        
        var jobsOption = new Option<int?>(
            ["--jobs", "-j"],
            "Maximum parallel jobs");
        
        var cleanOption = new Option<bool>(
            "--clean",
            "Clean before building");
        
        var distributeOption = new Option<bool>(
            ["--distribute", "-d"],
            "Use distributed build");
        
        var coordinatorOption = new Option<string?>(
            "--coordinator",
            "Coordinator address for distributed builds");

        var dryRunOption = new Option<bool>(
            "--dry-run",
            "Print derived command lines without executing the build");

        var dimOption = new Option<string[]>(
            "--dim",
            () => [],
            "Filter a custom dimension, e.g. --dim RenderApi=DirectX12 (repeatable)");

        var noServerOption = new Option<bool>(
            "--no-server",
            "Build in this process instead of through the background build server (also: OMEN_NO_SERVER=1)");

        var backgroundOption = new Option<bool>(
            "--background",
            "Lower priority on a shared build server - for agent and scripted builds");

        command.AddArgument(targetArgument);
        command.AddOption(platformOption);
        command.AddOption(configOption);
        command.AddOption(archOption);
        command.AddOption(jobsOption);
        command.AddOption(cleanOption);
        command.AddOption(distributeOption);
        command.AddOption(coordinatorOption);
        command.AddOption(dryRunOption);
        command.AddOption(dimOption);
        command.AddOption(noServerOption);
        command.AddOption(backgroundOption);

        command.SetHandler(async (context) =>
        {
            var target = context.ParseResult.GetValueForArgument(targetArgument);
            var platform = context.ParseResult.GetValueForOption(platformOption);
            var config = context.ParseResult.GetValueForOption(configOption);
            var arch = context.ParseResult.GetValueForOption(archOption);
            var jobs = context.ParseResult.GetValueForOption(jobsOption);
            var clean = context.ParseResult.GetValueForOption(cleanOption);
            var distribute = context.ParseResult.GetValueForOption(distributeOption);
            var coordinator = context.ParseResult.GetValueForOption(coordinatorOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var dim = context.ParseResult.GetValueForOption(dimOption) ?? [];
            var noServer = context.ParseResult.GetValueForOption(noServerOption);
            var background = context.ParseResult.GetValueForOption(backgroundOption);

            context.ExitCode = await ExecuteBuildAsync(
                target, platform, config, arch, jobs, clean, distribute, coordinator, dryRun, dim, noServer, background);
        });
        
        return command;
    }
    
    /// <summary>Internal so RebuildCommand's clean-then-build actually builds.</summary>
    internal static async Task<int> ExecuteBuildAsync(
        string? target,
        string? platform,
        string? configuration,
        string? arch,
        int? jobs,
        bool clean,
        bool distribute,
        string? coordinator,
        bool dryRun,
        string[] dim,
        bool noServer = false,
        bool background = false)
    {
        var stopwatch = Stopwatch.StartNew();

        AnsiConsole.Write(new FigletText("OMEN").Color(Color.Orange1));
        AnsiConsole.MarkupLine("[bold]Omen Build System[/]\n");

        var (targetFile, discoveredTargetFiles) = await ResolveTargetFileAsync(target);
        if (targetFile == null)
        {
            var available = discoveredTargetFiles
                .Select(path => Path.GetFileName(path).Replace(".target.cs", string.Empty))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!string.IsNullOrEmpty(target))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No target named '{target}'.");
                AnsiConsole.MarkupLine(
                    "A module name is not a target name - only a *.target.cs file can be built.");
            }
            else if (available.Count > 1)
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] Several targets found and none named. Pass one.");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] No target file found. Create a .target.cs file or specify a target.");
            }

            if (available.Count > 0)
            {
                AnsiConsole.MarkupLine($"[dim]Available:[/] {string.Join(", ", available)}");
            }

            return 1;
        }

        if (!TryParsePlatform(platform, out var targetPlatform))
        {
            if (IsKnownUnsupportedPlatform(platform))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning:[/] '{platform}' is a platform O3DE's CMake build knows about " +
                    "but Omen does not target yet - no toolchain is wired up, so there is nothing to " +
                    "build against. Use a supported platform, or keep using CMake for this one.");
                return 1;
            }

            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid platform '{platform}'");
            return 1;
        }

        var targetArch = ParseArchitecture(arch);
        var buildConfig = ParseConfiguration(configuration);

        // --clean was parsed and then never read, so a "clean" build reused every stale object.
        if (clean && !dryRun && await CleanCommand.ExecuteCleanAsync(target, platform, configuration,
                projectRoot: RuleCompiler.ResolveProjectRoot(targetFile)) is var cleanResult and not 0)
            return cleanResult;

        AnsiConsole.MarkupLine($"[blue]Target:[/] {Path.GetFileName(targetFile)}");
        AnsiConsole.MarkupLine($"[blue]Platform:[/] {(targetPlatform?.ToString() ?? "(matrix default)")}");
        AnsiConsole.MarkupLine($"[blue]Architecture:[/] {(targetArch?.ToString() ?? "(matrix default)")}");
        AnsiConsole.MarkupLine($"[blue]Configuration:[/] {(buildConfig?.ToString() ?? "(matrix default)")}");
        AnsiConsole.WriteLine();

        if (dryRun)
        {
            return await ExecuteDryRunAsync(
                targetFile,
                targetPlatform ?? DefaultPlatformForHostOS(),
                targetArch ?? DefaultArchitectureForHostOS(),
                buildConfig ?? BuildConfiguration.Development,
                jobs);
        }

        IReadOnlyDictionary<Type, IReadOnlyList<Enum>>? dimensionFilter = null;
        if (dim.Length > 0)
        {
            var lookupRoot = RuleCompiler.ResolveProjectRoot(targetFile);
            var lookupCompiler = new RuleCompiler(RuleCompiler.CacheDirectoryFor(lookupRoot));
            var lookupRules = await lookupCompiler.CompileRulesAsync(lookupRoot);
            var filter = new Dictionary<Type, List<Enum>>();
            foreach (var entry in dim)
            {
                var parts = entry.Split('=', 2);
                if (parts.Length != 2)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] --dim expects Name=Value, got '{entry}'");
                    return 1;
                }
                var dimensionType = DimensionLookup.FindDimensionType(lookupRules.Assembly, parts[0]);
                if (dimensionType == null || !DimensionLookup.TryParseValue(dimensionType, parts[1], out var value))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Unknown dimension or value in '--dim {entry}'");
                    return 1;
                }
                if (!filter.TryGetValue(dimensionType, out var list))
                    filter[dimensionType] = list = [];
                list.Add(value!);
            }
            dimensionFilter = filter.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Enum>)kv.Value);
        }

        var useServer = !noServer && dimensionFilter == null && string.IsNullOrEmpty(coordinator)
            && Environment.GetEnvironmentVariable("OMEN_NO_SERVER") is not { Length: > 0 };
        if (useServer)
        {
            var root = RuleCompiler.ResolveProjectRoot(targetFile);
            var serverConfiguration = (buildConfig ?? BuildConfiguration.Development).ToString();
            var serverRequest = new BuildRequestDto(targetFile, targetPlatform?.ToString(), targetArch?.ToString(), buildConfig?.ToString(), jobs, background, null);
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += onCancel;
            try
            {
                var (completed, serverResult) = await BuildServerClient.BuildAsync(root, serverConfiguration, ServerCommand.Version, serverRequest,
                    (level, text) => RenderEvent(new OrchestratorEvent(text, Enum.TryParse<OrchestratorEventLevel>(level, out var parsed) ? parsed : OrchestratorEventLevel.Info)),
                    progress =>
                    {
                        if (!progress.Finished)
                            return;
                        var status = progress.Success ? progress.Cached ? " [cyan](cached)[/]" : "" : " [red]FAILED[/]";
                        AnsiConsole.MarkupLine($"[[{progress.Completed}/{progress.Total}]] {(progress.Description ?? "").EscapeMarkup()}{status} [dim]({progress.Seconds:F1}s)[/]");
                    },
                    () => ServerCommand.StartDetachedAsync(root, serverConfiguration), cancel.Token);
                if (completed)
                    return RenderResult(serverResult is null ? null : (serverResult.Success, serverResult.Total, serverResult.Succeeded, serverResult.Cached, serverResult.Skipped), stopwatch);
                AnsiConsole.MarkupLine("[yellow]Omen server kept restarting - building in this process.[/]");
            }
            catch (ServerUnavailableException e)
            {
                AnsiConsole.MarkupLine($"[yellow]{e.Message.EscapeMarkup()} - building in this process.[/]");
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]Build cancelled.[/]");
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        var request = new BuildOrchestratorRequest
        {
            TargetFile = targetFile,
            Platform = targetPlatform,
            Architecture = targetArch,
            Configuration = buildConfig,
            DimensionFilter = dimensionFilter,
            Jobs = jobs
        };
        var orchestrator = new BuildOrchestrator();

        // SynchronousProgress, not System.Progress: this command inspects/relies on every
        // event having actually been rendered by the time BuildAsync's Task completes (the
        // final "Build completed successfully!"/error events happen right before the process
        // exits, with no further await to let a ThreadPool-queued callback catch up).
        var eventProgress = new SynchronousProgress<OrchestratorEvent>(RenderEvent);

        BuildResult? result = null;

        // Spectre's live progress renders with ANSI cursor control, which is nothing at all
        // when stdout is a pipe - a build driven from Visual Studio's NMake project (or any
        // CI log) showed the banner and then silence until the final table. Non-interactive
        // consoles get an MSBuild-style line per finished action instead.
        if (AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected)
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var buildTask = ctx.AddTask("[green]Building[/]", maxValue: 1);
                    var buildProgress = new Progress<BuildProgress>(p =>
                    {
                        buildTask.MaxValue = p.TotalActions == 0 ? 1 : p.TotalActions;
                        buildTask.Value = p.CompletedActions;
                        buildTask.Description = $"[green]Building[/] ({p.ActiveActions} active)";
                    });

                    result = await orchestrator.BuildAsync(request, eventProgress, buildProgress);

                    buildTask.Value = buildTask.MaxValue;
                });
        }
        else
        {
            // Synchronous so lines land in completion order and nothing is lost when the
            // process exits right after the last action (same reasoning as eventProgress).
            var lineProgress = new SynchronousProgress<BuildProgress>(p =>
            {
                if (p.LastResult is not { } last)
                    return;
                var status = last.Success
                    ? last.WasCached ? " [cyan](cached)[/]" : ""
                    : " [red]FAILED[/]";
                AnsiConsole.MarkupLine(
                    $"[[{p.CompletedActions}/{p.TotalActions}]] {last.Action.Description.EscapeMarkup()}{status} [dim]({last.Duration.TotalSeconds:F1}s)[/]");
            });

            result = await orchestrator.BuildAsync(request, eventProgress, lineProgress);
        }

        stopwatch.Stop();

        return RenderResult(result is null ? null : (result.Success, result.TotalActions, result.SuccessfulActions, result.CachedActions, result.SkippedActions), stopwatch);
    }

    /// <summary>The summary table and exit code, the same for an in-process build and one run by the build server.</summary>
    private static int RenderResult((bool Success, int Total, int Compiled, int Cached, int Skipped)? result, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        AnsiConsole.WriteLine();

        if (result is not { Success: true } done)
        {
            return 1;
        }

        var table = new Table();
        table.AddColumn("Metric");
        table.AddColumn("Value");

        table.AddRow("Status", "[green]SUCCESS[/]");
        table.AddRow("Total Actions", done.Total.ToString());
        table.AddRow("Compiled", done.Compiled.ToString());
        table.AddRow("Cached", $"[cyan]{done.Cached}[/]");
        table.AddRow("Skipped", done.Skipped.ToString());
        table.AddRow("Duration", $"{stopwatch.Elapsed.TotalSeconds:F2}s");

        AnsiConsole.Write(table);
        return 0;
    }

    private static void RenderEvent(OrchestratorEvent evt)
    {
        var text = evt.Message.EscapeMarkup();
        switch (evt.Level)
        {
            case OrchestratorEventLevel.Error:
                AnsiConsole.MarkupLine($"[red]{text}[/]");
                break;
            case OrchestratorEventLevel.Warning:
                AnsiConsole.MarkupLine($"[yellow]{text}[/]");
                break;
            case OrchestratorEventLevel.Success:
                AnsiConsole.MarkupLine($"[green]{text}[/]");
                break;
            default:
                AnsiConsole.MarkupLine($"[dim]{text}[/]");
                break;
        }
    }

    private static async Task<int> ExecuteDryRunAsync(
        string targetFile,
        TargetPlatform targetPlatform,
        TargetArchitecture targetArch,
        BuildConfiguration buildConfig,
        int? jobs)
    {
        var workingDir = RuleCompiler.ResolveProjectRoot(targetFile);

        var context = new BuildContext
        {
            Platform = targetPlatform,
            Architecture = targetArch,
            Configuration = buildConfig,
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate", $"{targetPlatform}_{buildConfig}"),
            OutputDirectory = Path.Combine(workingDir, "Binaries", $"{targetPlatform}_{buildConfig}"),
            ParallelJobs = jobs ?? Environment.ProcessorCount
        };

        var ruleCompiler = new RuleCompiler(RuleCompiler.CacheDirectoryFor(workingDir));
        CompiledRules compiledRules;
        try
        {
            compiledRules = await ruleCompiler.CompileRulesAsync(workingDir);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error compiling rules:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }

        var toolchain = PlatformFactory.CreateToolchain(targetPlatform, targetArch);
        if (toolchain == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Could not create toolchain for {targetPlatform}/{targetArch}");
            return 1;
        }

        var targets = compiledRules.CreateTargetRules(context);
        var modules = compiledRules.CreateModuleRules(context);

        // By the file the user named, as BuildOrchestrator does. FirstOrDefault() previewed whichever
        // target reflection listed first - on a tree with several, never the one asked for.
        var dryRunTargetType = compiledRules.FindTargetTypes(targetFile).FirstOrDefault();
        var dryRunTarget = targets.FirstOrDefault(t => t.GetType() == dryRunTargetType);
        if (dryRunTarget == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No target declared in {targetFile}.");
            return 1;
        }

        // Narrow to what the target can actually reach, exactly as a real build does.
        // Without this, --dry-run reported on every module the scan roots contained rather than
        // on the build it claims to be previewing - which is worse than useless, because it
        // fails on modules a real build never touches (a plugin's Tests flavour, say) and sends you
        // hunting a problem your target does not have.
        var dryRunClosure = ModuleClosure.Compute(
            dryRunTarget, modules, compiledRules.CreatePluginRules(context));

        if (dryRunClosure.UnresolvedSeeds.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] '{dryRunTarget.Name}' names " +
                $"{string.Join(", ", dryRunClosure.UnresolvedSeeds)} in LaunchModuleName/ExtraModules, " +
                "which match no module in this build.");
            return 1;
        }

        modules = dryRunClosure.Modules;

        AnsiConsole.MarkupLine(
            $"[grey]{modules.Count} module(s) in {dryRunTarget.Name.EscapeMarkup()}'s dependency closure[/]");

        foreach (var module in modules.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var path = dryRunClosure.PathToSeed(module.Name);
            AnsiConsole.MarkupLine(path.Count == 1
                ? $"[grey]  {module.Name.EscapeMarkup()}  (seed)[/]"
                : $"[grey]  {string.Join(" <- ", path).EscapeMarkup()}[/]");
        }

        try
        {
            LayeringValidator.Validate(modules);
        }
        catch (LayeringViolationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Layering violation:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }

        if (modules.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No modules to build.[/]");
            return 0;
        }

        var targetRules = dryRunTarget;

        var digestCalculator = new Sha256DigestCalculator();
        var graphBuilder = new ActionGraphBuilder(context, toolchain, digestCalculator);
        var graph = graphBuilder.Build(targetRules, modules);

        foreach (var action in graph.GetTopologicalOrder())
        {
            AnsiConsole.WriteLine($"[{action.Type}] {action.ModuleName ?? "(target)"}: {action.CommandLine}");
        }
        return 0;
    }

    /// <summary>
    /// Resolves what to build, plus every target file discovered (for the error message).
    ///
    /// Discovery goes through RuleCompiler - the project-definition-aware scan that honours
    /// a *.omenproject.cs's SourceRoots/ExcludedDirectories - NOT a whole-tree recursive
    /// glob. The glob this replaces enumerated every file under the current directory:
    /// on a real engine root that meant .git, caches, downloaded packages, and entire
    /// worktree checkouts - `omen build <name>` from Visual Studio sat silently crawling
    /// the disk for that long before printing anything, and could resolve a stale target
    /// file inside a worktree the project had excluded.
    /// </summary>
    internal static async Task<(string? TargetFile, IReadOnlyList<string> AllTargetFiles)> ResolveTargetFileAsync(string? target)
    {
        var searchDir = Environment.CurrentDirectory;

        // An explicit existing path needs no discovery at all.
        if (!string.IsNullOrEmpty(target) && File.Exists(target))
            return (Path.GetFullPath(target), []);

        IReadOnlyList<string> targetFiles;
        try
        {
            var discovery = await new RuleCompiler(RuleCompiler.CacheDirectoryFor(searchDir))
                .DiscoverRuleFilesAsync(searchDir);
            targetFiles = discovery.TargetFiles;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error discovering rule files:[/] {ex.Message.EscapeMarkup()}");
            return (null, []);
        }

        if (!string.IsNullOrEmpty(target))
        {
            var withExt = target.EndsWith(".target.cs") ? target : $"{target}.target.cs";
            var found = targetFiles.FirstOrDefault(f =>
                Path.GetFileName(f).Equals(withExt, StringComparison.OrdinalIgnoreCase));

            // A named target that does not exist is an error, never a licence to build a
            // different one: `omen build ModelDoc.Tests` must not silently build something else.
            return (found, targetFiles);
        }

        // Nothing was named: building "the" target of a single-target directory is the
        // convenience this exists for. Ambiguous only when there are several, which the caller
        // reports rather than guessing at.
        return (targetFiles.Count == 1 ? targetFiles[0] : null, targetFiles);
    }
    
    /// <summary>
    /// Platforms O3DE's CMake build knows about but Omen deliberately does not target.
    /// Kept distinct from a typo so the message can say "not supported yet" rather than
    /// "invalid" - the difference between a decision and a bug.
    /// </summary>
    private static readonly string[] KnownUnsupportedPlatforms = ["emscripten", "wasm", "web"];

    private static bool IsKnownUnsupportedPlatform(string? platform) =>
        platform is not null &&
        KnownUnsupportedPlatforms.Contains(platform, StringComparer.OrdinalIgnoreCase);

    private static bool TryParsePlatform(string? platform, out TargetPlatform? result)
    {
        if (string.IsNullOrEmpty(platform)) { result = null; return true; }
        if (Enum.TryParse<TargetPlatform>(platform, ignoreCase: true, out var parsed)) { result = parsed; return true; }
        result = null;
        return false;
    }

    private static TargetArchitecture? ParseArchitecture(string? arch)
    {
        if (string.IsNullOrEmpty(arch)) return null;
        return Enum.TryParse<TargetArchitecture>(arch, ignoreCase: true, out var result) ? result : TargetArchitecture.X64;
    }

    private static BuildConfiguration? ParseConfiguration(string? config)
    {
        if (string.IsNullOrEmpty(config)) return null;
        return Enum.TryParse<BuildConfiguration>(config, ignoreCase: true, out var result) ? result : BuildConfiguration.Development;
    }

    private static TargetPlatform DefaultPlatformForHostOS()
    {
        if (OperatingSystem.IsWindows()) return TargetPlatform.Windows;
        if (OperatingSystem.IsLinux()) return TargetPlatform.Linux;
        if (OperatingSystem.IsMacOS()) return TargetPlatform.Mac;
        if (OperatingSystem.IsFreeBSD()) return TargetPlatform.FreeBSD;
        return TargetPlatform.Windows;
    }

    private static TargetArchitecture DefaultArchitectureForHostOS() =>
        RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
            System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
            System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.ARM64,
            _ => TargetArchitecture.X64
        };
}
