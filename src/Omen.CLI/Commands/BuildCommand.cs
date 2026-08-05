// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Implementations;
using Omen.Core.Interfaces;
using Omen.Core.Rules;
using Omen.Executors;
using Omen.Executors.Orchestration;
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
        
        var configOption = new Option<string>(
            ["--configuration", "-c"],
            () => "Development",
            "Build configuration (Debug, Development, Shipping)");
        
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

        command.AddArgument(targetArgument);
        command.AddOption(platformOption);
        command.AddOption(configOption);
        command.AddOption(archOption);
        command.AddOption(jobsOption);
        command.AddOption(cleanOption);
        command.AddOption(distributeOption);
        command.AddOption(coordinatorOption);
        command.AddOption(dryRunOption);

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

            context.ExitCode = await ExecuteBuildAsync(
                target, platform, config, arch, jobs, clean, distribute, coordinator, dryRun);
        });
        
        return command;
    }
    
    private static async Task<int> ExecuteBuildAsync(
        string? target,
        string? platform,
        string configuration,
        string? arch,
        int? jobs,
        bool clean,
        bool distribute,
        string? coordinator,
        bool dryRun)
    {
        var stopwatch = Stopwatch.StartNew();

        AnsiConsole.Write(new FigletText("OMEN").Color(Color.Orange1));
        AnsiConsole.MarkupLine("[bold]Omen Build System[/]\n");

        var targetFile = ResolveTargetFile(target);
        if (targetFile == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No target file found. Create a .target.cs file or specify a target.");
            return 1;
        }

        var targetPlatform = ParsePlatform(platform);
        if (targetPlatform == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid platform '{platform}'");
            return 1;
        }

        var targetArch = ParseArchitecture(arch);
        var buildConfig = ParseConfiguration(configuration);

        AnsiConsole.MarkupLine($"[blue]Target:[/] {Path.GetFileName(targetFile)}");
        AnsiConsole.MarkupLine($"[blue]Platform:[/] {targetPlatform}");
        AnsiConsole.MarkupLine($"[blue]Architecture:[/] {targetArch}");
        AnsiConsole.MarkupLine($"[blue]Configuration:[/] {buildConfig}");
        AnsiConsole.WriteLine();

        if (dryRun)
        {
            return await ExecuteDryRunAsync(targetFile, targetPlatform.Value, targetArch, buildConfig, jobs);
        }

        var request = new BuildOrchestratorRequest
        {
            TargetFile = targetFile,
            Platform = targetPlatform.Value,
            Architecture = targetArch,
            Configuration = buildConfig,
            Jobs = jobs
        };
        var orchestrator = new BuildOrchestrator();

        var eventProgress = new Progress<OrchestratorEvent>(RenderEvent);

        BuildResult? result = null;

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

        stopwatch.Stop();

        AnsiConsole.WriteLine();

        if (result == null)
        {
            return 1;
        }

        if (result.Success)
        {
            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn("Value");

            table.AddRow("Status", "[green]SUCCESS[/]");
            table.AddRow("Total Actions", result.TotalActions.ToString());
            table.AddRow("Compiled", result.SuccessfulActions.ToString());
            table.AddRow("Cached", $"[cyan]{result.CachedActions}[/]");
            table.AddRow("Skipped", result.SkippedActions.ToString());
            table.AddRow("Duration", $"{stopwatch.Elapsed.TotalSeconds:F2}s");

            AnsiConsole.Write(table);
            return 0;
        }

        return 1;
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
        var workingDir = Path.GetDirectoryName(targetFile) ?? Environment.CurrentDirectory;

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

        var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));
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

        var targetRules = targets.FirstOrDefault();
        if (targetRules == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No target found.");
            return 1;
        }

        var digestCalculator = new Sha256DigestCalculator();
        var graphBuilder = new ActionGraphBuilder(context, toolchain, digestCalculator);
        var graph = graphBuilder.Build(targetRules, modules);

        foreach (var action in graph.GetTopologicalOrder())
        {
            AnsiConsole.WriteLine($"[{action.Type}] {action.ModuleName ?? "(target)"}: {action.CommandLine}");
        }
        return 0;
    }

    private static string? ResolveTargetFile(string? target)
    {
        var searchDir = Environment.CurrentDirectory;
        
        if (!string.IsNullOrEmpty(target))
        {
            // Try exact path
            if (File.Exists(target))
                return Path.GetFullPath(target);
            
            // Try as target name
            var withExt = target.EndsWith(".target.cs") ? target : $"{target}.target.cs";
            var found = Directory.GetFiles(searchDir, withExt, SearchOption.AllDirectories).FirstOrDefault();
            if (found != null)
                return found;
        }
        
        // Find any .target.cs file
        return Directory.GetFiles(searchDir, "*.target.cs", SearchOption.AllDirectories).FirstOrDefault();
    }
    
    private static TargetPlatform? ParsePlatform(string? platform)
    {
        if (string.IsNullOrEmpty(platform))
        {
            // Default to current platform
            if (OperatingSystem.IsWindows()) return TargetPlatform.Windows;
            if (OperatingSystem.IsLinux()) return TargetPlatform.Linux;
            if (OperatingSystem.IsMacOS()) return TargetPlatform.iOS; // macOS uses iOS SDK
            if (OperatingSystem.IsFreeBSD()) return TargetPlatform.FreeBSD;
            return TargetPlatform.Windows;
        }
        
        return Enum.TryParse<TargetPlatform>(platform, ignoreCase: true, out var result) 
            ? result 
            : null;
    }
    
    private static TargetArchitecture ParseArchitecture(string? arch)
    {
        if (string.IsNullOrEmpty(arch))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
                System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
                System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.ARM64,
                _ => TargetArchitecture.X64
            };
        }
        
        return Enum.TryParse<TargetArchitecture>(arch, ignoreCase: true, out var result)
            ? result
            : TargetArchitecture.X64;
    }
    
    private static BuildConfiguration ParseConfiguration(string config)
    {
        return Enum.TryParse<BuildConfiguration>(config, ignoreCase: true, out var result)
            ? result
            : BuildConfiguration.Development;
    }
}
