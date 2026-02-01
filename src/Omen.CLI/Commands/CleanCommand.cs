// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
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
    
    public static Task<int> ExecuteCleanAsync(
        string? target,
        string? platform,
        string? configuration,
        bool all = false)
    {
        var workingDir = Environment.CurrentDirectory;
        var intermediateDir = Path.Combine(workingDir, "Intermediate");
        var binariesDir = Path.Combine(workingDir, "Binaries");
        
        var dirsToClean = new List<string>();
        
        if (all || (string.IsNullOrEmpty(platform) && string.IsNullOrEmpty(configuration)))
        {
            // Clean everything
            if (Directory.Exists(intermediateDir))
                dirsToClean.Add(intermediateDir);
            if (Directory.Exists(binariesDir))
                dirsToClean.Add(binariesDir);
        }
        else
        {
            // Clean specific platform/configuration
            var pattern = $"{platform ?? "*"}_{configuration ?? "*"}";
            
            if (Directory.Exists(intermediateDir))
            {
                dirsToClean.AddRange(
                    Directory.GetDirectories(intermediateDir, pattern));
            }
            
            if (Directory.Exists(binariesDir))
            {
                dirsToClean.AddRange(
                    Directory.GetDirectories(binariesDir, pattern));
            }
        }
        
        if (dirsToClean.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to clean.[/]");
            return Task.FromResult(0);
        }
        
        AnsiConsole.MarkupLine($"[blue]Cleaning {dirsToClean.Count} directories...[/]");
        
        foreach (var dir in dirsToClean)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                    AnsiConsole.MarkupLine($"  [green]✓[/] {Path.GetRelativePath(workingDir, dir).EscapeMarkup()}");
            }
            catch (Exception ex)
            {
                    AnsiConsole.MarkupLine($"  [red]✗[/] {Path.GetRelativePath(workingDir, dir).EscapeMarkup()}: {ex.Message.EscapeMarkup()}");
            }
        }
        
        AnsiConsole.MarkupLine("[green]Clean complete.[/]");
        return Task.FromResult(0);
    }
}
