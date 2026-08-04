// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Interfaces;
using Omen.Core.Rules;

namespace Omen.Core.Graph;

/// <summary>
/// Builds an action graph from module and target rules.
/// </summary>
public sealed class ActionGraphBuilder
{
    private readonly BuildContext _context;
    private readonly IToolchain _toolchain;
    private readonly IDigestCalculator _digestCalculator;
    private int _actionCounter;
    private Dictionary<string, ModuleRules> _moduleDict = new();

    public ActionGraphBuilder(BuildContext context, IToolchain toolchain, IDigestCalculator digestCalculator)
    {
        _context = context;
        _toolchain = toolchain;
        _digestCalculator = digestCalculator;
    }

    /// <summary>
    /// Builds an action graph for the given target and modules.
    /// </summary>
    public ActionGraph Build(TargetRules target, IReadOnlyList<ModuleRules> modules)
    {
        var graph = new ActionGraph();
        var moduleOutputs = new Dictionary<string, List<FileItem>>();
        var moduleCompileActions = new Dictionary<string, List<BuildAction>>();
        var independentModuleLibraries = new Dictionary<string, string>(); // module name -> library path for dependents

        _moduleDict = modules.ToDictionary(m => m.Name);

        var orderedModules = TopologicalSortModules(modules);
        var aggregateObjectFiles = new List<FileItem>();
        var aggregateCompileActions = new List<BuildAction>();

        foreach (var module in orderedModules)
        {
            var (objectFiles, compileActions) = BuildModuleActions(graph, module, target, moduleOutputs);
            moduleOutputs[module.Name] = objectFiles;
            moduleCompileActions[module.Name] = compileActions;

            if (LinksIndependently(module, target))
            {
                var libraryPath = BuildModuleBinaryAction(graph, module, objectFiles, compileActions, independentModuleLibraries);
                independentModuleLibraries[module.Name] = libraryPath;
            }
            else
            {
                aggregateObjectFiles.AddRange(objectFiles);
                aggregateCompileActions.AddRange(compileActions);
            }
        }

        BuildLinkAction(graph, target, modules, aggregateObjectFiles, aggregateCompileActions, independentModuleLibraries);

        graph.ComputePriorities();

        return graph;
    }

    /// <summary>
    /// True when a module is linked as its own independent binary rather than folded
    /// into the target's aggregate link. Monolithic targets fold every module regardless
    /// of BinaryType (see Task 4).
    /// </summary>
    private static bool LinksIndependently(ModuleRules module, TargetRules target) =>
        module.BinaryType.HasValue;

    private string BuildModuleBinaryAction(
        ActionGraph graph,
        ModuleRules module,
        List<FileItem> objectFiles,
        List<BuildAction> compileActions,
        Dictionary<string, string> independentModuleLibraries)
    {
        var outputExtension = module.BinaryType switch
        {
            TargetType.SharedLibrary => _toolchain.SharedLibraryExtension,
            TargetType.StaticLibrary => _toolchain.StaticLibraryExtension,
            _ => throw new InvalidOperationException(
                $"Module '{module.Name}' has BinaryType '{module.BinaryType}', but independently-linked modules only " +
                $"support {nameof(TargetType.SharedLibrary)} or {nameof(TargetType.StaticLibrary)}.")
        };
        var outputPath = Path.Combine(_context.OutputDirectory, module.Name + outputExtension);

        // A module dependency that is itself independently linked contributes its produced
        // library rather than its object files (those were already absorbed into its own
        // link/archive action).
        var dependencyLibraries = module.PublicDependencies.Concat(module.PrivateDependencies)
            .Select(depName => independentModuleLibraries.GetValueOrDefault(depName))
            .Where(path => path != null)
            .Select(path => path!);

        var linkRequest = new LinkRequest
        {
            ObjectFiles = objectFiles.Select(o => o.Path).ToList(),
            OutputFile = outputPath,
            OutputType = module.BinaryType!.Value,
            Configuration = _context.Configuration,
            Libraries = module.PublicLibraries.Concat(module.PrivateLibraries).Concat(dependencyLibraries).Distinct().ToList(),
            SystemLibraries = module.PublicSystemLibraries.Distinct().ToList(),
            GenerateDebugInfo = true
        };
        var commandLine = BuildLinkCommandLine(linkRequest);

        var action = new BuildAction
        {
            Id = GenerateActionId(),
            Type = module.BinaryType == TargetType.StaticLibrary ? ActionType.Archive : ActionType.Link,
            Description = $"Link {module.Name}",
            CommandLine = commandLine,
            WorkingDirectory = _context.ProjectRoot,
            Inputs = objectFiles,
            Outputs = [new FileItem { Path = outputPath }],
            ModuleName = module.Name,
            CanExecuteRemotely = false,
            EstimatedDuration = TimeSpan.FromSeconds(10),
            Environment = new Dictionary<string, string>(_toolchain.Environment)
        };

        foreach (var compileAction in compileActions)
        {
            action.Dependencies.Add(compileAction);
            compileAction.Dependents.Add(action);
        }

        // Ensure an independently-linked dependency is built before this module's own
        // link/archive action (same pattern used at the target level in BuildLinkAction).
        foreach (var depName in module.PublicDependencies.Concat(module.PrivateDependencies))
        {
            if (independentModuleLibraries.ContainsKey(depName))
            {
                LinkAfterModuleAction(graph, action, depName);
            }
        }

        graph.AddAction(action);

        // A shared library's linkable artifact on Windows is its import lib, not the DLL
        // itself; the toolchain places it alongside the DLL with the same base name.
        return module.BinaryType == TargetType.SharedLibrary
            ? Path.ChangeExtension(outputPath, _toolchain.StaticLibraryExtension)
            : outputPath;
    }

