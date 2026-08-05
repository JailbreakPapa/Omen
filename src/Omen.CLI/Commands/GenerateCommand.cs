// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using Omen.Executors.Orchestration;
using Spectre.Console;

namespace Omen.CLI.Commands;

public static class GenerateCommand
{
    public static Command Create()
    {
        var command = new Command("generate", "Generate project files or build scripts");

        // Subcommands
        command.AddCommand(CreateProjectFilesCommand());
        command.AddCommand(CreateModuleCommand());
        command.AddCommand(CreateTargetCommand());

        return command;
    }

    private static Command CreateProjectFilesCommand()
    {
        var command = new Command("project", "Generate IDE project files");

        var ideOption = new Option<string>(
            ["--ide", "-i"],
            () => "vs2022",
            "IDE to generate for (vs2019, vs2022, vs2026, vscode, rider, cmake)");

        command.AddOption(ideOption);

        command.SetHandler(async (context) =>
        {
            var ide = context.ParseResult.GetValueForOption(ideOption)?.ToLowerInvariant() ?? "vs2022";

            context.ExitCode = await GenerateProjectFilesAsync(ide);
        });

        return command;
    }

    private static async Task<int> GenerateProjectFilesAsync(string ide)
    {
        var ideKind = ide switch
        {
            "vs2019" => IdeKind.VS2019,
            "vs2022" => IdeKind.VS2022,
            "vs2026" => IdeKind.VS2026,
            "vscode" => IdeKind.VSCode,
            "cmake" => IdeKind.CMake,
            "rider" => IdeKind.Rider,
            _ => (IdeKind?)null
        };

        if (ideKind == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unknown IDE '{ide.EscapeMarkup()}'");
            AnsiConsole.MarkupLine("[dim]Supported: vs2019, vs2022, vs2026, vscode, cmake, rider[/]");
            return 1;
        }

        var orchestrator = new ProjectGenerationOrchestrator();
        var success = await orchestrator.GenerateAsync(
            new ProjectGenerationOrchestratorRequest { ProjectRoot = Environment.CurrentDirectory, Ide = ideKind.Value },
            new Progress<OrchestratorEvent>(RenderEvent));

        return success ? 0 : 1;
    }

    private static void RenderEvent(OrchestratorEvent evt)
    {
        var text = evt.Message.EscapeMarkup();
        switch (evt.Level)
        {
            case OrchestratorEventLevel.Error:
                AnsiConsole.MarkupLine($"[red]Error:[/] {text}");
                break;
            case OrchestratorEventLevel.Warning:
                AnsiConsole.MarkupLine($"[yellow]{text}[/]");
                break;
            case OrchestratorEventLevel.Success:
                AnsiConsole.MarkupLine($"[green]✓[/] {text}");
                break;
            default:
                AnsiConsole.MarkupLine($"[dim]{text}[/]");
                break;
        }
    }

    private static Command CreateModuleCommand()
    {
        var command = new Command("module", "Generate a new module");

        var nameArgument = new Argument<string>(
            "name",
            "The name of the module");

        var typeOption = new Option<string>(
            ["--type", "-t"],
            () => "Runtime",
            "Module type (Runtime, Developer, Editor, ThirdParty)");

        var pathOption = new Option<string?>(
            ["--path", "-p"],
            "Directory to create the module in");

        command.AddArgument(nameArgument);
        command.AddOption(typeOption);
        command.AddOption(pathOption);

        command.SetHandler(async (context) =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            var type = context.ParseResult.GetValueForOption(typeOption) ?? "Runtime";
            var path = context.ParseResult.GetValueForOption(pathOption) ??
                       Path.Combine(Environment.CurrentDirectory, "Source", name);

            context.ExitCode = await GenerateModuleAsync(name, type, path);
        });

        return command;
    }

    private static Command CreateTargetCommand()
    {
        var command = new Command("target", "Generate a new target");

        var nameArgument = new Argument<string>(
            "name",
            "The name of the target");

        var typeOption = new Option<string>(
            ["--type", "-t"],
            () => "Executable",
            "Target type (Executable, SharedLibrary, StaticLibrary)");

        command.AddArgument(nameArgument);
        command.AddOption(typeOption);

        command.SetHandler(async (context) =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArgument);
            var type = context.ParseResult.GetValueForOption(typeOption) ?? "Executable";

            context.ExitCode = await GenerateTargetAsync(name, type);
        });

        return command;
    }

    private static async Task<int> GenerateModuleAsync(string name, string type, string path)
    {
        AnsiConsole.MarkupLine($"[blue]Generating module '{name.EscapeMarkup()}'...[/]");

        // Create directory structure
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "Public"));
        Directory.CreateDirectory(Path.Combine(path, "Private"));

        // Create module rules file
        var moduleContent = $$"""
            // {{name}} Module Build Rules

            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class {{name}}Module : ModuleRules
            {
                public {{name}}Module(BuildContext context) : base(context)
                {
                    Type = ModuleType.{{type}};
                    SourceDirectory = "Source/{{name}}";

                    PublicIncludePaths.Add("Public");
                    PrivateIncludePaths.Add("Private");

                    // Add your dependencies here
                    // PublicDependencies.Add("Core");

                    // Add your definitions here
                    // PublicDefinitions.Add("MY_DEFINE=1");

                    // Compiler settings
                    CppStandard = CppStandard.Cpp20;
                    EnableExceptions = true;
                    EnableRTTI = false;
                }
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(path, $"{name}.module.cs"), moduleContent);

        // Create a basic header file
        var headerContent = $$"""
            // {{name}} Module

            #pragma once

            // Public API header for {{name}} module
            """;

        await File.WriteAllTextAsync(Path.Combine(path, "Public", $"{name}.h"), headerContent);

        // Create a basic source file
        var sourceContent = $$"""
            // {{name}} Module

            #include "{{name}}.h"

            // Implementation
            """;

        await File.WriteAllTextAsync(Path.Combine(path, "Private", $"{name}.cpp"), sourceContent);

        AnsiConsole.MarkupLine($"[green]✓[/] Created module at {path.EscapeMarkup()}");
        AnsiConsole.MarkupLine("  - [dim]Public/[/] (public headers)");
        AnsiConsole.MarkupLine("  - [dim]Private/[/] (implementation)");
        AnsiConsole.MarkupLine($"  - [dim]{name.EscapeMarkup()}.module.cs[/] (build rules)");

        return 0;
    }

    private static async Task<int> GenerateTargetAsync(string name, string type)
    {
        AnsiConsole.MarkupLine($"[blue]Generating target '{name.EscapeMarkup()}'...[/]");

        var targetContent = $$"""
            // {{name}} Target Build Rules

            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class {{name}}Target : TargetRules
            {
                public {{name}}Target(BuildContext context) : base(context)
                {
                    Type = TargetType.{{type}};

                    // Supported platforms
                    SupportedPlatforms.Add(TargetPlatform.Windows);
                    SupportedPlatforms.Add(TargetPlatform.Linux);

                    // Build settings
                    UsePCHFiles = true;
                    UseUnityBuild = true;

                    // Enable LTO for shipping builds
                    ConfigureForConfiguration(BuildConfiguration.Shipping, () =>
                    {
                        EnableLTO = true;
                    });

                    // Add your modules here
                    // ExtraModules.Add("{{name}}");
                }
            }
            """;

        var targetPath = Path.Combine(Environment.CurrentDirectory, $"{name}.target.cs");
        await File.WriteAllTextAsync(targetPath, targetContent);

        AnsiConsole.MarkupLine($"[green]✓[/] Created target at {targetPath.EscapeMarkup()}");

        return 0;
    }
}
