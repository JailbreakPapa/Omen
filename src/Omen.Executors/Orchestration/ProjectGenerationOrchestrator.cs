// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Generators;
using Omen.Core.Graph;
using Omen.Core.Implementations;
using Omen.Core.Rules;
using Omen.Platforms;

namespace Omen.Executors.Orchestration;

public enum IdeKind
{
    VS2019,
    VS2022,
    VS2026,
    VSCode,
    CMake,
    Rider
}

public sealed class ProjectGenerationOrchestratorRequest
{
    public required string ProjectRoot { get; init; }
    public required IdeKind Ide { get; init; }
}

public sealed class ProjectGenerationOrchestrator
{
    public async Task<bool> GenerateAsync(ProjectGenerationOrchestratorRequest request, IProgress<OrchestratorEvent>? events)
    {
        var workingDir = request.ProjectRoot;

        events?.Report(new OrchestratorEvent($"Generating project files for {request.Ide}...", OrchestratorEventLevel.Info));

        var targetFiles = Directory.GetFiles(workingDir, "*.target.cs", SearchOption.AllDirectories);
        if (targetFiles.Length == 0)
        {
            events?.Report(new OrchestratorEvent("No target file found. Create a .target.cs file first.", OrchestratorEventLevel.Error));
            return false;
        }

        var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));
        CompiledRules compiledRules;
        try
        {
            compiledRules = await ruleCompiler.CompileRulesAsync(workingDir);
        }
        catch (Exception ex)
        {
            events?.Report(new OrchestratorEvent($"Error compiling rules: {ex.Message}", OrchestratorEventLevel.Error));
            return false;
        }

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
            events?.Report(new OrchestratorEvent("No targets found.", OrchestratorEventLevel.Error));
            return false;
        }

        var target = targets.First();

        switch (request.Ide)
        {
            case IdeKind.VS2019:
                await GenerateVisualStudioAsync(workingDir, target, modules, VisualStudioGenerator.VisualStudioVersion.VS2019, events);
                break;

            case IdeKind.VS2022:
                await GenerateVisualStudioAsync(workingDir, target, modules, VisualStudioGenerator.VisualStudioVersion.VS2022, events);
                break;

            case IdeKind.VS2026:
                await GenerateVisualStudioAsync(workingDir, target, modules, VisualStudioGenerator.VisualStudioVersion.VS2026, events);
                break;

            case IdeKind.VSCode:
                await GenerateVSCodeAsync(workingDir, target, modules, events);
                break;

            case IdeKind.CMake:
                await GenerateCMakeAsync(workingDir, target, modules, events);
                break;

            case IdeKind.Rider:
                // Rider can use CMake projects.
                await GenerateCMakeAsync(workingDir, target, modules, events);
                events?.Report(new OrchestratorEvent("Rider can open the generated CMakeLists.txt", OrchestratorEventLevel.Info));
                break;
        }

        // modules.Count == 0 guards against ActionGraph.GetCriticalPath() throwing on an
        // empty graph (ActionGraphBuilder.Build always calls ComputePriorities(), even when
        // BuildLinkAction produced zero actions) - the same edge case BuildOrchestrator
        // (Task 2) already guards against before building a graph.
        var toolchain = PlatformFactory.CreateToolchain(context.Platform, context.Architecture);
        if (toolchain != null && modules.Count > 0)
        {
            var digestCalculator = new Sha256DigestCalculator();
            var graphBuilder = new ActionGraphBuilder(context, toolchain, digestCalculator);
            var graph = graphBuilder.Build(target, modules);
            CompileCommandsWriter.Write(graph, Path.Combine(workingDir, "compile_commands.json"));
            events?.Report(new OrchestratorEvent("Generated compile_commands.json", OrchestratorEventLevel.Success));
        }

        return true;
    }

    private static async Task GenerateVisualStudioAsync(
        string projectRoot,
        TargetRules target,
        IReadOnlyList<ModuleRules> modules,
        VisualStudioGenerator.VisualStudioVersion version,
        IProgress<OrchestratorEvent>? events)
    {
        var generator = new VisualStudioGenerator(projectRoot, version);
        await generator.GenerateAsync(target, modules);

        var solutionPath = Path.Combine(projectRoot, $"{target.Name}.sln");
        events?.Report(new OrchestratorEvent($"Generated Visual Studio solution: {solutionPath}", OrchestratorEventLevel.Success));
        events?.Report(new OrchestratorEvent($"{modules.Count} project(s) generated", OrchestratorEventLevel.Info));
        events?.Report(new OrchestratorEvent("Project files in: Intermediate/ProjectFiles/", OrchestratorEventLevel.Info));
    }

    private static async Task GenerateVSCodeAsync(
        string projectRoot,
        TargetRules target,
        IReadOnlyList<ModuleRules> modules,
        IProgress<OrchestratorEvent>? events)
    {
        var vscodeDir = Path.Combine(projectRoot, ".vscode");
        Directory.CreateDirectory(vscodeDir);

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

        events?.Report(new OrchestratorEvent("Generated VS Code configuration in .vscode/", OrchestratorEventLevel.Success));
        events?.Report(new OrchestratorEvent("c_cpp_properties.json (IntelliSense)", OrchestratorEventLevel.Info));
        events?.Report(new OrchestratorEvent("tasks.json (Build tasks)", OrchestratorEventLevel.Info));
        events?.Report(new OrchestratorEvent("launch.json (Debug configurations)", OrchestratorEventLevel.Info));
    }

    private static async Task GenerateCMakeAsync(
        string projectRoot,
        TargetRules target,
        IReadOnlyList<ModuleRules> modules,
        IProgress<OrchestratorEvent>? events)
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

                    sb.AppendLine($"target_include_directories({module.Name} PUBLIC");
                    sb.AppendLine($"    {sourceDir}");
                    foreach (var inc in module.PublicIncludePaths)
                    {
                        sb.AppendLine($"    {sourceDir}/{inc}");
                    }
                    sb.AppendLine(")");

                    if (module.PrivateIncludePaths.Count > 0)
                    {
                        sb.AppendLine($"target_include_directories({module.Name} PRIVATE");
                        foreach (var inc in module.PrivateIncludePaths)
                        {
                            sb.AppendLine($"    {sourceDir}/{inc}");
                        }
                        sb.AppendLine(")");
                    }

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

        sb.AppendLine("# Main executable");
        sb.AppendLine($"add_executable({target.Name}_exe");
        sb.AppendLine("    # Add your main.cpp here");
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

        events?.Report(new OrchestratorEvent("Generated CMakeLists.txt", OrchestratorEventLevel.Success));
        events?.Report(new OrchestratorEvent($"{modules.Count} module(s) configured", OrchestratorEventLevel.Info));
        events?.Report(new OrchestratorEvent("Use: cmake -B build && cmake --build build", OrchestratorEventLevel.Info));
    }
}
