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
        // Redirected (Visual Studio's Output window, a teed log) Spectre wraps at 80 columns, splitting
        // paths across lines; the reader's window wraps on its own.
        if (Console.IsOutputRedirected)
            AnsiConsole.Profile.Width = 4096;

        var rootCommand = new RootCommand("Omen Build System - Fast, scalable, distributed C/C++ builds")
        {
            Name = "omen"
        };
        
        // Add commands
        rootCommand.AddCommand(BuildCommand.Create());
        rootCommand.AddCommand(RebuildCommand.Create());
        rootCommand.AddCommand(CleanCommand.Create());
        rootCommand.AddCommand(GenerateCommand.Create());
        rootCommand.AddCommand(ConfigureCommand.Create());
        rootCommand.AddCommand(AgentCommand.Create());
        rootCommand.AddCommand(CoordinatorCommand.Create());
        rootCommand.AddCommand(InfoCommand.Create());
        rootCommand.AddCommand(ServerCommand.Create());
        
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
                // Escape the message: exception text routinely contains square brackets
                // (type names, indexer paths, MSBuild diagnostics), which Spectre parses as
                // markup and then throws on - replacing the real error with an unbalanced-tag
                // crash from inside the error handler.
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(exception.Message)}");
                context.ExitCode = 1;
            })
            .Build();
        
        return await parser.InvokeAsync(args);
    }
}
