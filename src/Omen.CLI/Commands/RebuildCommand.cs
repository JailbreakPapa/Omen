// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;

namespace Omen.CLI.Commands;

public static class RebuildCommand
{
    public static Command Create()
    {
        var command = new Command("rebuild", "Clean and rebuild a target or module");
        
        var targetArgument = new Argument<string?>(
            "target",
            () => null,
            "The target to rebuild");
        
        var platformOption = new Option<string?>(
            ["--platform", "-p"],
            "Target platform");
        
        var configOption = new Option<string>(
            ["--configuration", "-c"],
            () => "Development",
            "Build configuration");
        
        command.AddArgument(targetArgument);
        command.AddOption(platformOption);
        command.AddOption(configOption);
        
        command.SetHandler(async (context) =>
        {
            // First run clean, then build
            var cleanResult = await CleanCommand.ExecuteCleanAsync(
                context.ParseResult.GetValueForArgument(targetArgument),
                context.ParseResult.GetValueForOption(platformOption),
                context.ParseResult.GetValueForOption(configOption));
            
            if (cleanResult != 0)
            {
                context.ExitCode = cleanResult;
                return;
            }
            
            // The build command handler will be reused
            // For now, just indicate success
            context.ExitCode = 0;
        });
        
        return command;
    }
}
