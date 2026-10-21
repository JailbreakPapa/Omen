// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Omen.CLI.Commands;
using Omen.Core.Configuration;
using Spectre.Console;

namespace Omen.CLI;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var previous = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            string root = Environment.CurrentDirectory;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--project") root = Path.GetFullPath(args[i + 1]);
            foreach (string arg in args)
                if (arg.StartsWith("--project=", StringComparison.Ordinal)) root = Path.GetFullPath(arg.Split('=', 2)[1]);
            if (args.Contains("--list-presets"))
            {
                foreach (var preset in new ProjectPresets(root).List())
                    AnsiConsole.WriteLine($"{preset.Name}: {preset.Description} [{string.Join(", ", preset.Commands.Keys)}]");
                return 0;
            }
            string? presetName = null;
            var explicitArgs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--preset" || args[i].StartsWith("--preset=", StringComparison.Ordinal))
                {
                    if (presetName != null) throw new InvalidOperationException("Only one --preset may be selected.");
                    presetName = args[i].Contains('=') ? args[i].Split('=', 2)[1] :
                        ++i < args.Length ? args[i] : throw new InvalidOperationException("--preset requires a name.");
                }
                else explicitArgs.Add(args[i]);
            }
            if (presetName != null)
            {
                if (explicitArgs.Count == 0) throw new InvalidOperationException("Select a command, e.g. omen build --preset dev.");
                string operation = explicitArgs[0];
                int commandTokens = 1;
                if (operation == "generate" && explicitArgs.Count > 1)
                {
                    operation += "." + explicitArgs[1];
                    commandTokens = 2;
                }
                var preset = new ProjectPresets(root).Resolve(presetName);
                if (preset.Hidden) throw new InvalidOperationException($"Preset '{presetName}' is hidden and can only be inherited.");
                if (!ProjectPresets.Available(preset)) throw new InvalidOperationException($"Preset '{presetName}' is not available on {ProjectPresets.HostPlatform}.");
                if (!preset.Commands.TryGetValue(operation, out var defaults))
                    throw new InvalidOperationException($"Preset '{presetName}' has no '{operation}' arguments.");
                args = explicitArgs.Take(commandTokens).Concat(ProjectPresets.MergeArguments(defaults,
                    explicitArgs.Skip(commandTokens).ToArray())).ToArray();
                foreach (var (key, value) in preset.Environment)
                {
                    previous[key] = Environment.GetEnvironmentVariable(key);
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
            return await InvokeAsync(args);
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex) { AnsiConsole.WriteLine("Error: " + ex.Message); return 1; }
        finally { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); }
    }

    private static async Task<int> InvokeAsync(string[] args)
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
        rootCommand.AddCommand(WorkflowCommands.Test());
        rootCommand.AddCommand(WorkflowCommands.Install(package: false));
        rootCommand.AddCommand(WorkflowCommands.Install(package: true));
        rootCommand.AddCommand(WorkflowCommands.Targets());
        rootCommand.AddCommand(WorkflowCommands.Workflow());
        rootCommand.AddCommand(WorkflowCommands.Check());
        
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
        rootCommand.AddGlobalOption(new Option<string?>("--preset", "Use a named OmenPresets.json/OmenUserPresets.json preset"));
        rootCommand.AddGlobalOption(new Option<bool>("--list-presets", "List available presets for the current project"));
        
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
