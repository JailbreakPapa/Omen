// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using Omen.Core.Configuration;
using Omen.Core.Generators;
using Omen.Core.Rules;
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
        var workingDir = Environment.CurrentDirectory;

        AnsiConsole.MarkupLine($"[blue]Generating project files for {ide.EscapeMarkup()}...[/]");

        // Find and compile rules
        var targetFiles = Directory.GetFiles(workingDir, "*.target.cs", SearchOption.AllDirectories);
        if (targetFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No target file found. Create a .target.cs file first.");
            return 1;
        }

        var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));
        CompiledRules compiledRules;

        try
        {
            await AnsiConsole.Status()
                .StartAsync("Compiling build rules...", async ctx =>
                {
                    compiledRules = await ruleCompiler.CompileRulesAsync(workingDir);
                });

            compiledRules = await ruleCompiler.CompileRulesAsync(workingDir);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error compiling rules:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }

        // Create a build context for instantiation
        var context = new BuildContext
        {
            Platform = TargetPlatform.Windows,
            Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Development,
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate"),
            OutputDirectory = Path.Combine(workingDir, "Binaries")
        };

        var targets = compiledRules.CreateTargetRules(context);
        var modules = compiledRules.CreateModuleRules(context);

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No targets found.");
            return 1;
        }

        var target = targets.First();

        // Generate based on IDE
        switch (ide)
        {
            case "vs2019":
                await GenerateVisualStudioAsync(workingDir, target, modules, VisualStudioGenerator.VisualStudioVersion.VS2019);
                break;

            case "vs2022":
                await GenerateVisualStudioAsync(workingDir, target, modules, VisualStudioGenerator.VisualStudioVersion.VS2022);
                break;

            case "vs2026":
                await GenerateVisualStudioAsync(workingDir, target, modules, VisualStudioGenerator.VisualStudioVersion.VS2026);
                break;

            case "vscode":
                await GenerateVSCodeAsync(workingDir, target, modules);
                break;

            case "cmake":
                await GenerateCMakeAsync(workingDir, target, modules);
                break;

            case "rider":
                // Rider can use CMake projects
                await GenerateCMakeAsync(workingDir, target, modules);
                AnsiConsole.MarkupLine("[dim]Rider can open the generated CMakeLists.txt[/]");
                break;

            default:
                AnsiConsole.MarkupLine($"[red]Error:[/] Unknown IDE '{ide.EscapeMarkup()}'");
                AnsiConsole.MarkupLine("[dim]Supported: vs2019, vs2022, vs2026, vscode, cmake, rider[/]");
                return 1;
        }

        return 0;
    }

    private static async Task GenerateVisualStudioAsync(
        string projectRoot,
        TargetRules target,
        IReadOnlyList<ModuleRules> modules,
        VisualStudioGenerator.VisualStudioVersion version)
    {
        var generator = new VisualStudioGenerator(projectRoot, version);
        
        await AnsiConsole.Status()
            .StartAsync("Generating Visual Studio solution...", async ctx =>
            {
                await generator.GenerateAsync(target, modules);
            });

        var solutionPath = Path.Combine(projectRoot, $"{target.Name}.sln");
        AnsiConsole.MarkupLine($"[green]✓[/] Generated Visual Studio solution: {solutionPath.EscapeMarkup()}");
        
        var projectCount = modules.Count;
        AnsiConsole.MarkupLine($"[dim]  • {projectCount} project(s) generated[/]");
        AnsiConsole.MarkupLine($"[dim]  • Project files in: Intermediate/ProjectFiles/[/]");
    }

    private static async Task GenerateVSCodeAsync(
        string projectRoot,
        TargetRules target,
        IReadOnlyList<ModuleRules> modules)
    {
        var vscodeDir = Path.Combine(projectRoot, ".vscode");
        Directory.CreateDirectory(vscodeDir);

        // Generate c_cpp_properties.json
        var includePaths = new List<string> { "${workspaceFolder}/**" };
        foreach (var module in modules)
        {
            var sourceDir = module.SourceDirectory ?? $"Source/{module.Name}";
            includePaths.Add($"${{workspaceFolder}}/{sourceDir}/**");
        }

        var cppProperties = $$"""
        {
            "configurations": [
                {
                    "name": "Win32",
                    "includePath": [
                        {{string.Join(",\n                        ", includePaths.Select(p => $"\"{p}\""))}}
                    ],
                    "defines": [
                        "_DEBUG",
                        "UNICODE",
                        "_UNICODE"
                    ],
                    "windowsSdkVersion": "10.0.22621.0",
                    "compilerPath": "cl.exe",
                    "cStandard": "c17",
                    "cppStandard": "c++20",
                    "intelliSenseMode": "windows-msvc-x64"
                },
                {
                    "name": "Linux",
                    "includePath": [
                        {{string.Join(",\n                        ", includePaths.Select(p => $"\"{p}\""))}}
                    ],
                    "defines": [],
                    "compilerPath": "/usr/bin/clang",
                    "cStandard": "c17",
                    "cppStandard": "c++20",
                    "intelliSenseMode": "linux-clang-x64"
                }
            ],
            "version": 4
        }
        """;

        await File.WriteAllTextAsync(Path.Combine(vscodeDir, "c_cpp_properties.json"), cppProperties);

        // Generate tasks.json
        var tasks = $$"""
        {
            "version": "2.0.0",
            "tasks": [
                {
                    "label": "Omen: Build (Debug)",
                    "type": "shell",
                    "command": "omen",
                    "args": ["build", "-c", "Debug"],
                    "group": {
                        "kind": "build",
                        "isDefault": true
                    },
                    "problemMatcher": ["$msCompile"]
                },
                {
                    "label": "Omen: Build (Development)",
                    "type": "shell",
                    "command": "omen",
                    "args": ["build", "-c", "Development"],
                    "group": "build",
                    "problemMatcher": ["$msCompile"]
                },
                {
                    "label": "Omen: Build (Shipping)",
                    "type": "shell",
                    "command": "omen",
                    "args": ["build", "-c", "Shipping"],
                    "group": "build",
                    "problemMatcher": ["$msCompile"]
                },
                {
                    "label": "Omen: Clean",
                    "type": "shell",
                    "command": "omen",
                    "args": ["clean"],
                    "problemMatcher": []
                },
                {
                    "label": "Omen: Rebuild",
                    "type": "shell",
                    "command": "omen",
                    "args": ["rebuild", "-c", "Debug"],
                    "problemMatcher": ["$msCompile"]
                }
            ]
        }
        """;

        await File.WriteAllTextAsync(Path.Combine(vscodeDir, "tasks.json"), tasks);

        // Generate launch.json
        var launch = $$"""
        {
            "version": "0.2.0",
            "configurations": [
                {
                    "name": "{{target.Name}} (Debug)",
                    "type": "cppvsdbg",
                    "request": "launch",
                    "program": "${workspaceFolder}/Binaries/Windows_Debug/{{target.Name}}.exe",
                    "args": [],
                    "stopAtEntry": false,
                    "cwd": "${workspaceFolder}",
                    "environment": [],
                    "console": "integratedTerminal",
                    "preLaunchTask": "Omen: Build (Debug)"
                },
                {
                    "name": "{{target.Name}} (Development)",
                    "type": "cppvsdbg",
                    "request": "launch",
                    "program": "${workspaceFolder}/Binaries/Windows_Development/{{target.Name}}.exe",
                    "args": [],
                    "stopAtEntry": false,
                    "cwd": "${workspaceFolder}",
                    "environment": [],
                    "console": "integratedTerminal",
                    "preLaunchTask": "Omen: Build (Development)"
                }
            ]
        }
        """;

        await File.WriteAllTextAsync(Path.Combine(vscodeDir, "launch.json"), launch);

        AnsiConsole.MarkupLine($"[green]✓[/] Generated VS Code configuration in .vscode/");
        AnsiConsole.MarkupLine("[dim]  • c_cpp_properties.json (IntelliSense)[/]");
        AnsiConsole.MarkupLine("[dim]  • tasks.json (Build tasks)[/]");
        AnsiConsole.MarkupLine("[dim]  • launch.json (Debug configurations)[/]");
    }

    private static async Task GenerateCMakeAsync(
        string projectRoot,
        TargetRules target,
        IReadOnlyList<ModuleRules> modules)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Generated by Omen Build System");
        sb.AppendLine("cmake_minimum_required(VERSION 3.20)");
        sb.AppendLine($"project({target.Name} CXX)");
        sb.AppendLine();
        sb.AppendLine("set(CMAKE_CXX_STANDARD 20)");
        sb.AppendLine("set(CMAKE_CXX_STANDARD_REQUIRED ON)");
        sb.AppendLine("set(CMAKE_EXPORT_COMPILE_COMMANDS ON)");
        sb.AppendLine();

        foreach (var module in modules)
        {
            var sourceDir = module.SourceDirectory ?? $"Source/{module.Name}";
            var fullSourceDir = Path.Combine(projectRoot, sourceDir);

            sb.AppendLine($"# Module: {module.Name}");

            // Collect source files
            if (Directory.Exists(fullSourceDir))
            {
                var sources = Directory.GetFiles(fullSourceDir, "*.cpp", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(fullSourceDir, "*.c", SearchOption.AllDirectories))
                    .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
                    .ToList();

                if (sources.Count > 0)
                {
                    var libType = module.Type == ModuleType.Runtime ? "SHARED" : "STATIC";
                    sb.AppendLine($"add_library({module.Name} {libType}");
                    foreach (var source in sources)
                    {
                        sb.AppendLine($"    {source}");
                    }
                    sb.AppendLine(")");
                    sb.AppendLine();

                    // Include directories
                    sb.AppendLine($"target_include_directories({module.Name} PUBLIC");
                    sb.AppendLine($"    {sourceDir}");
                    foreach (var inc in module.PublicIncludePaths)
                    {
                        sb.AppendLine($"    {sourceDir}/{inc}");
                    }
                    sb.AppendLine(")");

                    // Private includes
                    if (module.PrivateIncludePaths.Count > 0)
                    {
                        sb.AppendLine($"target_include_directories({module.Name} PRIVATE");
                        foreach (var inc in module.PrivateIncludePaths)
                        {
                            sb.AppendLine($"    {sourceDir}/{inc}");
                        }
                        sb.AppendLine(")");
                    }

                    // Definitions
                    if (module.PublicDefinitions.Count > 0 || module.PrivateDefinitions.Count > 0)
                    {
                        sb.AppendLine($"target_compile_definitions({module.Name}");
                        foreach (var def in module.PublicDefinitions)
                        {
                            sb.AppendLine($"    PUBLIC {def}");
                        }
                        foreach (var def in module.PrivateDefinitions)
                        {
                            sb.AppendLine($"    PRIVATE {def}");
                        }
                        sb.AppendLine(")");
                    }

                    // Dependencies
                    if (module.PublicDependencies.Count > 0)
                    {
                        sb.AppendLine($"target_link_libraries({module.Name} PUBLIC");
                        foreach (var dep in module.PublicDependencies)
                        {
                            sb.AppendLine($"    {dep}");
                        }
                        sb.AppendLine(")");
                    }

                    sb.AppendLine();
                }
            }
        }

        // Create main executable
        sb.AppendLine($"# Main executable");
        sb.AppendLine($"add_executable({target.Name}_exe");
        sb.AppendLine($"    # Add your main.cpp here");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine($"target_link_libraries({target.Name}_exe PRIVATE");
        foreach (var module in modules)
        {
            sb.AppendLine($"    {module.Name}");
        }
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine($"set_target_properties({target.Name}_exe PROPERTIES OUTPUT_NAME \"{target.Name}\")");

        await File.WriteAllTextAsync(Path.Combine(projectRoot, "CMakeLists.txt"), sb.ToString());

        AnsiConsole.MarkupLine($"[green]✓[/] Generated CMakeLists.txt");
        AnsiConsole.MarkupLine($"[dim]  • {modules.Count} module(s) configured[/]");
        AnsiConsole.MarkupLine("[dim]  • Use: cmake -B build && cmake --build build[/]");
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
