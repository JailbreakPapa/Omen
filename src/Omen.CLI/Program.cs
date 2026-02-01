// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Omen.CLI.Commands;
using Spectre.Console;

namespace Omen.CLI;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Omen Build System - Fast, scalable, distributed C/C++ builds")
        {
            Name = "omen"
        };
        
        // Add commands
        rootCommand.AddCommand(BuildCommand.Create());
        rootCommand.AddCommand(RebuildCommand.Create());
        rootCommand.AddCommand(CleanCommand.Create());
        rootCommand.AddCommand(GenerateCommand.Create());
        rootCommand.AddCommand(AgentCommand.Create());
        rootCommand.AddCommand(CoordinatorCommand.Create());
        rootCommand.AddCommand(InfoCommand.Create());
        
        // Global options
        var verboseOption = new Option<bool>(
            ["--verbose", "-v"],
            "Enable verbose output");
        
        var quietOption = new Option<bool>(
            ["--quiet", "-q"],
            "Suppress non-essential output");
        
        var colorOption = new Option<bool>(
            "--no-color",
            "Disable colored output");
        
        rootCommand.AddGlobalOption(verboseOption);
        rootCommand.AddGlobalOption(quietOption);
        rootCommand.AddGlobalOption(colorOption);
        
        var parser = new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .UseExceptionHandler((exception, context) =>
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {exception.Message}");
                context.ExitCode = 1;
            })
            .Build();
        
        return await parser.InvokeAsync(args);
    }
}
