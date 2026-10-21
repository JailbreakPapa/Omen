// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.RegularExpressions;
using Omen.Core.Configuration;
using Omen.Core.Interfaces;
using Omen.Core.Build;
using Omen.Core.Qt;
using Omen.Core.Reflect;
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
    /// <summary>
    /// The scanner that turns each translation unit's #includes into action inputs. A caller
    /// that wants incremental builds to be cheap hands in one primed from the previous run
    /// (IncludeScanner.LoadFrom) and saves it afterwards.
    /// </summary>
    public IncludeScanner IncludeScanner { get; init; } = new();

    /// <summary>
    /// False skips include scanning entirely. Only the compile command lines need the graph
    /// then - project generation writing compile_commands.json spent 137 of its 168 seconds
    /// scanning headers it never looked at.
    /// </summary>
    public bool ScanIncludes { get; init; } = true;
    private int _actionCounter;
    private Dictionary<string, ModuleRules> _moduleDict = new();
    private readonly Dictionary<string, string> _binaryNameOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<BuildAction>> _codeGenActionsByModule = new();

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
        _binaryNameOwners.Clear();
        _codeGenActionsByModule.Clear();

        var orderedModules = TopologicalSortModules(modules);
        var aggregateObjectFiles = new List<FileItem>();
        var aggregateCompileActions = new List<BuildAction>();
        var monolithicModuleNames = new List<string>();
        var objectLibraryNames = FindObjectLibraries(modules, target);

        foreach (var module in orderedModules)
        {
            var (objectFiles, compileActions) = BuildModuleActions(graph, module, target, moduleOutputs);
            moduleOutputs[module.Name] = objectFiles;
            moduleCompileActions[module.Name] = compileActions;

            if (LinksIndependently(module, target))
            {
                var libraryPath = BuildModuleBinaryAction(graph, module, objectFiles, compileActions, independentModuleLibraries, moduleOutputs, moduleCompileActions);

                // An Executable module is a program, not something anything links against -
                // registering it here would put an .exe on a dependent's linker command line.
                // A module that needs the program to exist declares it as a runtime dependency,
                // which BuildRuntimeDependencyCopyActions orders separately.
                if (module.BinaryType != TargetType.Executable)
                    independentModuleLibraries[module.Name] = libraryPath;
            }
            else if (objectLibraryNames.Contains(module.Name))
            {
                // An object library's objects reach each consumer's own link, so adding them to
                // the target's link as well would put the same code in two binaries.
            }
            else
            {
                aggregateObjectFiles.AddRange(objectFiles);
                aggregateCompileActions.AddRange(compileActions);

                if (target.LinkType == LinkType.Monolithic && module.BinaryType == TargetType.SharedLibrary)
                {
                    monolithicModuleNames.Add(module.Name);
                }
            }
        }

        var registrationAction = BuildStaticModuleRegistrationAction(graph, monolithicModuleNames);
        if (registrationAction != null)
        {
            aggregateObjectFiles.AddRange(registrationAction.Outputs);
            aggregateCompileActions.Add(registrationAction);
        }

        BuildLinkAction(graph, target, modules, aggregateObjectFiles, aggregateCompileActions, independentModuleLibraries);
        BuildRuntimeDependencyCopyActions(graph, target, modules);

        graph.ComputePriorities();

        return graph;
    }

    /// <summary>
    /// Stages a module's RUNTIME_DEPENDENCIES next to the target's output, for every
    /// dependency name Omen can actually resolve to an artifact it built: a SharedLibrary
    /// module that is part of this same Build() call. Anything else - an external
    /// target/plugin Omen doesn't itself compile in this build, a name that doesn't resolve at
    /// all, a StaticLibrary/HeaderOnly dependency with no runtime artifact - is left as a
    /// recorded-only entry on ModuleRules.RuntimeDependencies; see its doc comment.
    /// </summary>
    private void BuildRuntimeDependencyCopyActions(ActionGraph graph, TargetRules target, IReadOnlyList<ModuleRules> modules)
    {
        var targetOutputDirectory = target.OutputDirectory ?? _context.OutputDirectory;

        foreach (var module in modules)
        {
            foreach (var dependency in module.RuntimeDependencies)
            {
                var depModuleName = dependency.StartsWith("Plugin::", StringComparison.Ordinal)
                    ? dependency["Plugin::".Length..]
                    : dependency;

                if (!_moduleDict.TryGetValue(depModuleName, out var depModule) ||
                    depModule.BinaryType != TargetType.SharedLibrary)
                {
                    continue;
                }

                var sourcePath = Path.Combine(_context.OutputDirectory, ModulePathName.BinaryName(depModule.Name) + _toolchain.SharedLibraryExtension);
                var destinationPath = Path.Combine(targetOutputDirectory, ModulePathName.BinaryName(depModule.Name) + _toolchain.SharedLibraryExtension);
                if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue; // already builds straight into the target's own output directory
                }

                var copyCommand = OperatingSystem.IsWindows()
                    ? $"copy /Y \"{sourcePath}\" \"{destinationPath}\""
                    : $"cp -f '{sourcePath}' '{destinationPath}'";

                var copyAction = new BuildAction
                {
                    Id = GenerateActionId(),
                    Type = ActionType.Copy,
                    Description = $"Stage runtime dependency {depModule.Name} for {target.Name}",
                    CommandLine = copyCommand,
                    WorkingDirectory = _context.ProjectRoot,
                    Inputs = [new FileItem { Path = sourcePath }],
                    Outputs = [new FileItem { Path = destinationPath }],
                    ModuleName = module.Name,
                    CanExecuteRemotely = false,
                    EstimatedDuration = TimeSpan.FromSeconds(1),
                    Environment = new Dictionary<string, string>(_toolchain.Environment)
                };

                LinkAfterModuleAction(graph, copyAction, depModule.Name);
                graph.AddAction(copyAction);
            }

            // RuntimeFiles are concrete prebuilt files (a vcpkg port's DLLs, an O3DE
            // package's ly_add_target_files entries) - staged as-is, no module resolution.
            foreach (var runtimeFile in module.RuntimeFiles)
            {
                var destinationPath = Path.Combine(targetOutputDirectory, Path.GetFileName(runtimeFile));
                if (!File.Exists(runtimeFile) ||
                    string.Equals(Path.GetFullPath(runtimeFile), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                graph.AddAction(new BuildAction
                {
                    Id = GenerateActionId(),
                    Type = ActionType.Copy,
                    Description = $"Stage {Path.GetFileName(runtimeFile)} for {target.Name}",
                    CommandLine = OperatingSystem.IsWindows()
                        ? $"copy /Y \"{runtimeFile}\" \"{destinationPath}\""
                        : $"cp -f '{runtimeFile}' '{destinationPath}'",
                    WorkingDirectory = _context.ProjectRoot,
                    Inputs = [new FileItem { Path = runtimeFile }],
                    Outputs = [new FileItem { Path = destinationPath }],
                    ModuleName = module.Name,
                    CanExecuteRemotely = false,
                    EstimatedDuration = TimeSpan.FromSeconds(1),
                    Environment = new Dictionary<string, string>(_toolchain.Environment)
                });
            }
        }
    }

    /// <summary>
    /// True when a module is linked as its own independent binary rather than folded
    /// into the target's aggregate link. Monolithic targets fold modules that would
    /// otherwise be independent shared libraries back into the aggregate link (see Task 4);
    /// independent static-library modules remain independent regardless of LinkType.
    /// HeaderOnly (O3DE INTERFACE targets, e.g. a plugin's `.API` flavor) never links
    /// independently - it has BinaryType set (so BuildIncludePaths/BuildDefinitions can walk
    /// it as a real dependency) but produces no artifact of its own, so it's always folded
    /// into the aggregate (where it contributes nothing, since it also compiles nothing).
    /// </summary>
    private static bool LinksIndependently(ModuleRules module, TargetRules target) =>
        module.BinaryType.HasValue &&
        module.BinaryType != TargetType.HeaderOnly &&
        !(target.LinkType == LinkType.Monolithic && module.BinaryType == TargetType.SharedLibrary);

    /// <summary>
    /// A module with no BinaryType that an independently-linked module depends on: O3DE's
    /// `ly_add_target(NAME <x>.Private.Object OBJECT)`, whose objects are compiled once and
    /// absorbed by each consumer's link rather than producing an artifact of their own. Atom uses
    /// the pattern to share one set of private objects between a plugin module and its test module.
    /// </summary>
    private HashSet<string> FindObjectLibraries(IReadOnlyList<ModuleRules> modules, TargetRules target) =>
        modules
            .Where(m => LinksIndependently(m, target))
            .SelectMany(m => m.AllDependencies)
            .Where(name => _moduleDict.TryGetValue(name, out var dep) && !dep.BinaryType.HasValue)
            .ToHashSet();

    private string BuildModuleBinaryAction(
        ActionGraph graph,
        ModuleRules module,
        List<FileItem> objectFiles,
        List<BuildAction> compileActions,
        Dictionary<string, string> independentModuleLibraries,
        Dictionary<string, List<FileItem>> moduleOutputs,
        Dictionary<string, List<BuildAction>> moduleCompileActions)
    {
        var outputExtension = module.BinaryType switch
        {
            TargetType.SharedLibrary => _toolchain.SharedLibraryExtension,
            TargetType.StaticLibrary => _toolchain.StaticLibraryExtension,
            // An Executable module is a program in its own right - O3DE's `ly_add_target ...
            // EXECUTABLE` (AssetProcessorBatch, AzTestRunner, ProcessLaunchTest, ...). It links
            // exactly like a shared library minus /DLL or -shared, which BuildLinkCommandLine
            // already does off OutputType, so only the extension needed teaching.
            TargetType.Executable => _toolchain.ExecutableExtension,
            _ => throw new InvalidOperationException(
                $"Module '{module.Name}' has BinaryType '{module.BinaryType}', but independently-linked modules only " +
                $"support {nameof(TargetType.SharedLibrary)}, {nameof(TargetType.StaticLibrary)} or " +
                $"{nameof(TargetType.Executable)}.")
        };
        // Dropping the namespace qualifier means two modules whose leaf names match would write
        // the same file, and the second link would silently replace the first.
        var binaryName = ModulePathName.BinaryName(module.Name) + outputExtension;
        if (_binaryNameOwners.TryGetValue(binaryName, out var owner) && owner != module.Name)
        {
            throw new InvalidOperationException(
                $"Modules '{owner}' and '{module.Name}' both produce '{binaryName}'. Module names may " +
                $"differ only by namespace qualifier, which the artifact name does not carry - rename one.");
        }
        _binaryNameOwners[binaryName] = module.Name;

        var outputPath = Path.Combine(_context.OutputDirectory, binaryName);

        // A module dependency that is itself independently linked contributes its produced
        // library rather than its object files (those were already absorbed into its own
        // link/archive action). A dependency name that isn't a known module at all is not our
        // concern here (handled elsewhere). But a dependency that IS a known module and simply
        // has no BinaryType set is folded into some other aggregate link rather than producing
        // its own artifact - silently dropping it would mean missing symbols at link time, so
        // fail loudly instead.
        var (dependencyLibraries, dependencySystemLibraries, linkedModules) =
            CollectDependencyLibraries(module, independentModuleLibraries);

        // A dependency with no artifact of its own is an object library: its objects belong in
        // this module's link, the way CMake's OBJECT libraries are absorbed by each consumer.
        // Modules are topologically sorted, so a dependency's objects already exist here.
        var absorbedObjects = new List<FileItem>();
        var absorbedCompileActions = new List<BuildAction>();
        foreach (var depName in module.AllDependencies)
        {
            if (independentModuleLibraries.ContainsKey(depName) ||
                !_moduleDict.TryGetValue(depName, out var depModule) ||
                depModule.BinaryType.HasValue)
            {
                continue;
            }

            if (moduleOutputs.TryGetValue(depName, out var depObjects))
            {
                absorbedObjects.AddRange(depObjects);
            }

            if (moduleCompileActions.TryGetValue(depName, out var depActions))
            {
                absorbedCompileActions.AddRange(depActions);
            }
        }

        if (absorbedObjects.Count > 0)
        {
            objectFiles = [.. objectFiles, .. absorbedObjects];
            compileActions = [.. compileActions, .. absorbedCompileActions];
        }

        string commandLine;
        if (module.BinaryType == TargetType.StaticLibrary)
        {
            commandLine = BuildArchiveCommandLine(new ArchiveRequest
            {
                ObjectFiles = objectFiles.Select(o => o.Path).ToList(),
                OutputFile = outputPath
            });
        }
        else
        {
            var linkRequest = new LinkRequest
            {
                ObjectFiles = objectFiles.Select(o => o.Path).ToList(),
                OutputFile = outputPath,
                OutputType = module.BinaryType!.Value,
                Configuration = _context.Configuration,
                Libraries = module.PublicLibraries.Concat(module.PrivateLibraries).Concat(dependencyLibraries).Distinct().ToList(),
                SystemLibraries = module.PublicSystemLibraries.Concat(dependencySystemLibraries).Distinct().ToList(),
                // Engine-wide flags first, the module's own after: the target link's last-flag-wins order.
                AdditionalFlags = _context.GlobalLinkerOptions.Concat(module.AdditionalLinkerFlags).Distinct().ToList(),
                GenerateDebugInfo = true
            };
            commandLine = BuildLinkCommandLine(linkRequest);
        }

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

        // Order after every independently-linked module the command line names - the same
        // walk that put them there, not the direct list. A library reached only through an
        // object library or a header-only module has no direct dependency to hang the edge
        // on; Atom_RPI.Builders opened NightFoxToolsFramework.Static.lib while lib.exe was
        // still writing it.
        foreach (var depName in linkedModules)
        {
            LinkAfterModuleAction(graph, action, depName);
        }

        graph.AddAction(action);

        // A shared library's linkable artifact on Windows is its import lib, not the DLL
        // itself; the toolchain places it alongside the DLL with the same base name.
        return module.BinaryType == TargetType.SharedLibrary
            ? Path.ChangeExtension(outputPath, _toolchain.StaticLibraryExtension)
            : outputPath;
    }

    /// <summary>
    /// Generates (only when its content actually changed) a source file listing every
    /// module folded into a monolithic link, and a compile action for it. This is Omen's
    /// equivalent of O3DE's StaticModules.inl: a generic name table, not tied to any
    /// specific engine's module-registration mechanism.
    /// </summary>
    private BuildAction? BuildStaticModuleRegistrationAction(ActionGraph graph, List<string> monolithicModuleNames)
    {
        if (monolithicModuleNames.Count == 0) return null;

        var entries = string.Join(",\n    ", monolithicModuleNames.Select(n => $"\"{n}\""));
        var source =
            "// Generated by Omen. Do not edit.\n" +
            "extern \"C\" const char* const OmenStaticModules[] = {\n" +
            $"    {entries},\n" +
            "    nullptr\n" +
            "};\n" +
            $"extern \"C\" const int OmenStaticModuleCount = {monolithicModuleNames.Count};\n";

        Directory.CreateDirectory(_context.IntermediateDirectory);
        var sourcePath = Path.Combine(_context.IntermediateDirectory, "StaticModuleRegistration.g.cpp");
        if (!File.Exists(sourcePath) || File.ReadAllText(sourcePath) != source)
        {
            File.WriteAllText(sourcePath, source);
        }

        var objectFile = Path.Combine(_context.IntermediateDirectory, "StaticModuleRegistration" + _toolchain.ObjectFileExtension);
        var compileRequest = new CompileRequest
        {
            SourceFile = sourcePath,
            OutputFile = objectFile,
            Configuration = _context.Configuration,
            CppStandard = CppStandard.Cpp20
        };

        var action = new BuildAction
        {
            Id = GenerateActionId(),
            Type = ActionType.Compile,
            Description = "Compile StaticModuleRegistration.g.cpp",
            CommandLine = BuildCompileCommandLine(compileRequest),
            WorkingDirectory = _context.ProjectRoot,
            Inputs = [new FileItem { Path = sourcePath }],
            Outputs = [new FileItem { Path = objectFile }],
            Environment = new Dictionary<string, string>(_toolchain.Environment)
        };
        graph.AddAction(action);
        return action;
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

    /// <summary>
    /// Every library an independently-linked module's own link needs, walked transitively.
    ///
    /// Transitive because linking is not include visibility: NightFoxFramework depends on
    /// NightFoxNetworking, which depends on the OpenSSL package, and the framework's DLL is where
    /// libssl's symbols are actually resolved - a one-level walk left 117 unresolved externals
    /// naming a library nothing on the command line had ever mentioned. Over-linking is free; the
    /// linker drops what it does not need. System libraries travel the same way, which is how
    /// ws2_32 reaches the module that calls getaddrinfo through two layers of dependency.
    /// </summary>
    private (List<string> Libraries, List<string> SystemLibraries, List<string> LinkedModules) CollectDependencyLibraries(
        ModuleRules module, Dictionary<string, string> independentModuleLibraries)
    {
        var libraries = new List<string>();
        var systemLibraries = new List<string>();
        var linkedModules = new List<string>();
        var visited = new HashSet<string> { module.Name };

        void Walk(ModuleRules current)
        {
            foreach (var depName in current.AllDependencies)
            {
                if (!visited.Add(depName))
                    continue;

                // A dependency's own prebuilt libraries travel with it. This is how a vendored
                // third-party package works: a HeaderOnly module that compiles nothing and exists
                // only to contribute include paths and an already-built .lib.
                if (_moduleDict.TryGetValue(depName, out var dependency))
                {
                    libraries.AddRange(dependency.PublicLibraries);
                    systemLibraries.AddRange(dependency.PublicSystemLibraries);
                    Walk(dependency);
                }

                if (independentModuleLibraries.TryGetValue(depName, out var libraryPath))
                {
                    libraries.Add(libraryPath);
                    linkedModules.Add(depName);
                }
            }
        }

        Walk(module);
        return (libraries, systemLibraries, linkedModules);
    }

    private (List<FileItem> ObjectFiles, List<BuildAction> Actions) BuildModuleActions(
        ActionGraph graph,
        ModuleRules module,
        TargetRules target,
        Dictionary<string, List<FileItem>> moduleOutputs)
    {
        var objectFiles = new List<FileItem>();
        var compileActions = new List<BuildAction>();

        // A HeaderOnly module (O3DE INTERFACE target, e.g. a plugin's `.API` flavor) declares
        // that it compiles nothing - guaranteed here rather than left to the coincidence that
        // its SourceFiles/SourceDirectory only happen to contain headers, no .cpp/.c.
        if (module.BinaryType == TargetType.HeaderOnly)
        {
            return (objectFiles, compileActions);
        }

        // Collect source files (explicit SourceFiles list wins over globbing SourceDirectory)
        var sourceFiles = module.ResolveSourceFiles(_context.ProjectRoot, ".cpp", ".cc", ".cxx", ".c").ToList();

        if (sourceFiles.Count == 0 && module.CodeGenSteps.Count == 0)
        {
            return (objectFiles, compileActions);
        }

        // Include paths and definitions both come from the one shared resolver, so the graph,
        // the generators and moc cannot drift apart. See CompileEnvironmentResolver.
        var compileEnvironment = new CompileEnvironmentResolver(_context, _moduleDict).Resolve(module, target);
        IReadOnlyList<string> includePaths = compileEnvironment.IncludePaths;
        IReadOnlyList<string> externalIncludePaths = compileEnvironment.ExternalIncludePaths;

        // A Qt module's moc steps are synthesized here rather than declared: their inputs are
        // whichever of its headers contain Q_OBJECT, which is discovered by scanning. This is
        // also the first point where the full compile environment exists - moc preprocesses and
        // needs exactly what the compiler gets, and CreateModuleRules has no TargetRules in
        // scope to build it from. Synthesized steps are returned rather than appended to the
        // module, so building the graph once per configuration cannot accumulate duplicates.
        var qtToolchain = new QtToolchainResolver().Resolve(module);
        var qtSteps = qtToolchain is null
            ? []
            : new QtCodeGenSynthesizer().CreateSteps(module, _context, qtToolchain, compileEnvironment);

        // Reflection codegen is synthesized on the same terms and for the same reason: its inputs
        // are whichever headers carry NF_CLASS, and libclang parses them for real, so it needs the
        // compile environment that only exists here.
        List<CodeGenStep> synthesizedSteps =
            [.. qtSteps, .. new ReflectCodeGenSynthesizer().CreateSteps(module, _context, compileEnvironment)];

        // Code generation runs before anything else in the module. Generated .cpp/.c outputs
        // join the compile list; the module's intermediate directory (where relative outputs
        // land) joins its include paths so generated headers resolve.
        var (ownCodeGenActions, generatedSources) = BuildCodeGenActions(graph, module, synthesizedSteps);
        if (module.CodeGenSteps.Count > 0 || synthesizedSteps.Count > 0)
        {
            includePaths = includePaths.Append(Path.Combine(_context.IntermediateDirectory, ModulePathName.Sanitize(module.Name))).ToList();
        }
        _codeGenActionsByModule[module.Name] = ownCodeGenActions;

        // A dependency's generated headers reach this module the way its include paths do -
        // AddAutoGen publishes its output directory - so every compile here orders after that
        // generation too. Modules are visited in dependency order, so those actions exist.
        var codeGenActions = ownCodeGenActions.Concat(DependencyCodeGenActions(module)).ToList();

        var definitions = compileEnvironment.Definitions;

        // Unity builds: combine this module's translation units into a few generated
        // Unity*.cpp/.c files and compile those instead. The module's own setting wins over
        // the target's; the context flag (GUI/CLI override) is a veto over both. Files the
        // generator excludes (UnityBuildExclusions, PCH sources) still compile individually.
        if (_context.UseUnityBuild && (module.UseUnityBuild ?? target.UseUnityBuild) && sourceFiles.Count > 1)
        {
            var generator = new UnityBuildGenerator();
            var unityFiles = generator.GenerateUnityFiles(module, sourceFiles, _context.IntermediateDirectory);
            if (unityFiles.Count > 0)
            {
                generator.WriteUnityFiles(unityFiles);
                var absorbed = unityFiles
                    .SelectMany(u => u.IncludedFiles)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                sourceFiles = unityFiles.Select(u => u.UnityFilePath)
                    .Concat(sourceFiles.Where(s => !absorbed.Contains(s)))
                    .ToList();
            }
        }

        // Generated translation units compile like any other, but join after unity
        // substitution - they don't exist on disk at graph-build time, so combining them
        // into a unity file that #includes missing files would break the first build.
        sourceFiles.AddRange(generatedSources);

        // PCH action if needed
        BuildAction? pchAction = null;
        string? pchFile = null;
        if (_context.UsePCH && target.UsePCHFiles &&
            module.PCHUsage != PCHUsage.None && module.PrecompiledHeaderFile != null)
        {
            (pchAction, pchFile) = CreatePCHAction(graph, module, includePaths, externalIncludePaths, definitions);
            foreach (var codeGenAction in codeGenActions)
            {
                pchAction.Dependencies.Add(codeGenAction);
                codeGenAction.Dependents.Add(pchAction);
            }
        }

        // Create compile actions for each source file
        // Scanning is per-file cached and thread-safe; a module's translation units share
        // most headers, so warming the cache in parallel makes the sequential loop below a
        // lookup. Several hundred TUs at engine scale, a dozen cores - not a small factor.
        if (ScanIncludes)
        {
            Parallel.ForEach(sourceFiles, sourceFile => IncludeScanner.GetTransitiveIncludes(sourceFile, includePaths));
        }

        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(_context.ProjectRoot, sourceFile);
            var objectFile = GetObjectFilePath(module, relativePath);

            var inputFiles = new List<FileItem>
            {
                new() { Path = sourceFile }
            };

            // Headers this TU transitively includes are real inputs: without them, editing a
            // header invalidates nothing (the up-to-date check only compares input timestamps
            // and the command-line digest). For a unity TU this also pulls in the absorbed
            // .cpp files, since the unity file #includes them.
            if (ScanIncludes)
            {
                foreach (var header in IncludeScanner.GetTransitiveIncludes(sourceFile, includePaths))
                {
                    inputFiles.Add(new FileItem { Path = header });
                }
            }

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
                ExternalIncludePaths = externalIncludePaths,
                Definitions = definitions,
                CppStandard = module.CppStandard,
                Optimization = GetOptimizationLevel(module, target),
                WarningLevel = module.WarningLevel,
                TreatWarningsAsErrors = module.TreatWarningsAsErrors,
                EnableRTTI = module.EnableRTTI,
                EnableExceptions = module.EnableExceptions,
                GenerateDebugInfo = target.GenerateDebugInfo,
                PrecompiledHeader = pchFile,
                // Engine-wide flags first so a module's own can override them - both compiler
                // drivers take the last of a conflicting pair.
                AdditionalFlags = _context.GlobalCompilerOptions.Concat(module.AdditionalCompilerFlags).ToList()
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

            // Every compile orders after every code-generation step: generated headers must
            // exist before any TU that might include them, and a generated TU's own step
            // obviously precedes its compile.
            foreach (var codeGenAction in codeGenActions)
            {
                action.Dependencies.Add(codeGenAction);
                codeGenAction.Dependents.Add(action);
            }

            graph.AddAction(action);
            compileActions.Add(action);
            objectFiles.Add(outputFile);
        }

        // An .rc carries the exe's icon and version, which Explorer, the taskbar and Qt's window class all read.
        // link.exe takes the .res beside the objects; lib.exe does not, so a static library has none.
        if (_toolchain.ResourceCompilerPath is { } rc && module.BinaryType != TargetType.StaticLibrary)
        {
            foreach (var script in module.ResolveSourceFiles(_context.ProjectRoot, ".rc"))
            {
                var resFile = Path.ChangeExtension(GetObjectFilePath(module, Path.GetRelativePath(_context.ProjectRoot, script)), ".res");
                var scriptDir = Path.GetDirectoryName(script)!;
                var inputs = new List<FileItem> { new() { Path = script } };
                foreach (Match m in Regex.Matches(File.ReadAllText(script), "\"([^\"]+\\.(?:ico|bmp|cur))\"", RegexOptions.IgnoreCase))
                {
                    inputs.Add(new FileItem { Path = Path.GetFullPath(Path.Combine(scriptDir, m.Groups[1].Value.Replace("\\\\", "\\"))) });
                }

                var resAction = new BuildAction
                {
                    Id = GenerateActionId(),
                    Type = ActionType.Custom,
                    Description = $"Resource {Path.GetFileName(script)}",
                    CommandLine = $"\"{rc}\" /nologo /fo \"{resFile}\" \"{script}\"",
                    WorkingDirectory = scriptDir,
                    Inputs = inputs,
                    Outputs = [new FileItem { Path = resFile }],
                    ModuleName = module.Name,
                    CanExecuteRemotely = false,
                    EstimatedDuration = TimeSpan.FromSeconds(1),
                    Environment = new Dictionary<string, string>(_toolchain.Environment)
                };
                graph.AddAction(resAction);
                compileActions.Add(resAction);
                objectFiles.Add(resAction.Outputs[0]);
            }
        }

        return (objectFiles, compileActions);
    }

    /// <summary>
    /// One graph action per <see cref="ModuleRules.CodeGenSteps"/> entry - the
    /// add_custom_command equivalent. Returns the actions plus the declared outputs that are
    /// themselves translation units (.cpp/.c/.cc), which the caller compiles into the module.
    /// Relative inputs resolve against the module's source directory, relative outputs
    /// against the module's intermediate directory; both (and the command) support
    /// $(ModuleDir)/$(IntermediateDir)/$(ProjectRoot)/$(OutputDir).
    /// </summary>
    private (List<BuildAction> Actions, List<string> GeneratedSources) BuildCodeGenActions(
        ActionGraph graph, ModuleRules module, IReadOnlyList<CodeGenStep> synthesizedSteps)
    {
        var actions = new List<BuildAction>();
        var generatedSources = new List<string>();
        var steps = module.CodeGenSteps.Concat(synthesizedSteps).ToList();
        if (steps.Count == 0)
            return (actions, generatedSources);

        var moduleDir = Path.Combine(_context.ProjectRoot, module.SourceDirectory ?? $"Source/{module.Name}");
        var intermediateDir = Path.Combine(_context.IntermediateDirectory, ModulePathName.Sanitize(module.Name));

        string Expand(string value) => value
            .Replace("$(ModuleDir)", moduleDir)
            .Replace("$(IntermediateDir)", intermediateDir)
            .Replace("$(ProjectRoot)", _context.ProjectRoot)
            .Replace("$(OutputDir)", _context.OutputDirectory);

        foreach (var step in steps)
        {
            if (step.Outputs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Module '{module.Name}': code-gen step '{step.Description}' declares no Outputs - " +
                    "outputs are what the graph tracks and orders on.");
            }

            var inputs = step.Inputs
                .Select(Expand)
                .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(moduleDir, p))
                .ToList();
            var outputs = step.Outputs
                .Select(Expand)
                .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(intermediateDir, p))
                .ToList();

            var action = new BuildAction
            {
                Id = GenerateActionId(),
                Type = ActionType.Custom,
                Description = step.Description,
                CommandLine = Expand(step.Command),
                WorkingDirectory = _context.ProjectRoot,
                Inputs = inputs.Select(p => new FileItem { Path = p }).ToList(),
                Outputs = outputs.Select(p => new FileItem { Path = p }).ToList(),
                ModuleName = module.Name,
                CanExecuteRemotely = false,
                EstimatedDuration = TimeSpan.FromSeconds(2),
                Environment = new Dictionary<string, string>(_toolchain.Environment)
            };
            graph.AddAction(action);
            actions.Add(action);

            if (step.CompileGeneratedSources)
            {
                generatedSources.AddRange(outputs.Where(o =>
                    o.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ||
                    o.EndsWith(".cc", StringComparison.OrdinalIgnoreCase) ||
                    o.EndsWith(".c", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return (actions, generatedSources);
    }

    /// <summary>
    /// The code-generation actions of every module in this one's dependency closure - the same
    /// transitive AllDependencies walk CompileEnvironmentResolver takes for include paths.
    /// </summary>
    private List<BuildAction> DependencyCodeGenActions(ModuleRules module)
    {
        var actions = new List<BuildAction>();
        var visited = new HashSet<string> { module.Name };
        var pending = new Stack<string>(module.AllDependencies);
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!visited.Add(name) || !_moduleDict.TryGetValue(name, out var dependency))
                continue;
            if (_codeGenActionsByModule.TryGetValue(name, out var dependencyActions))
                actions.AddRange(dependencyActions);
            foreach (var next in dependency.AllDependencies)
                pending.Push(next);
        }
        return actions;
    }

    private (BuildAction Action, string PchFile) CreatePCHAction(
        ActionGraph graph,
        ModuleRules module,
        IReadOnlyList<string> includePaths,
        IReadOnlyList<string> externalIncludePaths,
        IReadOnlyList<string> definitions)
    {
        var pchHeader = Path.Combine(_context.ProjectRoot, module.SourceDirectory ?? $"Source/{module.Name}", module.PrecompiledHeaderFile!);
        var pchOutput = Path.Combine(_context.IntermediateDirectory, ModulePathName.Sanitize(module.Name),
            Path.GetFileNameWithoutExtension(module.PrecompiledHeaderFile!) + ".pch");

        var compileRequest = new CompileRequest
        {
            SourceFile = pchHeader,
            OutputFile = pchOutput,
            Configuration = _context.Configuration,
            IncludePaths = includePaths,
            ExternalIncludePaths = externalIncludePaths,
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
            // The PCH header's own transitive includes are inputs too - editing a header the
            // PCH wraps must regenerate the PCH (and, through it, every consumer).
            Inputs = new List<FileItem> { new() { Path = pchHeader } }
                .Concat((ScanIncludes ? IncludeScanner.GetTransitiveIncludes(pchHeader, includePaths) : [])
                    .Select(h => new FileItem { Path = h }))
                .ToList(),
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
        // No objects of the target's own means there is no target binary to produce: in a Modular
        // target every module already linked to its own library, and those ARE the output. Trying
        // anyway hands link.exe a DLL with no entry point ("LNK2001: _DllMainCRTStartup") after the
        // whole target has otherwise built.
        if (aggregateObjectFiles.Count == 0) return;

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
        // Engine-wide linker flags first, then whatever the modules ask for - same
        // last-flag-wins ordering as the compile side.
        var linkerFlags = _context.GlobalLinkerOptions
            .Concat(modules.SelectMany(m => m.AdditionalLinkerFlags))
            .Distinct()
            .ToList();

        string commandLine;
        if (target.Type == TargetType.StaticLibrary)
        {
            commandLine = BuildArchiveCommandLine(new ArchiveRequest
            {
                ObjectFiles = aggregateObjectFiles.Select(o => o.Path).ToList(),
                OutputFile = outputPath
            });
        }
        else
        {
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
            commandLine = BuildLinkCommandLine(linkRequest);
        }

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

    private OptimizationLevel GetOptimizationLevel(ModuleRules module, TargetRules target)
    {
        if (module.OptimizeCode.HasValue)
            return module.OptimizeCode.Value;

        return _context.Configuration switch
        {
            BuildConfiguration.Debug => OptimizationLevel.Disabled,
            // Development is the optimized-with-debug-info configuration - O3DE calls the same
            // thing "profile" and compiles it /O2. Mapping it to OptimizationLevel.Debug emitted
            // /Od, so every Development build was unoptimized, and once a project supplied its
            // own engine-wide /O2 the two collided visibly:
            // "cl : Command line warning D9025 : overriding '/Od' with '/O2'".
            BuildConfiguration.Development => OptimizationLevel.Development,
            BuildConfiguration.Release => OptimizationLevel.Shipping,
            BuildConfiguration.Shipping => OptimizationLevel.Shipping,
            _ => OptimizationLevel.Debug
        };
    }

    private string GetObjectFilePath(ModuleRules module, string sourceRelativePath)
    {
        // A generated source already lives under the intermediate directory, so a project-root
        // relative path would nest the whole intermediate path inside itself.
        var intermediateRelative = Path.GetRelativePath(_context.IntermediateDirectory,
            Path.Combine(_context.ProjectRoot, sourceRelativePath));
        if (!intermediateRelative.StartsWith("..") && !Path.IsPathRooted(intermediateRelative))
        {
            sourceRelativePath = intermediateRelative;
        }

        // Mirror the source tree instead of flattening to a basename. NightFoxCore has both
        // NightFoxCore/Utils/Utils.cpp and Platform/Android/NightFoxCore/Android/Utils.cpp; flattened
        // they are one Utils.obj, the second compile overwrites the first, and the link fails with
        // unresolved symbols that point at no missing source.
        var objectName = Path.ChangeExtension(sourceRelativePath, _toolchain.ObjectFileExtension);
        return Path.Combine(_context.IntermediateDirectory, ModulePathName.Sanitize(module.Name), objectName);
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

            // C++ standard. Skipped for .c sources: cl rejects a C++ /std: value together with a C
            // one (D8016 "command-line options are incompatible"), and a C module that wants a
            // specific C standard says so itself via AdditionalCompilerFlags (e.g. /std:c17).
            if (!request.SourceFile.EndsWith(".c", StringComparison.OrdinalIgnoreCase))
            {
                args.Add(request.CppStandard switch
                {
                    CppStandard.Cpp14 => "/std:c++14",
                    CppStandard.Cpp17 => "/std:c++17",
                    CppStandard.Cpp20 => "/std:c++20",
                    CppStandard.Cpp23 => "/std:c++latest",
                    CppStandard.Latest => "/std:c++latest",
                    _ => "/std:c++20"
                });
            }

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
                // A shared dependency module (e.g. DolphinScene, depended on by several
                // independent targets) has the same Intermediate/ output path regardless of
                // which target's own `omen build` invocation is compiling it - so two targets
                // built concurrently (Visual Studio's own default for independent projects in
                // a solution) can have two separate cl.exe processes writing the same .pdb at
                // once. /FS routes PDB writes through mspdbsrv.exe so concurrent cl.exe
                // processes serialize safely instead of racing (cl.exe's own C1041 error names
                // this exact flag as the fix).
                args.Add("/FS");
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

            // Third-party roots as external: their warnings are not ours. /external:W0 must
            // precede the /external:I flags it governs.
            if (request.ExternalIncludePaths.Count > 0)
            {
                args.Add("/external:W0");
                foreach (var include in request.ExternalIncludePaths)
                    args.Add($"/external:I\"{include}\"");
            }

            // Definitions. The arguments become one command line that cl parses itself, so a quote
            // inside a definition (LY_CMAKE_TARGET="Name", which every O3DE executable target has)
            // must arrive as \" or cl eats it and the macro expands to a bare identifier. Rules spell
            // it both ways - the converter pre-escapes (AZ_BUILD_CONFIGURATION_TYPE=\"profile\"),
            // hand-written modules do not - so only a quote that is not already escaped is escaped.
            foreach (var def in request.Definitions)
            {
                var escaped = System.Text.RegularExpressions.Regex.Replace(def, "(?<!\\\\)\"", "\\\"");
                // A value with a space (a path under Program Files) must stay one argument.
                args.Add(escaped.Any(char.IsWhiteSpace) ? $"/D\"{escaped}\"" : "/D" + escaped);
            }

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

        // Compiles spill to a response file on the same rule as links and archives. A module with
        // a wide dependency closure carries a long enough include list on its own:
        // AtomToolsFramework.Static pulls in 48 modules, and 73 of its compile actions exceeded
        // the shell's limit with "The command line is too long" - before any of the code was even
        // parsed. cl.exe takes @file exactly as link.exe does.
        return WithResponseFile(_toolchain.CompilerPath, args, request.OutputFile);
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

            // The Win32 libraries CMake supplies implicitly through CMAKE_CXX_STANDARD_LIBRARIES.
            // Invoking link.exe directly gets only what the objects' own /DEFAULTLIB directives
            // name, so without these a module using RegOpenKeyExW, CryptGenRandom, MessageBoxW or
            // SHGetFolderPathW fails with unresolved __imp_ symbols that look like missing source
            // rather than a missing library.
            foreach (var lib in WindowsStandardLibraries)
            {
                if (!request.SystemLibraries.Contains(lib, StringComparer.OrdinalIgnoreCase))
                {
                    args.Add($"{lib}.lib");
                }
            }

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

        return WithResponseFile(_toolchain.LinkerPath, args, request.OutputFile);
    }

    /// <summary>
    /// The MSVC value of CMAKE_CXX_STANDARD_LIBRARIES, which CMake appends to every link and Omen
    /// otherwise would not. Kept in CMake's order so a link line diffed against a CMake one matches.
    /// </summary>
    private static readonly string[] WindowsStandardLibraries =
    [
        "kernel32", "user32", "gdi32", "winspool", "shell32",
        "ole32", "oleaut32", "uuid", "comdlg32", "advapi32",
    ];

    /// <summary>
    /// Maximum command line we will pass directly.
    ///
    /// The binding limit is cmd.exe's 8,191 characters, not CreateProcess's 32,767: actions are
    /// launched through the platform shell (HybridExecutor). A 22,911-character link sat happily
    /// under a 30,000 threshold and still died with "The command line is too long", because the
    /// shell rejected it before the linker existed. 7,500 leaves room for the "cmd.exe /c "
    /// prefix and the quoting the shell adds around the whole command.
    /// </summary>
    private const int MaxDirectCommandLineLength = 7500;

    /// <summary>
    /// A tool invocation, spilled to a response file when it would be too long to pass directly.
    ///
    /// Linking NightFoxCore is 290 object files at ~90 characters of absolute path each, which
    /// clears Windows' limit on its own and fails with "The command line is too long." before the
    /// linker sees any of it. cl.exe, link.exe and lib.exe all accept @file.
    ///
    /// The file name carries a hash of its own contents, and that is load-bearing rather than
    /// tidiness: BuildAction.ComputeDigest hashes the command line, so a fixed response-file name
    /// would make every invocation of an action look identical no matter what moved inside the
    /// file. Changing an include path or a define would leave the digest unchanged and the action
    /// would be skipped as up to date, silently linking or compiling stale output. With the hash
    /// in the name, the command line moves whenever the arguments do.
    /// </summary>
    private static string WithResponseFile(string toolPath, IReadOnlyList<string> args, string outputFile)
    {
        var direct = $"\"{toolPath}\" {string.Join(" ", args)}";
        if (direct.Length <= MaxDirectCommandLineLength)
        {
            return direct;
        }

        var payload = string.Join("\n", args);
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)))[..8];

        var responseFile = Path.ChangeExtension(outputFile, $".{hash}.rsp");
        var directory = Path.GetDirectoryName(responseFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // One argument per line: the tools split on newlines as well as spaces, and a path
        // containing a space stays inside its own quotes rather than becoming two arguments.
        File.WriteAllLines(responseFile, args);
        return $"\"{toolPath}\" @\"{responseFile}\"";
    }

    /// <summary>
    /// Builds the archiver command line for a static library. This is a distinct tool from
    /// the linker (lib.exe/ar, not link.exe/ld) - an archive just bundles object files, it
    /// doesn't resolve symbols or take linker flags, so it must never share BuildLinkCommandLine.
    /// </summary>
    private string BuildArchiveCommandLine(ArchiveRequest request)
    {
        var args = new List<string>();

        if (_context.Platform == TargetPlatform.Windows)
        {
            args.Add("/nologo");
            args.Add($"/OUT:\"{request.OutputFile}\"");

            foreach (var obj in request.ObjectFiles)
                args.Add($"\"{obj}\"");
        }
        else
        {
            args.Add("rcs");
            args.Add($"\"{request.OutputFile}\"");

            foreach (var obj in request.ObjectFiles)
                args.Add($"\"{obj}\"");
        }

        return WithResponseFile(_toolchain.ArchiverPath, args, request.OutputFile);
    }

    private string GenerateActionId() => $"action_{Interlocked.Increment(ref _actionCounter):D6}";

    private IReadOnlyList<ModuleRules> TopologicalSortModules(IReadOnlyList<ModuleRules> modules)
    {
        var moduleDict = modules.ToDictionary(m => m.Name);
        var result = new List<ModuleRules>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        // The path currently being walked, so a cycle can name every module in it rather than
        // just the one the walk happened to re-enter. "Circular dependency detected: X" alone
        // says nothing about which edge to cut.
        var path = new List<string>();

        void Visit(ModuleRules module)
        {
            if (visited.Contains(module.Name))
                return;

            if (visiting.Contains(module.Name))
            {
                var start = path.IndexOf(module.Name);
                var cycle = start >= 0 ? path.Skip(start).Append(module.Name) : [module.Name];

                throw new InvalidOperationException(
                    $"Circular dependency detected: {string.Join(" -> ", cycle)}");
            }

            visiting.Add(module.Name);
            path.Add(module.Name);

            foreach (var depName in module.AllDependencies)
            {
                if (moduleDict.TryGetValue(depName, out var dep))
                {
                    Visit(dep);
                }
            }

            path.RemoveAt(path.Count - 1);
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
