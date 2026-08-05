// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
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
        
        command.AddArgument(targetArgument);
        command.AddOption(platformOption);
        command.AddOption(configOption);
        command.AddOption(allOption);
        
        command.SetHandler(async (context) =>
        {
            var target = context.ParseResult.GetValueForArgument(targetArgument);
            var platform = context.ParseResult.GetValueForOption(platformOption);
            var config = context.ParseResult.GetValueForOption(configOption);
            var all = context.ParseResult.GetValueForOption(allOption);
            
            context.ExitCode = await ExecuteCleanAsync(target, platform, config, all);
        });
        
        return command;
    }
    
    public static async Task<int> ExecuteCleanAsync(
        string? target,
        string? platform,
        string? configuration,
        bool all = false)
    {
        var orchestrator = new CleanOrchestrator();
        var result = await orchestrator.CleanAsync(
            new CleanOrchestratorRequest
            {
                ProjectRoot = Environment.CurrentDirectory,
                Platform = platform,
                Configuration = configuration,
                All = all
            },
            new Progress<OrchestratorEvent>(RenderEvent));

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
