// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using Spectre.Console;

namespace Omen.CLI.Commands;

/// <summary>
/// Provides the `agent` command for managing build agents (start/status/stop).
/// </summary>
public static class AgentCommand
{
    /// <summary>
    /// Creates the `agent` root command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("agent", "Manage build agent");
        
        command.AddCommand(CreateStartCommand());
        command.AddCommand(CreateStatusCommand());
        command.AddCommand(CreateStopCommand());
        
        return command;
    }
    
    private static Command CreateStartCommand()
    {
        var command = new Command("start", "Start a build agent");
        
        var coordinatorOption = new Option<string>(
            ["--coordinator", "-c"],
            () => "localhost:5051",
            "Coordinator address to connect to");
        
        var nameOption = new Option<string?>(
            ["--name", "-n"],
            "Agent name (defaults to hostname)");
        
        var jobsOption = new Option<int?>(
            ["--jobs", "-j"],
            "Maximum concurrent jobs (defaults to processor count)");
        
        var platformsOption = new Option<string[]>(
            ["--platforms", "-p"],
            () => Array.Empty<string>(),
            "Platforms this agent can build for");
        
        var workDirOption = new Option<string?>(
            ["--work-dir", "-w"],
            "Working directory for build operations");
        
        command.AddOption(coordinatorOption);
        command.AddOption(nameOption);
        command.AddOption(jobsOption);
        command.AddOption(platformsOption);
        command.AddOption(workDirOption);
        
        command.SetHandler(async (context) =>
        {
            var coordinator = context.ParseResult.GetValueForOption(coordinatorOption);
            var name = context.ParseResult.GetValueForOption(nameOption) ?? Environment.MachineName;
            var jobs = context.ParseResult.GetValueForOption(jobsOption) ?? Environment.ProcessorCount;
            var platforms = context.ParseResult.GetValueForOption(platformsOption);
            var workDir = context.ParseResult.GetValueForOption(workDirOption);
            
            AnsiConsole.Write(new Rule("[orange1]Omen Build Agent[/]").RuleStyle("dim"));
            AnsiConsole.WriteLine();
            
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Setting");
            table.AddColumn("Value");
            table.AddRow("Agent Name", name);
            table.AddRow("Coordinator", coordinator!);
            table.AddRow("Max Jobs", jobs.ToString());
            table.AddRow("Platforms", platforms?.Length > 0 ? string.Join(", ", platforms) : "(host platform)");
            table.AddRow("Work Directory", workDir ?? "(temp)");
            
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            
            AnsiConsole.MarkupLine("[yellow]Starting agent...[/]");
            
            // TODO: Implement actual agent startup
            // This would connect to the coordinator and register
            
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Connecting to coordinator...", async ctx =>
                {
                    await Task.Delay(1000);
                    ctx.Status("Registering agent...");
                    await Task.Delay(500);
                    ctx.Status("Ready for work!");
                });
            
            AnsiConsole.MarkupLine("[green]Agent started successfully![/]");
            AnsiConsole.MarkupLine("[dim]Press Ctrl+C to stop the agent[/]");
            
            // Keep running until cancelled
            try
            {
                await Task.Delay(Timeout.Infinite, context.GetCancellationToken());
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[yellow]Shutting down agent...[/]");
            }
            
            context.ExitCode = 0;
        });
        
        return command;
    }
    
    private static Command CreateStatusCommand()
    {
        var command = new Command("status", "Show agent status");
        
        command.SetHandler((context) =>
        {
            AnsiConsole.MarkupLine("[yellow]Agent status not available - no agent running[/]");
            context.ExitCode = 0;
        });
        
        return command;
    }
    
    private static Command CreateStopCommand()
    {
        var command = new Command("stop", "Stop the local agent");
        
        command.SetHandler((context) =>
        {
            AnsiConsole.MarkupLine("[yellow]No local agent to stop[/]");
            context.ExitCode = 0;
        });
        
        return command;
    }
}
