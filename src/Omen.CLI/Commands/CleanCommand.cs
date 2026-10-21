// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using Omen.Core.Diagnostics;
using Omen.Core.Rules;
using Omen.Executors.Orchestration;
using Spectre.Console;

namespace Omen.CLI.Commands;

public static class CleanCommand
{
    public static Command Create()
    {
        var command = new Command("clean", "Clean build outputs");
        
        var targetArgument = new Argument<string?>(
            "target",
            () => null,
            "The target to clean");
        
        var platformOption = new Option<string?>(
            ["--platform", "-p"],
            "Target platform");
        
        var configOption = new Option<string?>(
            ["--configuration", "-c"],
            "Build configuration (or all if not specified)");
        
        var allOption = new Option<bool>(
            "--all",
            "Clean all platforms and configurations");

        // Separate from --all (which deletes Intermediate wholesale, rule cache included):
        // recompiling rules is cheap next to recompiling C++, so "my rule edits aren't taking
        // effect" wants this, not a full clean.
        var rulesOption = new Option<bool>(
            "--rules",
            "Clear the compiled rule cache (Intermediate/RuleCache). On its own, leaves build outputs alone.");

        command.AddArgument(targetArgument);
        command.AddOption(platformOption);
        command.AddOption(configOption);
        command.AddOption(allOption);
        command.AddOption(rulesOption);
        
        command.SetHandler(async (context) =>
        {
            var target = context.ParseResult.GetValueForArgument(targetArgument);
            var platform = context.ParseResult.GetValueForOption(platformOption);
            var config = context.ParseResult.GetValueForOption(configOption);
            var all = context.ParseResult.GetValueForOption(allOption);
            var rules = context.ParseResult.GetValueForOption(rulesOption);

            context.ExitCode = await ExecuteCleanAsync(target, platform, config, all, rules);
        });
        
        return command;
    }
    
    public static async Task<int> ExecuteCleanAsync(
        string? target,
        string? platform,
        string? configuration,
        bool all = false,
        bool rules = false,
        string? projectRoot = null)
    {
        projectRoot ??= Environment.CurrentDirectory;

        if (rules)
        {
            var cacheDirectory = RuleCompiler.CacheDirectoryFor(projectRoot);
            var removed = RuleCompiler.ClearCacheDirectory(cacheDirectory);
            RenderEvent(new OrchestratorEvent(
                removed == 0
                    ? "Rule cache was already empty."
                    : $"Cleared {removed} cached rule file(s) from {cacheDirectory}.",
                OrchestratorEventLevel.Success));

            // --rules on its own means only the rule cache; build outputs are left alone.
            if (!all && string.IsNullOrEmpty(platform) && string.IsNullOrEmpty(configuration))
                return 0;
        }

        await ServerCommand.StopServersAsync(projectRoot);

        var orchestrator = new CleanOrchestrator();
        var result = await orchestrator.CleanAsync(
            new CleanOrchestratorRequest
            {
                ProjectRoot = projectRoot,
                Platform = platform,
                Configuration = configuration,
                All = all
            },
            new SynchronousProgress<OrchestratorEvent>(RenderEvent));

        return result.DirectoriesFailed > 0 ? 1 : 0;
    }

    private static void RenderEvent(OrchestratorEvent evt)
    {
        var text = evt.Message.EscapeMarkup();
        switch (evt.Level)
        {
            case OrchestratorEventLevel.Error:
                AnsiConsole.MarkupLine($"  [red]{text}[/]");
                break;
            case OrchestratorEventLevel.Warning:
                AnsiConsole.MarkupLine($"[yellow]{text}[/]");
                break;
            case OrchestratorEventLevel.Success:
                AnsiConsole.MarkupLine($"[green]{text}[/]");
                break;
            default:
                AnsiConsole.MarkupLine($"[blue]{text}[/]");
                break;
        }
    }
}