    /// <summary>
    /// Wires <paramref name="consumer"/> to depend on the link/archive action of the
    /// independently-linked module named <paramref name="moduleName"/>, so the dependency's
    /// binary is built before the action that links against it.
    /// </summary>
    private static void LinkAfterModuleAction(ActionGraph graph, BuildAction consumer, string moduleName)
    {
        var moduleAction = graph.Actions.FirstOrDefault(a => a.ModuleName == moduleName && a.Type is ActionType.Link or ActionType.Archive);
        if (moduleAction != null)
        {
            consumer.Dependencies.Add(moduleAction);
            moduleAction.Dependents.Add(consumer);
        }
    }

    private (List<FileItem> ObjectFiles, List<BuildAction> Actions) BuildModuleActions(
        ActionGraph graph,
        ModuleRules module,
        TargetRules target,
        Dictionary<string, List<FileItem>> moduleOutputs)
    {
        var objectFiles = new List<FileItem>();
        var compileActions = new List<BuildAction>();
        var sourceDir = module.SourceDirectory ?? $"Source/{module.Name}";
        var fullSourceDir = Path.Combine(_context.ProjectRoot, sourceDir);

        if (!Directory.Exists(fullSourceDir))
        {
            return (objectFiles, compileActions);
        }

        // Collect source files
        var sourceFiles = Directory.GetFiles(fullSourceDir, "*.cpp", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(fullSourceDir, "*.c", SearchOption.AllDirectories))
            .ToList();

        // Build include paths (resolving dependencies)
        var includePaths = BuildIncludePaths(module);

        // Build definitions (including dependency definitions)
        var definitions = BuildDefinitions(module, target);

        // PCH action if needed
        BuildAction? pchAction = null;
        string? pchFile = null;
        if (module.PCHUsage != PCHUsage.None && module.PrecompiledHeaderFile != null)
        {
            (pchAction, pchFile) = CreatePCHAction(graph, module, includePaths, definitions);
        }

        // Create compile actions for each source file
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(_context.ProjectRoot, sourceFile);
            var objectFile = GetObjectFilePath(module, relativePath);

            var inputFiles = new List<FileItem>
            {
                new() { Path = sourceFile }
            };

            // Add PCH as input if present
            if (pchFile != null)
            {
                inputFiles.Add(new FileItem { Path = pchFile });
            }

            var outputFile = new FileItem { Path = objectFile };

            var compileRequest = new CompileRequest
            {
                SourceFile = sourceFile,
                OutputFile = objectFile,
                Configuration = _context.Configuration,
                IncludePaths = includePaths,
                Definitions = definitions,
                CppStandard = module.CppStandard,
                Optimization = GetOptimizationLevel(module, target),
                WarningLevel = module.WarningLevel,
                TreatWarningsAsErrors = module.TreatWarningsAsErrors,
                EnableRTTI = module.EnableRTTI,
                EnableExceptions = module.EnableExceptions,
                GenerateDebugInfo = target.GenerateDebugInfo,
                PrecompiledHeader = pchFile,
                AdditionalFlags = module.AdditionalCompilerFlags.ToList()
            };

            var commandLine = BuildCompileCommandLine(compileRequest);

            var action = new BuildAction
            {
                Id = GenerateActionId(),
                Type = ActionType.Compile,
                Description = $"Compile {Path.GetFileName(sourceFile)}",
                CommandLine = commandLine,
                WorkingDirectory = _context.ProjectRoot,
                Inputs = inputFiles,
                Outputs = [outputFile],
                ModuleName = module.Name,
                CanExecuteRemotely = true,
                EstimatedDuration = TimeSpan.FromSeconds(3),
                Environment = new Dictionary<string, string>(_toolchain.Environment)
            };

            // Add dependency on PCH action
            if (pchAction != null)
            {
                action.Dependencies.Add(pchAction);
                pchAction.Dependents.Add(action);
            }

            graph.AddAction(action);
            compileActions.Add(action);
            objectFiles.Add(outputFile);
        }

        return (objectFiles, compileActions);
    }

    private (BuildAction Action, string PchFile) CreatePCHAction(
        ActionGraph graph,
        ModuleRules module,
        IReadOnlyList<string> includePaths,
        IReadOnlyList<string> definitions)
    {
        var pchHeader = Path.Combine(_context.ProjectRoot, module.SourceDirectory ?? $"Source/{module.Name}", module.PrecompiledHeaderFile!);
        var pchOutput = Path.Combine(_context.IntermediateDirectory, module.Name,
            Path.GetFileNameWithoutExtension(module.PrecompiledHeaderFile!) + ".pch");

        var compileRequest = new CompileRequest
        {
            SourceFile = pchHeader,
            OutputFile = pchOutput,
            Configuration = _context.Configuration,
            IncludePaths = includePaths,
            Definitions = definitions,
            CppStandard = module.CppStandard,
            Optimization = OptimizationLevel.Debug,
            WarningLevel = module.WarningLevel,
            EnableRTTI = module.EnableRTTI,
            EnableExceptions = module.EnableExceptions,
            CreatePrecompiledHeader = true
        };

        var commandLine = BuildCompileCommandLine(compileRequest);

        var action = new BuildAction
        {
            Id = GenerateActionId(),
            Type = ActionType.GeneratePCH,
            Description = $"Generate PCH {Path.GetFileName(module.PrecompiledHeaderFile)}",
            CommandLine = commandLine,
            WorkingDirectory = _context.ProjectRoot,
            Inputs = [new FileItem { Path = pchHeader }],
            Outputs = [new FileItem { Path = pchOutput }],
            ModuleName = module.Name,
            CanExecuteRemotely = false, // PCH generation is usually local
            EstimatedDuration = TimeSpan.FromSeconds(10),
            Environment = new Dictionary<string, string>(_toolchain.Environment)
        };

        graph.AddAction(action);
        return (action, pchOutput);
    }

    private void BuildLinkAction(
        ActionGraph graph,
        TargetRules target,
        IReadOnlyList<ModuleRules> modules,
        List<FileItem> aggregateObjectFiles,
        List<BuildAction> aggregateCompileActions,
        Dictionary<string, string> independentModuleLibraries)
    {
        if (aggregateObjectFiles.Count == 0 && independentModuleLibraries.Count == 0) return;

        var outputName = target.OutputName ?? target.Name;
        var outputExtension = target.Type switch
        {
            TargetType.Executable => _toolchain.ExecutableExtension,
            TargetType.SharedLibrary => _toolchain.SharedLibraryExtension,
            TargetType.StaticLibrary => _toolchain.StaticLibraryExtension,
            _ => ""
        };

        var outputPath = Path.Combine(
            target.OutputDirectory ?? _context.OutputDirectory,
            outputName + outputExtension);

        var libraries = modules.SelectMany(m => m.PublicLibraries.Concat(m.PrivateLibraries))
            .Concat(independentModuleLibraries.Values)
            .Distinct().ToList();
        var systemLibraries = modules.SelectMany(m => m.PublicSystemLibraries).Distinct().ToList();
        var frameworks = modules.SelectMany(m => m.PublicFrameworks).Distinct().ToList();
        var linkerFlags = modules.SelectMany(m => m.AdditionalLinkerFlags).Distinct().ToList();

        var linkRequest = new LinkRequest
        {
            ObjectFiles = aggregateObjectFiles.Select(o => o.Path).ToList(),
            OutputFile = outputPath,
            OutputType = target.Type,
            Configuration = _context.Configuration,
            Libraries = libraries,
            SystemLibraries = systemLibraries,
            Frameworks = frameworks,
            GenerateDebugInfo = target.GenerateDebugInfo,
            IncrementalLinking = target.UseIncrementalLinking,
            EnableLTO = target.EnableLTO,
            AdditionalFlags = linkerFlags
        };

        var commandLine = BuildLinkCommandLine(linkRequest);

        var linkAction = new BuildAction
        {
            Id = GenerateActionId(),
            Type = target.Type == TargetType.StaticLibrary ? ActionType.Archive : ActionType.Link,
            Description = $"Link {outputName}",
            CommandLine = commandLine,
            WorkingDirectory = _context.ProjectRoot,
            Inputs = aggregateObjectFiles,
            Outputs = [new FileItem { Path = outputPath }],
            CanExecuteRemotely = false,
            EstimatedDuration = TimeSpan.FromSeconds(10),
            Environment = new Dictionary<string, string>(_toolchain.Environment)
        };

        foreach (var compileAction in aggregateCompileActions)
        {
            linkAction.Dependencies.Add(compileAction);
            compileAction.Dependents.Add(linkAction);
        }

        // Ensure independent module binaries are linked/archived before the target that
        // consumes them, even though the target doesn't compile their objects itself.
        foreach (var moduleName in independentModuleLibraries.Keys)
        {
            LinkAfterModuleAction(graph, linkAction, moduleName);
        }

        graph.AddAction(linkAction);
    }

    private IReadOnlyList<string> BuildIncludePaths(ModuleRules module)
    {
        var paths = new List<string>();
        var visited = new HashSet<string>();

        void AddModuleIncludePaths(ModuleRules mod, bool includePrivate)
        {
            if (visited.Contains(mod.Name))
                return;
            visited.Add(mod.Name);

            var sourceDir = mod.SourceDirectory ?? $"Source/{mod.Name}";
            var baseDir = Path.Combine(_context.ProjectRoot, sourceDir);

            // Add module's source directory
            paths.Add(baseDir);

            // Add Public subdirectory if it exists
            var publicDir = Path.Combine(baseDir, "Public");
            if (Directory.Exists(publicDir))
            {
                paths.Add(publicDir);
            }

            // Add Private subdirectory only for the main module
            if (includePrivate)
            {
                var privateDir = Path.Combine(baseDir, "Private");
                if (Directory.Exists(privateDir))
                {
                    paths.Add(privateDir);
                }
            }

            // Add explicit public include paths (relative to module source dir)
            foreach (var p in mod.PublicIncludePaths)
            {
                var fullPath = Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);
                if (!paths.Contains(fullPath))
                    paths.Add(fullPath);
            }

            // Add explicit private include paths only for main module
            if (includePrivate)
            {
                foreach (var p in mod.PrivateIncludePaths)
                {
                    var fullPath = Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);
                    if (!paths.Contains(fullPath))
                        paths.Add(fullPath);
                }
            }

            // Recursively add dependencies' public include paths
            foreach (var depName in mod.PublicDependencies.Concat(mod.PrivateDependencies))
            {
                if (_moduleDict.TryGetValue(depName, out var depModule))
                {
                    AddModuleIncludePaths(depModule, false); // Only public paths for dependencies
                }
            }
        }

        // Start with the main module (include private paths)
        AddModuleIncludePaths(module, true);

        return paths;
    }

    private IReadOnlyList<string> BuildDefinitions(ModuleRules module, TargetRules target)
    {
        var defs = new List<string>();

        // Configuration definitions
        defs.Add($"OMEN_{_context.Configuration.ToString().ToUpperInvariant()}=1");
        defs.Add($"OMEN_{_context.Platform.ToString().ToUpperInvariant()}=1");

        // Target definitions
        defs.AddRange(target.GlobalDefinitions);

        // Module definitions
        defs.AddRange(module.PublicDefinitions);
        defs.AddRange(module.PrivateDefinitions);

        // Add public definitions from dependencies
        var visited = new HashSet<string>();
        void AddDependencyDefinitions(ModuleRules mod)
        {
            if (visited.Contains(mod.Name))
                return;
            visited.Add(mod.Name);

            foreach (var depName in mod.PublicDependencies.Concat(mod.PrivateDependencies))
            {
                if (_moduleDict.TryGetValue(depName, out var depModule))
                {
                    defs.AddRange(depModule.PublicDefinitions);
                    AddDependencyDefinitions(depModule);
                }
            }
        }
        AddDependencyDefinitions(module);

        return defs.Distinct().ToList();
    }

    private OptimizationLevel GetOptimizationLevel(ModuleRules module, TargetRules target)
    {
        if (module.OptimizeCode.HasValue)
            return module.OptimizeCode.Value;

        return _context.Configuration switch
        {
            BuildConfiguration.Debug => OptimizationLevel.Disabled,
            BuildConfiguration.Development => OptimizationLevel.Debug,
            BuildConfiguration.Release => OptimizationLevel.Shipping,
            BuildConfiguration.Shipping => OptimizationLevel.Shipping,
            _ => OptimizationLevel.Debug
        };
    }

    private string GetObjectFilePath(ModuleRules module, string sourceRelativePath)
    {
        var objectName = Path.ChangeExtension(Path.GetFileName(sourceRelativePath), _toolchain.ObjectFileExtension);
        return Path.Combine(_context.IntermediateDirectory, module.Name, objectName);
    }

    private string BuildCompileCommandLine(CompileRequest request)
    {
        var args = new List<string>();

        // Use MSVC-style flags for Windows, GCC-style otherwise
        if (_context.Platform == TargetPlatform.Windows)
        {
            args.Add("/nologo");
            args.Add("/c");
            args.Add($"/Fo\"{request.OutputFile}\"");
            args.Add($"\"{request.SourceFile}\"");

            // C++ standard
            args.Add(request.CppStandard switch
            {
                CppStandard.Cpp14 => "/std:c++14",
                CppStandard.Cpp17 => "/std:c++17",
                CppStandard.Cpp20 => "/std:c++20",
                CppStandard.Cpp23 => "/std:c++latest",
                CppStandard.Latest => "/std:c++latest",
                _ => "/std:c++20"
            });

            // Optimization
            args.Add(request.Optimization switch
            {
                OptimizationLevel.Disabled => "/Od",
                OptimizationLevel.Debug => "/Od",
                OptimizationLevel.Development => "/O1",
                OptimizationLevel.Shipping => "/O2",
                OptimizationLevel.Size => "/Os",
                OptimizationLevel.SizeAndSpeed => "/Ox",
                _ => "/Od"
            });

            // Debug info
            if (request.GenerateDebugInfo)
            {
                args.Add("/Zi");
                args.Add($"/Fd\"{Path.ChangeExtension(request.OutputFile, ".pdb")}\"");
            }

            // Warning level
            args.Add(request.WarningLevel switch
            {
                WarningLevel.Off => "/W0",
                WarningLevel.Level1 => "/W1",
                WarningLevel.Level2 => "/W2",
                WarningLevel.Level3 => "/W3",
                WarningLevel.Level4 => "/W4",
                WarningLevel.EnableAll => "/Wall",
                _ => "/W4"
            });

            if (request.TreatWarningsAsErrors) args.Add("/WX");
            args.Add(request.EnableRTTI ? "/GR" : "/GR-");
            if (request.EnableExceptions) args.Add("/EHsc");

            // Runtime library
            args.Add(request.Configuration == BuildConfiguration.Debug ? "/MDd" : "/MD");

            // Include paths
            foreach (var include in request.IncludePaths)
                args.Add($"/I\"{include}\"");

            // Definitions
            foreach (var def in request.Definitions)
                args.Add($"/D{def}");

            // PCH
            if (request.CreatePrecompiledHeader)
            {
                args.Add($"/Yc\"{Path.GetFileName(request.SourceFile)}\"");
                args.Add($"/Fp\"{request.OutputFile}\"");
            }
            else if (request.PrecompiledHeader != null)
            {
                args.Add($"/Yu\"{Path.GetFileName(request.PrecompiledHeader)}\"");
                args.Add($"/Fp\"{request.PrecompiledHeader}\"");
            }

            foreach (var flag in request.AdditionalFlags)
                args.Add(flag);
        }
        else
        {
            // GCC/Clang style
            args.Add("-c");
            args.Add($"-o \"{request.OutputFile}\"");
            args.Add($"\"{request.SourceFile}\"");

            args.Add(request.CppStandard switch
            {
                CppStandard.Cpp14 => "-std=c++14",
                CppStandard.Cpp17 => "-std=c++17",
                CppStandard.Cpp20 => "-std=c++20",
                CppStandard.Cpp23 => "-std=c++23",
                CppStandard.Latest => "-std=c++2b",
                _ => "-std=c++20"
            });

            args.Add(request.Optimization switch
            {
                OptimizationLevel.Disabled => "-O0",
                OptimizationLevel.Debug => "-O0 -g",
                OptimizationLevel.Development => "-O1",
                OptimizationLevel.Shipping => "-O3",
                OptimizationLevel.Size => "-Os",
                OptimizationLevel.SizeAndSpeed => "-O2",
                _ => "-O0"
            });

            if (request.GenerateDebugInfo) args.Add("-g");
            if (!request.EnableRTTI) args.Add("-fno-rtti");
            if (!request.EnableExceptions) args.Add("-fno-exceptions");
            if (request.TreatWarningsAsErrors) args.Add("-Werror");

            foreach (var include in request.IncludePaths)
                args.Add($"-I\"{include}\"");

            foreach (var def in request.Definitions)
                args.Add($"-D{def}");

            foreach (var flag in request.AdditionalFlags)
                args.Add(flag);
        }

        return $"\"{_toolchain.CompilerPath}\" {string.Join(" ", args)}";
    }

    private string BuildLinkCommandLine(LinkRequest request)
    {
        var args = new List<string>();

        if (_context.Platform == TargetPlatform.Windows)
        {
            args.Add("/nologo");
            args.Add($"/OUT:\"{request.OutputFile}\"");

            if (request.OutputType == TargetType.SharedLibrary)
                args.Add("/DLL");

            if (request.GenerateDebugInfo)
            {
                args.Add("/DEBUG");
                args.Add($"/PDB:\"{Path.ChangeExtension(request.OutputFile, ".pdb")}\"");
            }

            args.Add(request.IncrementalLinking ? "/INCREMENTAL" : "/INCREMENTAL:NO");

            if (request.EnableLTO) args.Add("/LTCG");

            foreach (var libPath in request.LibraryPaths)
                args.Add($"/LIBPATH:\"{libPath}\"");

            foreach (var obj in request.ObjectFiles)
                args.Add($"\"{obj}\"");

            foreach (var lib in request.Libraries)
                args.Add(lib.EndsWith(".lib") ? $"\"{lib}\"" : $"{lib}.lib");

            foreach (var lib in request.SystemLibraries)
                args.Add(lib.EndsWith(".lib") ? lib : $"{lib}.lib");

            foreach (var flag in request.AdditionalFlags)
                args.Add(flag);
        }
        else
        {
            args.Add($"-o \"{request.OutputFile}\"");

            if (request.OutputType == TargetType.SharedLibrary)
                args.Add("-shared");

            if (request.GenerateDebugInfo) args.Add("-g");
            if (request.EnableLTO) args.Add("-flto");

            foreach (var libPath in request.LibraryPaths)
                args.Add($"-L\"{libPath}\"");

            foreach (var obj in request.ObjectFiles)
                args.Add($"\"{obj}\"");

            foreach (var lib in request.Libraries)
                args.Add($"-l{lib}");

            foreach (var lib in request.SystemLibraries)
                args.Add($"-l{lib}");

            foreach (var framework in request.Frameworks)
                args.Add($"-framework {framework}");

            foreach (var flag in request.AdditionalFlags)
                args.Add(flag);
        }

        return $"\"{_toolchain.LinkerPath}\" {string.Join(" ", args)}";
    }

    private string GenerateActionId() => $"action_{Interlocked.Increment(ref _actionCounter):D6}";

    private IReadOnlyList<ModuleRules> TopologicalSortModules(IReadOnlyList<ModuleRules> modules)
    {
        var moduleDict = modules.ToDictionary(m => m.Name);
        var result = new List<ModuleRules>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Visit(ModuleRules module)
        {
            if (visited.Contains(module.Name))
                return;
            if (visiting.Contains(module.Name))
                throw new InvalidOperationException($"Circular dependency detected: {module.Name}");

            visiting.Add(module.Name);

            foreach (var depName in module.PublicDependencies.Concat(module.PrivateDependencies))
            {
                if (moduleDict.TryGetValue(depName, out var dep))
                {
                    Visit(dep);
                }
            }

            visiting.Remove(module.Name);
            visited.Add(module.Name);
            result.Add(module);
        }

        foreach (var module in modules)
        {
            Visit(module);
        }

        return result;
    }
}
