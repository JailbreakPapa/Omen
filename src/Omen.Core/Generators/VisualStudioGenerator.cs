// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text;
using System.Security.Cryptography;
using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.Core.Generators;

/// <summary>
/// Generates Visual Studio solution and project files.
/// Supports C++ (.vcxproj), C# (.csproj), and Qt projects.
/// </summary>
public sealed class VisualStudioGenerator
{
    private readonly string _projectRoot;
    private readonly VisualStudioVersion _version;
    private Dictionary<string, ModuleRules> _moduleDict = new();

    public enum VisualStudioVersion
    {
        VS2019,
        VS2022,
        VS2026
    }

    public VisualStudioGenerator(string projectRoot, VisualStudioVersion version = VisualStudioVersion.VS2022)
    {
        _projectRoot = projectRoot;
        _version = version;
    }

    /// <summary>
    /// Generates a Visual Studio solution from compiled rules.
    /// </summary>
    public async Task GenerateAsync(
        TargetRules target,
        IReadOnlyList<ModuleRules> modules,
        CancellationToken ct = default)
    {
        var solutionName = target.Name;
        var solutionPath = Path.Combine(_projectRoot, $"{solutionName}.sln");
        var projectsDir = Path.Combine(_projectRoot, "Intermediate", "ProjectFiles");

        Directory.CreateDirectory(projectsDir);

        // Build module dictionary for dependency resolution
        _moduleDict = modules.ToDictionary(m => m.Name);

        // Generate project files for each module
        var projects = new List<(string Name, string Path, Guid Guid, bool IsCSharp)>();

        foreach (var module in modules)
        {
            var projectGuid = GenerateGuid(module.Name);
            
            if (module.IsCSharpProject)
            {
                var projectPath = Path.Combine(projectsDir, module.Name, $"{module.Name}.csproj");
                await GenerateCsprojAsync(module, projectPath, target, modules, ct);
                projects.Add((module.Name, projectPath, projectGuid, true));
            }
            else
            {
                var projectPath = Path.Combine(projectsDir, module.Name, $"{module.Name}.vcxproj");
                await GenerateVcxprojAsync(module, projectPath, target, modules, ct);
                await GenerateVcxprojFiltersAsync(module, projectPath + ".filters", ct);
                await GenerateVcxprojUserAsync(module, projectPath + ".user", ct);
                projects.Add((module.Name, projectPath, projectGuid, false));
            }
        }

        // Generate solution file
        await GenerateSolutionAsync(solutionPath, solutionName, target, projects, modules, ct);
    }

    private async Task GenerateSolutionAsync(
        string solutionPath,
        string solutionName,
        TargetRules target,
        List<(string Name, string Path, Guid Guid, bool IsCSharp)> projects,
        IReadOnlyList<ModuleRules> modules,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        // Solution header
        var (formatVersion, vsVersion, minVsVersion) = _version switch
        {
            VisualStudioVersion.VS2019 => ("12.00", "16", "10.0.40219.1"),
            VisualStudioVersion.VS2022 => ("12.00", "17", "10.0.40219.1"),
            VisualStudioVersion.VS2026 => ("12.00", "18", "10.0.40219.1"),
            _ => ("12.00", "17", "10.0.40219.1")
        };

        // Project type GUIDs
        const string CppProjectTypeGuid = "8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942";
        const string CSharpProjectTypeGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
        const string SolutionFolderGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

        sb.AppendLine();
        sb.AppendLine($"Microsoft Visual Studio Solution File, Format Version {formatVersion}");
        sb.AppendLine($"# Visual Studio Version {vsVersion}");
        sb.AppendLine($"VisualStudioVersion = {vsVersion}.0.00000.0");
        sb.AppendLine($"MinimumVisualStudioVersion = {minVsVersion}");

        // Solution folders
        var srcFolderGuid = GenerateGuid("Source");
        var cppFolderGuid = GenerateGuid("C++");
        var csharpFolderGuid = GenerateGuid("C#");

        sb.AppendLine($"Project(\"{{{SolutionFolderGuid}}}\") = \"Source\", \"Source\", \"{{{srcFolderGuid}}}\"");
        sb.AppendLine("EndProject");

        // Create sub-folders if we have mixed projects
        var hasCpp = projects.Any(p => !p.IsCSharp);
        var hasCSharp = projects.Any(p => p.IsCSharp);

        if (hasCpp && hasCSharp)
        {
            sb.AppendLine($"Project(\"{{{SolutionFolderGuid}}}\") = \"C++\", \"C++\", \"{{{cppFolderGuid}}}\"");
            sb.AppendLine("EndProject");
            sb.AppendLine($"Project(\"{{{SolutionFolderGuid}}}\") = \"C#\", \"C#\", \"{{{csharpFolderGuid}}}\"");
            sb.AppendLine("EndProject");
        }

        // Projects with dependencies
        var projectGuids = projects.ToDictionary(p => p.Name, p => p.Guid);

        foreach (var (name, path, guid, isCSharp) in projects)
        {
            var relativePath = Path.GetRelativePath(_projectRoot, path);
            var projectTypeGuid = isCSharp ? CSharpProjectTypeGuid : CppProjectTypeGuid;
            
            sb.AppendLine($"Project(\"{{{projectTypeGuid}}}\") = \"{name}\", \"{relativePath}\", \"{{{guid}}}\"");

            // Add project dependencies
            var module = _moduleDict[name];
            var deps = module.PublicDependencies.Concat(module.PrivateDependencies)
                .Where(d => projectGuids.ContainsKey(d))
                .ToList();

            if (deps.Count > 0)
            {
                sb.AppendLine("\tProjectSection(ProjectDependencies) = postProject");
                foreach (var dep in deps)
                {
                    var depGuid = projectGuids[dep];
                    sb.AppendLine($"\t\t{{{depGuid}}} = {{{depGuid}}}");
                }
                sb.AppendLine("\tEndProjectSection");
            }

            sb.AppendLine("EndProject");
        }

        // Global section
        sb.AppendLine("Global");

        // Solution configuration platforms
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        foreach (var config in new[] { "Debug", "Development", "Shipping" })
        {
            sb.AppendLine($"\t\t{config}|x64 = {config}|x64");
            sb.AppendLine($"\t\t{config}|ARM64 = {config}|ARM64");
        }
        sb.AppendLine("\tEndGlobalSection");

        // Project configuration platforms
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var (name, _, guid, isCSharp) in projects)
        {
            // C++ module (dependency, non-main) projects carry no build command of their
            // own -- the NMake-converted main project already builds everything via
            // `omen build`. Omitting their Build.0 entry keeps "Build Solution" from
            // having MSBuild compile them a second time for real; ActiveCfg is still
            // written so they remain mapped/browsable. C# projects are outside this task's
            // NMake conversion and keep building normally.
            var isBuildable = isCSharp || IsMainModule(_moduleDict[name], target, modules);

            foreach (var config in new[] { "Debug", "Development", "Shipping" })
            {
                foreach (var platform in new[] { "x64", "ARM64" })
                {
                    // C# projects use AnyCPU, need to map platforms
                    var projectConfig = isCSharp ? MapToCSharpConfig(config) : config;
                    var projectPlatform = isCSharp ? "Any CPU" : platform;

                    sb.AppendLine($"\t\t{{{guid}}}.{config}|{platform}.ActiveCfg = {projectConfig}|{projectPlatform}");
                    if (isBuildable)
                        sb.AppendLine($"\t\t{{{guid}}}.{config}|{platform}.Build.0 = {projectConfig}|{projectPlatform}");
                }
            }
        }
        sb.AppendLine("\tEndGlobalSection");

        // Solution properties
        sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        sb.AppendLine("\t\tHideSolutionNode = FALSE");
        sb.AppendLine("\tEndGlobalSection");

        // Nested projects
        sb.AppendLine("\tGlobalSection(NestedProjects) = preSolution");
        foreach (var (_, _, guid, isCSharp) in projects)
        {
            Guid parentGuid;
            if (hasCpp && hasCSharp)
            {
                parentGuid = isCSharp ? csharpFolderGuid : cppFolderGuid;
            }
            else
            {
                parentGuid = srcFolderGuid;
            }
            sb.AppendLine($"\t\t{{{guid}}} = {{{parentGuid}}}");
        }

        // Nest C++ and C# folders under Source if both exist
        if (hasCpp && hasCSharp)
        {
            sb.AppendLine($"\t\t{{{cppFolderGuid}}} = {{{srcFolderGuid}}}");
            sb.AppendLine($"\t\t{{{csharpFolderGuid}}} = {{{srcFolderGuid}}}");
        }
        sb.AppendLine("\tEndGlobalSection");

        // Extensibility globals
        sb.AppendLine("\tGlobalSection(ExtensibilityGlobals) = postSolution");
        sb.AppendLine($"\t\tSolutionGuid = {{{GenerateGuid(solutionName)}}}");
        sb.AppendLine("\tEndGlobalSection");

        sb.AppendLine("EndGlobal");

        await File.WriteAllTextAsync(solutionPath, sb.ToString(), ct);
    }

    private static string MapToCSharpConfig(string config) => config switch
    {
        "Debug" => "Debug",
        "Development" => "Release",
        "Shipping" => "Release",
        _ => "Debug"
    };

    #region C# Project Generation

    private async Task GenerateCsprojAsync(
        ModuleRules module,
        string projectPath,
        TargetRules target,
        IReadOnlyList<ModuleRules> allModules,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);

        var sourceDir = Path.Combine(_projectRoot, module.SourceDirectory ?? $"Source/{module.Name}");

        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine();

        // Property Group
        sb.AppendLine("  <PropertyGroup>");
        
        // Output type
        var outputType = module.Type switch
        {
            ModuleType.Runtime => "Exe",
            ModuleType.Editor => "Exe",
            ModuleType.ThirdParty => "Library",
            _ => "Library"
        };
        sb.AppendLine($"    <OutputType>{outputType}</OutputType>");

        // Target framework
        var targetFramework = module.TargetFramework switch
        {
            DotNetFramework.Net60 => "net6.0",
            DotNetFramework.Net70 => "net7.0",
            DotNetFramework.Net80 => "net8.0",
            DotNetFramework.Net90 => "net9.0",
            DotNetFramework.NetStandard20 => "netstandard2.0",
            DotNetFramework.NetStandard21 => "netstandard2.1",
            _ => "net8.0"
        };
        sb.AppendLine($"    <TargetFramework>{targetFramework}</TargetFramework>");

        // C# version
        var langVersion = module.CSharpVersion switch
        {
            CSharpVersion.CSharp10 => "10.0",
            CSharpVersion.CSharp11 => "11.0",
            CSharpVersion.CSharp12 => "12.0",
            CSharpVersion.CSharp13 => "13.0",
            CSharpVersion.Latest => "latest",
            _ => "latest"
        };
        sb.AppendLine($"    <LangVersion>{langVersion}</LangVersion>");

        // Nullable
        sb.AppendLine($"    <Nullable>{(module.EnableNullable ? "enable" : "disable")}</Nullable>");
        sb.AppendLine($"    <ImplicitUsings>{(module.ImplicitUsings ? "enable" : "disable")}</ImplicitUsings>");

        // Treat warnings as errors
        if (module.TreatWarningsAsErrors)
        {
            sb.AppendLine("    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>");
        }

        // Root namespace
        sb.AppendLine($"    <RootNamespace>{module.Name}</RootNamespace>");
        sb.AppendLine($"    <AssemblyName>{module.Name}</AssemblyName>");

        // Output paths
        sb.AppendLine("    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
        sb.AppendLine("    <OutputPath>$(SolutionDir)Binaries\\$(Configuration)\\</OutputPath>");
        
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();

        // Debug configuration
        sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)'=='Debug'\">");
        sb.AppendLine("    <DefineConstants>DEBUG;TRACE;OMEN_CONFIG_DEBUG</DefineConstants>");
        sb.AppendLine("    <Optimize>false</Optimize>");
        sb.AppendLine("    <DebugType>full</DebugType>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();

        // Release configuration
        sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)'=='Release'\">");
        sb.AppendLine("    <DefineConstants>TRACE;OMEN_CONFIG_SHIPPING</DefineConstants>");
        sb.AppendLine("    <Optimize>true</Optimize>");
        sb.AppendLine("    <DebugType>pdbonly</DebugType>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();

        // Source files - use wildcard includes
        sb.AppendLine("  <ItemGroup>");
        var relativeSourceDir = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, sourceDir);
        sb.AppendLine($"    <Compile Include=\"{relativeSourceDir}\\**\\*.cs\" />");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine();

        // Package references
        if (module.PackageReferences.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var package in module.PackageReferences)
            {
                var parts = package.Split('/');
                var packageName = parts[0];
                var version = parts.Length > 1 ? parts[1] : "*";
                sb.AppendLine($"    <PackageReference Include=\"{packageName}\" Version=\"{version}\" />");
            }
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
        }

        // Project references
        var deps = module.PublicDependencies.Concat(module.PrivateDependencies)
            .Where(d => _moduleDict.ContainsKey(d) && _moduleDict[d].IsCSharpProject)
            .ToList();

        if (deps.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var dep in deps)
            {
                var depPath = Path.Combine(_projectRoot, "Intermediate", "ProjectFiles", dep, $"{dep}.csproj");
                var relDepPath = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, depPath);
                sb.AppendLine($"    <ProjectReference Include=\"{relDepPath}\" />");
            }
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
        }

        // Assembly references
        if (module.AssemblyReferences.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var assembly in module.AssemblyReferences)
            {
                sb.AppendLine($"    <Reference Include=\"{assembly}\" />");
            }
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
        }

        sb.AppendLine("</Project>");

        await File.WriteAllTextAsync(projectPath, sb.ToString(), ct);
    }

    #endregion

    #region C++ Project Generation

    private async Task GenerateVcxprojAsync(
        ModuleRules module,
        string projectPath,
        TargetRules target,
        IReadOnlyList<ModuleRules> allModules,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);

        var projectGuid = GenerateGuid(module.Name);
        var sourceDir = Path.Combine(_projectRoot, module.SourceDirectory ?? $"Source/{module.Name}");

        // Collect source files
        var sourceFiles = new List<string>();
        var headerFiles = new List<string>();
        var uiFiles = new List<string>();
        var resourceFiles = new List<string>();

        if (Directory.Exists(sourceDir))
        {
            sourceFiles.AddRange(Directory.GetFiles(sourceDir, "*.cpp", SearchOption.AllDirectories));
            sourceFiles.AddRange(Directory.GetFiles(sourceDir, "*.c", SearchOption.AllDirectories));
            headerFiles.AddRange(Directory.GetFiles(sourceDir, "*.h", SearchOption.AllDirectories));
            headerFiles.AddRange(Directory.GetFiles(sourceDir, "*.hpp", SearchOption.AllDirectories));

            // Qt-specific files
            if (module.IsQtProject)
            {
                uiFiles.AddRange(Directory.GetFiles(sourceDir, "*.ui", SearchOption.AllDirectories));
                resourceFiles.AddRange(Directory.GetFiles(sourceDir, "*.qrc", SearchOption.AllDirectories));
            }
        }

        var toolsVersion = _version switch
        {
            VisualStudioVersion.VS2019 => "16.0",
            VisualStudioVersion.VS2022 => "17.0",
            VisualStudioVersion.VS2026 => "18.0",
            _ => "17.0"
        };

        var platformToolset = _version switch
        {
            VisualStudioVersion.VS2019 => "v142",
            VisualStudioVersion.VS2022 => "v143",
            VisualStudioVersion.VS2026 => "v144",
            _ => "v143"
        };

        // Determine if this module is the "main" module that produces the executable.
        // Prefer the target's explicit LaunchModuleName when set: pure dependency-topology
        // (below) can't tell which target a root module belongs to once a project has more
        // than one TargetRules, so every target's root module would otherwise satisfy the
        // topology check and all collide on being treated as "the" main module. Falling
        // back to topology when LaunchModuleName is unset keeps today's single-target
        // projects (e.g. ExampleGame, which doesn't set it) behaving exactly as before.
        var isMainModule = IsMainModule(module, target, allModules);

        // Module type: main module uses target type, others are static libraries
        var configType = isMainModule ?
            (target.Type switch
            {
                TargetType.Executable => "Application",
                TargetType.SharedLibrary => "DynamicLibrary",
                TargetType.StaticLibrary => "StaticLibrary",
                _ => "Application"
            }) : "StaticLibrary";

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine($"<Project DefaultTargets=\"Build\" ToolsVersion=\"{toolsVersion}\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");

        // Item group for configurations
        sb.AppendLine("  <ItemGroup Label=\"ProjectConfigurations\">");
        foreach (var config in new[] { "Debug", "Development", "Shipping" })
        {
            foreach (var platform in new[] { "x64", "ARM64" })
            {
                sb.AppendLine($"    <ProjectConfiguration Include=\"{config}|{platform}\">");
                sb.AppendLine($"      <Configuration>{config}</Configuration>");
                sb.AppendLine($"      <Platform>{platform}</Platform>");
                sb.AppendLine("    </ProjectConfiguration>");
            }
        }
        sb.AppendLine("  </ItemGroup>");

        // Globals
        sb.AppendLine("  <PropertyGroup Label=\"Globals\">");
        sb.AppendLine($"    <VCProjectVersion>{toolsVersion}</VCProjectVersion>");
        sb.AppendLine($"    <ProjectGuid>{{{projectGuid}}}</ProjectGuid>");
        sb.AppendLine($"    <RootNamespace>{module.Name}</RootNamespace>");
        sb.AppendLine($"    <WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion>");
        
        // Qt-specific globals
        if (module.IsQtProject)
        {
            sb.AppendLine($"    <Keyword>QtVS_v304</Keyword>");
            sb.AppendLine($"    <QtMsBuild Condition=\"'$(QtMsBuild)'==''\">"
                + "$(MSBuildProjectDirectory)\\QtMsBuild</QtMsBuild>");
        }
        
        sb.AppendLine("  </PropertyGroup>");

        sb.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.Default.props\" />");

        // Configuration property groups
        foreach (var config in new[] { "Debug", "Development", "Shipping" })
        {
            foreach (var platform in new[] { "x64", "ARM64" })
            {
                var useDebugLibs = config == "Debug" ? "true" : "false";

                sb.AppendLine($"  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='{config}|{platform}'\" Label=\"Configuration\">");
                sb.AppendLine($"    <ConfigurationType>{(isMainModule ? "Makefile" : configType)}</ConfigurationType>");
                sb.AppendLine($"    <UseDebugLibraries>{useDebugLibs}</UseDebugLibraries>");
                sb.AppendLine($"    <PlatformToolset>{platformToolset}</PlatformToolset>");
                sb.AppendLine("    <CharacterSet>Unicode</CharacterSet>");
                if (config != "Debug")
                {
                    sb.AppendLine("    <WholeProgramOptimization>true</WholeProgramOptimization>");
                }
                sb.AppendLine("  </PropertyGroup>");
            }
        }

        sb.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.props\" />");

        // Qt properties import
        if (module.IsQtProject)
        {
            sb.AppendLine("  <ImportGroup Condition=\"Exists('$(QtMsBuild)\\qt_defaults.props')\">");
            sb.AppendLine("    <Import Project=\"$(QtMsBuild)\\qt_defaults.props\" />");
            sb.AppendLine("  </ImportGroup>");
        }

        sb.AppendLine("  <ImportGroup Label=\"ExtensionSettings\">");
        sb.AppendLine("  </ImportGroup>");

        // Property sheets
        foreach (var config in new[] { "Debug", "Development", "Shipping" })
        {
            foreach (var platform in new[] { "x64", "ARM64" })
            {
                sb.AppendLine($"  <ImportGroup Label=\"PropertySheets\" Condition=\"'$(Configuration)|$(Platform)'=='{config}|{platform}'\">");
                sb.AppendLine("    <Import Project=\"$(UserRootDir)\\Microsoft.Cpp.$(Platform).user.props\" Condition=\"exists('$(UserRootDir)\\Microsoft.Cpp.$(Platform).user.props')\" Label=\"LocalAppDataPlatform\" />");
                sb.AppendLine("  </ImportGroup>");
            }
        }

        sb.AppendLine("  <PropertyGroup Label=\"UserMacros\" />");

        // Qt settings
        if (module.IsQtProject)
        {
            foreach (var config in new[] { "Debug", "Development", "Shipping" })
            {
                foreach (var platform in new[] { "x64", "ARM64" })
                {
                    sb.AppendLine($"  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='{config}|{platform}'\">");
                    sb.AppendLine($"    <QtInstall>{GetQtInstallName(module)}</QtInstall>");
                    sb.AppendLine($"    <QtModules>{string.Join(";", module.QtModules.Select(m => $"qt{m.ToLowerInvariant()}"))}</QtModules>");
                    if (module.EnableMoc) sb.AppendLine("    <QtMocEnabled>true</QtMocEnabled>");
                    if (module.EnableUic) sb.AppendLine("    <QtUicEnabled>true</QtUicEnabled>");
                    if (module.EnableRcc) sb.AppendLine("    <QtRccEnabled>true</QtRccEnabled>");
                    sb.AppendLine("  </PropertyGroup>");
                }
            }
        }

        // Output directories
        foreach (var config in new[] { "Debug", "Development", "Shipping" })
        {
            foreach (var platform in new[] { "x64", "ARM64" })
            {
                sb.AppendLine($"  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='{config}|{platform}'\">");
                sb.AppendLine($"    <OutDir>$(SolutionDir)Binaries\\Windows_{config}\\</OutDir>");
                sb.AppendLine($"    <IntDir>$(SolutionDir)Intermediate\\$(ProjectName)\\$(Configuration)\\</IntDir>");
                sb.AppendLine($"    <TargetName>{module.Name}</TargetName>");
                sb.AppendLine("  </PropertyGroup>");
            }
        }

        // Build all include paths including dependencies
        var allIncludePaths = BuildIncludePaths(module);

        // Add Qt include paths
        if (module.IsQtProject)
        {
            allIncludePaths.InsertRange(0, GetQtIncludePaths(module));
        }

        // Build all definitions including dependencies
        var allDefinitions = BuildDefinitions(module);

        // Output extension for the target's final artifact (used by the main module's
        // NMakeOutput below; VS project generation targets Windows only, same as the
        // rest of this generator).
        var targetOutputExtension = target.Type switch
        {
            TargetType.Executable => ".exe",
            TargetType.SharedLibrary => ".dll",
            TargetType.StaticLibrary => ".lib",
            _ => ".exe"
        };

        // Compiler/Linker settings.
        // The main module (the one that actually produces the target's output) is
        // NMake-style: Visual Studio shells into `omen` for Build/Rebuild/Clean instead
        // of compiling via ClCompile/Link items, so no ItemDefinitionGroup is emitted for
        // it. It still gets NMakePreprocessorDefinitions/NMakeIncludeSearchPath so
        // IntelliSense keeps working. Dependency modules are unaffected and keep compiling
        // for real via MSBuild, as before.
        foreach (var config in new[] { "Debug", "Development", "Shipping" })
        {
            foreach (var platform in new[] { "x64", "ARM64" })
            {
                var optimization = config == "Shipping" ? "MaxSpeed" : (config == "Development" ? "MinSpace" : "Disabled");
                var runtimeLib = config == "Debug" ? "MultiThreadedDebugDLL" : "MultiThreadedDLL";
                var debugInfo = config == "Debug" || config == "Development" ? "true" : "false";

                // Add config-specific definitions
                var definitions = new List<string>(allDefinitions);
                definitions.Add($"OMEN_CONFIG_{config.ToUpperInvariant()}=1");

                // Qt definitions
                if (module.IsQtProject)
                {
                    definitions.Add("QT_CORE_LIB");
                    if (module.QtModules.Any(m => m.Equals("Widgets", StringComparison.OrdinalIgnoreCase)))
                        definitions.Add("QT_WIDGETS_LIB");
                    if (module.QtModules.Any(m => m.Equals("Gui", StringComparison.OrdinalIgnoreCase)))
                        definitions.Add("QT_GUI_LIB");
                }

                var includePaths = new List<string>(allIncludePaths);

                if (isMainModule)
                {
                    var nmakeOutput = $"$(SolutionDir)Binaries\\Windows_{config}\\{module.Name}{targetOutputExtension}";

                    sb.AppendLine($"  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='{config}|{platform}'\">");
                    sb.AppendLine($"    <NMakeBuildCommandLine>omen build {target.Name} -Configuration={config} -Platform={platform}</NMakeBuildCommandLine>");
                    sb.AppendLine($"    <NMakeReBuildCommandLine>omen rebuild {target.Name} -Configuration={config} -Platform={platform}</NMakeReBuildCommandLine>");
                    sb.AppendLine($"    <NMakeCleanCommandLine>omen clean {target.Name} -Configuration={config} -Platform={platform}</NMakeCleanCommandLine>");
                    sb.AppendLine($"    <NMakeOutput>{nmakeOutput}</NMakeOutput>");
                    sb.AppendLine($"    <NMakePreprocessorDefinitions>{string.Join(";", definitions)}</NMakePreprocessorDefinitions>");
                    sb.AppendLine($"    <NMakeIncludeSearchPath>{string.Join(";", includePaths)}</NMakeIncludeSearchPath>");
                    sb.AppendLine("  </PropertyGroup>");

                    continue;
                }

                definitions.Add("%(PreprocessorDefinitions)");
                includePaths.Add("%(AdditionalIncludeDirectories)");

                sb.AppendLine($"  <ItemDefinitionGroup Condition=\"'$(Configuration)|$(Platform)'=='{config}|{platform}'\">");
                sb.AppendLine("    <ClCompile>");
                sb.AppendLine($"      <Optimization>{optimization}</Optimization>");
                sb.AppendLine($"      <AdditionalIncludeDirectories>{string.Join(";", includePaths)}</AdditionalIncludeDirectories>");
                sb.AppendLine($"      <PreprocessorDefinitions>{string.Join(";", definitions)}</PreprocessorDefinitions>");
                sb.AppendLine("      <ConformanceMode>true</ConformanceMode>");

                var cppStd = module.CppStandard switch
                {
                    CppStandard.Cpp14 => "stdcpp14",
                    CppStandard.Cpp17 => "stdcpp17",
                    CppStandard.Cpp20 => "stdcpp20",
                    CppStandard.Cpp23 => "stdcpplatest",
                    CppStandard.Latest => "stdcpplatest",
                    _ => "stdcpp20"
                };
                sb.AppendLine($"      <LanguageStandard>{cppStd}</LanguageStandard>");

                sb.AppendLine($"      <RuntimeLibrary>{runtimeLib}</RuntimeLibrary>");

                var warningLevel = module.WarningLevel switch
                {
                    WarningLevel.Off => "TurnOffAllWarnings",
                    WarningLevel.Level1 => "Level1",
                    WarningLevel.Level2 => "Level2",
                    WarningLevel.Level3 => "Level3",
                    WarningLevel.Level4 => "Level4",
                    WarningLevel.EnableAll => "EnableAllWarnings",
                    _ => "Level4"
                };
                sb.AppendLine($"      <WarningLevel>{warningLevel}</WarningLevel>");

                if (module.TreatWarningsAsErrors)
                {
                    sb.AppendLine("      <TreatWarningAsError>true</TreatWarningAsError>");
                }

                sb.AppendLine($"      <ExceptionHandling>{(module.EnableExceptions ? "Sync" : "false")}</ExceptionHandling>");
                sb.AppendLine($"      <RuntimeTypeInfo>{(module.EnableRTTI ? "true" : "false")}</RuntimeTypeInfo>");
                sb.AppendLine("      <MultiProcessorCompilation>true</MultiProcessorCompilation>");

                if (config == "Debug")
                {
                    sb.AppendLine("      <DebugInformationFormat>ProgramDatabase</DebugInformationFormat>");
                    sb.AppendLine("      <BasicRuntimeChecks>EnableFastChecks</BasicRuntimeChecks>");
                }
                else
                {
                    sb.AppendLine("      <FunctionLevelLinking>true</FunctionLevelLinking>");
                    sb.AppendLine("      <IntrinsicFunctions>true</IntrinsicFunctions>");
                }

                sb.AppendLine("    </ClCompile>");

                sb.AppendLine("    <Link>");
                sb.AppendLine("      <SubSystem>Console</SubSystem>");
                sb.AppendLine($"      <GenerateDebugInformation>{debugInfo}</GenerateDebugInformation>");
                if (config == "Shipping")
                {
                    sb.AppendLine("      <EnableCOMDATFolding>true</EnableCOMDATFolding>");
                    sb.AppendLine("      <OptimizeReferences>true</OptimizeReferences>");
                    sb.AppendLine("      <LinkTimeCodeGeneration>UseLinkTimeCodeGeneration</LinkTimeCodeGeneration>");
                }

                // Collect all libraries
                var libs = CollectSystemLibraries(module);
                
                // Add Qt libraries
                if (module.IsQtProject)
                {
                    libs.AddRange(GetQtLibraries(module, config));
                }
                
                libs.Add("%(AdditionalDependencies)");
                sb.AppendLine($"      <AdditionalDependencies>{string.Join(";", libs)}</AdditionalDependencies>");

                // Qt library paths
                if (module.IsQtProject)
                {
                    sb.AppendLine($"      <AdditionalLibraryDirectories>{GetQtLibPath(module)};%(AdditionalLibraryDirectories)</AdditionalLibraryDirectories>");
                }

                sb.AppendLine("    </Link>");

                // Librarian settings for static libraries
                if (configType == "StaticLibrary")
                {
                    sb.AppendLine("    <Lib>");
                    sb.AppendLine("    </Lib>");
                }

                sb.AppendLine("  </ItemDefinitionGroup>");
            }
        }

        // Source files. Not emitted for the main (NMake) module: `omen build` compiles
        // these, so MSBuild has no ClCompile items to invoke a compiler task on.
        if (!isMainModule)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var source in sourceFiles)
            {
                var relativePath = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, source);
                sb.AppendLine($"    <ClCompile Include=\"{relativePath}\" />");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        // Header files
        sb.AppendLine("  <ItemGroup>");
        foreach (var header in headerFiles)
        {
            var relativePath = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, header);
            sb.AppendLine($"    <ClInclude Include=\"{relativePath}\" />");
        }
        sb.AppendLine("  </ItemGroup>");

        // Qt UI files
        if (uiFiles.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var ui in uiFiles)
            {
                var relativePath = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, ui);
                sb.AppendLine($"    <QtUic Include=\"{relativePath}\" />");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        // Qt Resource files
        if (resourceFiles.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var qrc in resourceFiles)
            {
                var relativePath = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, qrc);
                sb.AppendLine($"    <QtRcc Include=\"{relativePath}\" />");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        // Project references for dependencies (for main module)
        if (isMainModule)
        {
            var deps = module.PublicDependencies.Concat(module.PrivateDependencies)
                .Where(d => _moduleDict.ContainsKey(d) && !_moduleDict[d].IsCSharpProject)
                .ToList();

            if (deps.Count > 0)
            {
                sb.AppendLine("  <ItemGroup>");
                foreach (var dep in deps)
                {
                    var depGuid = GenerateGuid(dep);
                    var depPath = Path.Combine(_projectRoot, "Intermediate", "ProjectFiles", dep, $"{dep}.vcxproj");
                    var relDepPath = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, depPath);
                    sb.AppendLine($"    <ProjectReference Include=\"{relDepPath}\">");
                    sb.AppendLine($"      <Project>{{{depGuid}}}</Project>");
                    sb.AppendLine("    </ProjectReference>");
                }
                sb.AppendLine("  </ItemGroup>");
            }
        }

        sb.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.targets\" />");

        // Qt targets
        if (module.IsQtProject)
        {
            sb.AppendLine("  <ImportGroup Condition=\"Exists('$(QtMsBuild)\\qt.targets')\">");
            sb.AppendLine("    <Import Project=\"$(QtMsBuild)\\qt.targets\" />");
            sb.AppendLine("  </ImportGroup>");
        }

        sb.AppendLine("  <ImportGroup Label=\"ExtensionTargets\">");
        sb.AppendLine("  </ImportGroup>");
        sb.AppendLine("</Project>");

        await File.WriteAllTextAsync(projectPath, sb.ToString(), ct);
    }

    #endregion

    #region Qt Support

    private string GetQtInstallName(ModuleRules module)
    {
        // Return the Qt install identifier for Qt VS Tools
        return module.QtVersion switch
        {
            QtVersion.Qt5 => "5.15.2_msvc2019_64",
            QtVersion.Qt6 => "6.8.3_msvc2022_64",
            _ => "6.8.3_msvc2022_64"
        };
    }

    private List<string> GetQtIncludePaths(ModuleRules module)
    {
        var qtPath = module.QtPath ?? GetDefaultQtPath(module.QtVersion);
        var paths = new List<string>
        {
            Path.Combine(qtPath, "include")
        };

        foreach (var mod in module.QtModules)
        {
            paths.Add(Path.Combine(qtPath, "include", $"Qt{mod}"));
        }

        return paths;
    }

    private string GetQtLibPath(ModuleRules module)
    {
        var qtPath = module.QtPath ?? GetDefaultQtPath(module.QtVersion);
        return Path.Combine(qtPath, "lib");
    }

    private List<string> GetQtLibraries(ModuleRules module, string config)
    {
        var libs = new List<string>();
        var suffix = config == "Debug" ? "d" : "";

        foreach (var mod in module.QtModules)
        {
            var majorVersion = module.QtVersion == QtVersion.Qt5 ? "5" : "6";
            libs.Add($"Qt{majorVersion}{mod}{suffix}.lib");
        }

        return libs;
    }

    private string GetDefaultQtPath(QtVersion version)
    {
        // Check environment variable first
        var qtDir = Environment.GetEnvironmentVariable("QTDIR");
        if (!string.IsNullOrEmpty(qtDir))
            return qtDir;

        // Default paths
        return version switch
        {
            QtVersion.Qt5 => "C:\\Qt\\5.15.2\\msvc2019_64",
            QtVersion.Qt6 => "C:\\Qt\\6.8.3\\msvc2022_64",
            _ => "C:\\Qt\\6.8.3\\msvc2022_64"
        };
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Determines whether <paramref name="module"/> is the module that actually drives
    /// <paramref name="target"/>'s build (the one converted to an NMake/Makefile project,
    /// and the only one whose solution entry is marked buildable). Prefers the target's
    /// explicit <see cref="TargetRules.LaunchModuleName"/> when set; falls back to
    /// dependency-topology (the root of the graph -- nothing depends on it -- that itself
    /// has dependencies) when unset. The topology fallback is only reliable for
    /// single-target projects: with multiple targets sharing a module dictionary, every
    /// target's root module would otherwise satisfy it independently and collide.
    /// </summary>
    private static bool IsMainModule(ModuleRules module, TargetRules target, IReadOnlyList<ModuleRules> allModules)
    {
        if (target.LaunchModuleName != null)
            return module.Name == target.LaunchModuleName;

        var hasDependents = allModules.Any(m =>
            m.PublicDependencies.Contains(module.Name) ||
            m.PrivateDependencies.Contains(module.Name));

        return !hasDependents && (module.PublicDependencies.Count > 0 || module.PrivateDependencies.Count > 0);
    }

    private List<string> BuildIncludePaths(ModuleRules module)
    {
        var paths = new List<string>();
        var visited = new HashSet<string>();

        void AddModuleIncludePaths(ModuleRules mod, bool includePrivate)
        {
            if (visited.Contains(mod.Name))
                return;
            visited.Add(mod.Name);

            var sourceDir = Path.Combine(_projectRoot, mod.SourceDirectory ?? $"Source/{mod.Name}");

            // Add module's source directory
            paths.Add(sourceDir);

            // Add Public subdirectory if it exists
            var publicDir = Path.Combine(sourceDir, "Public");
            if (Directory.Exists(publicDir))
            {
                paths.Add(publicDir);
            }

            // Add Private subdirectory only for the main module
            if (includePrivate)
            {
                var privateDir = Path.Combine(sourceDir, "Private");
                if (Directory.Exists(privateDir))
                {
                    paths.Add(privateDir);
                }
            }

            // Add explicit public include paths
            foreach (var p in mod.PublicIncludePaths)
            {
                var fullPath = Path.IsPathRooted(p) ? p : Path.Combine(sourceDir, p);
                if (!paths.Contains(fullPath))
                    paths.Add(fullPath);
            }

            // Add explicit private include paths only for main module
            if (includePrivate)
            {
                foreach (var p in mod.PrivateIncludePaths)
                {
                    var fullPath = Path.IsPathRooted(p) ? p : Path.Combine(sourceDir, p);
                    if (!paths.Contains(fullPath))
                        paths.Add(fullPath);
                }
            }

            // Recursively add dependencies' public include paths
            foreach (var depName in mod.PublicDependencies.Concat(mod.PrivateDependencies))
            {
                if (_moduleDict.TryGetValue(depName, out var depModule))
                {
                    AddModuleIncludePaths(depModule, false);
                }
            }
        }

        AddModuleIncludePaths(module, true);
        return paths;
    }

    private List<string> BuildDefinitions(ModuleRules module)
    {
        var defs = new List<string>();
        var visited = new HashSet<string>();

        void AddModuleDefinitions(ModuleRules mod, bool includePrivate)
        {
            if (visited.Contains(mod.Name))
                return;
            visited.Add(mod.Name);

            defs.AddRange(mod.PublicDefinitions);
            if (includePrivate)
            {
                defs.AddRange(mod.PrivateDefinitions);
            }

            foreach (var depName in mod.PublicDependencies.Concat(mod.PrivateDependencies))
            {
                if (_moduleDict.TryGetValue(depName, out var depModule))
                {
                    AddModuleDefinitions(depModule, false);
                }
            }
        }

        AddModuleDefinitions(module, true);
        return defs.Distinct().ToList();
    }

    private List<string> CollectSystemLibraries(ModuleRules module)
    {
        var libs = new List<string>();
        var visited = new HashSet<string>();

        void AddModuleLibs(ModuleRules mod)
        {
            if (visited.Contains(mod.Name))
                return;
            visited.Add(mod.Name);

            libs.AddRange(mod.PublicSystemLibraries);

            foreach (var depName in mod.PublicDependencies.Concat(mod.PrivateDependencies))
            {
                if (_moduleDict.TryGetValue(depName, out var depModule))
                {
                    AddModuleLibs(depModule);
                }
            }
        }

        AddModuleLibs(module);
        return libs.Distinct().ToList();
    }

    private async Task GenerateVcxprojFiltersAsync(
        ModuleRules module,
        string filtersPath,
        CancellationToken ct)
    {
        var sourceDir = Path.Combine(_projectRoot, module.SourceDirectory ?? $"Source/{module.Name}");

        var sourceFiles = new List<string>();
        var headerFiles = new List<string>();
        var uiFiles = new List<string>();
        var resourceFiles = new List<string>();

        if (Directory.Exists(sourceDir))
        {
            sourceFiles.AddRange(Directory.GetFiles(sourceDir, "*.cpp", SearchOption.AllDirectories));
            sourceFiles.AddRange(Directory.GetFiles(sourceDir, "*.c", SearchOption.AllDirectories));
            headerFiles.AddRange(Directory.GetFiles(sourceDir, "*.h", SearchOption.AllDirectories));
            headerFiles.AddRange(Directory.GetFiles(sourceDir, "*.hpp", SearchOption.AllDirectories));

            if (module.IsQtProject)
            {
                uiFiles.AddRange(Directory.GetFiles(sourceDir, "*.ui", SearchOption.AllDirectories));
                resourceFiles.AddRange(Directory.GetFiles(sourceDir, "*.qrc", SearchOption.AllDirectories));
            }
        }

        var filters = new HashSet<string>();

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");

        // Collect unique filter paths
        var allFiles = sourceFiles.Concat(headerFiles).Concat(uiFiles).Concat(resourceFiles);
        foreach (var file in allFiles)
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var dir = Path.GetDirectoryName(relativePath);
            if (!string.IsNullOrEmpty(dir))
            {
                var parts = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var current = "";
                foreach (var part in parts)
                {
                    current = string.IsNullOrEmpty(current) ? part : $"{current}\\{part}";
                    filters.Add(current);
                }
            }
        }

        // Write filters
        sb.AppendLine("  <ItemGroup>");
        foreach (var filter in filters.OrderBy(f => f))
        {
            var filterGuid = GenerateGuid(filter);
            sb.AppendLine($"    <Filter Include=\"{filter}\">");
            sb.AppendLine($"      <UniqueIdentifier>{{{filterGuid}}}</UniqueIdentifier>");
            sb.AppendLine("    </Filter>");
        }
        sb.AppendLine("  </ItemGroup>");

        // Source files with filters
        sb.AppendLine("  <ItemGroup>");
        foreach (var source in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(Path.GetDirectoryName(filtersPath)!, source);
            var relativeToSource = Path.GetRelativePath(sourceDir, source);
            var filterPath = Path.GetDirectoryName(relativeToSource)?.Replace('/', '\\');

            sb.AppendLine($"    <ClCompile Include=\"{relativePath}\">");
            if (!string.IsNullOrEmpty(filterPath))
            {
                sb.AppendLine($"      <Filter>{filterPath}</Filter>");
            }
            sb.AppendLine("    </ClCompile>");
        }
        sb.AppendLine("  </ItemGroup>");

        // Header files with filters
        sb.AppendLine("  <ItemGroup>");
        foreach (var header in headerFiles)
        {
            var relativePath = Path.GetRelativePath(Path.GetDirectoryName(filtersPath)!, header);
            var relativeToSource = Path.GetRelativePath(sourceDir, header);
            var filterPath = Path.GetDirectoryName(relativeToSource)?.Replace('/', '\\');

            sb.AppendLine($"    <ClInclude Include=\"{relativePath}\">");
            if (!string.IsNullOrEmpty(filterPath))
            {
                sb.AppendLine($"      <Filter>{filterPath}</Filter>");
            }
            sb.AppendLine("    </ClInclude>");
        }
        sb.AppendLine("  </ItemGroup>");

        // Qt UI files
        if (uiFiles.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var ui in uiFiles)
            {
                var relativePath = Path.GetRelativePath(Path.GetDirectoryName(filtersPath)!, ui);
                var relativeToSource = Path.GetRelativePath(sourceDir, ui);
                var filterPath = Path.GetDirectoryName(relativeToSource)?.Replace('/', '\\');

                sb.AppendLine($"    <QtUic Include=\"{relativePath}\">");
                if (!string.IsNullOrEmpty(filterPath))
                {
                    sb.AppendLine($"      <Filter>{filterPath}</Filter>");
                }
                sb.AppendLine("    </QtUic>");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        // Qt Resource files
        if (resourceFiles.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var qrc in resourceFiles)
            {
                var relativePath = Path.GetRelativePath(Path.GetDirectoryName(filtersPath)!, qrc);
                var relativeToSource = Path.GetRelativePath(sourceDir, qrc);
                var filterPath = Path.GetDirectoryName(relativeToSource)?.Replace('/', '\\');

                sb.AppendLine($"    <QtRcc Include=\"{relativePath}\">");
                if (!string.IsNullOrEmpty(filterPath))
                {
                    sb.AppendLine($"      <Filter>{filterPath}</Filter>");
                }
                sb.AppendLine("    </QtRcc>");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine("</Project>");

        await File.WriteAllTextAsync(filtersPath, sb.ToString(), ct);
    }

    private async Task GenerateVcxprojUserAsync(
        ModuleRules module,
        string userPath,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<Project ToolsVersion=\"Current\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");

        foreach (var config in new[] { "Debug", "Development", "Shipping" })
        {
            foreach (var platform in new[] { "x64", "ARM64" })
            {
                sb.AppendLine($"  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='{config}|{platform}'\">");
                sb.AppendLine($"    <LocalDebuggerWorkingDirectory>$(SolutionDir)</LocalDebuggerWorkingDirectory>");
                sb.AppendLine("    <DebuggerFlavor>WindowsLocalDebugger</DebuggerFlavor>");

                // Add Qt DLL path to debugger environment
                if (module.IsQtProject)
                {
                    var qtPath = module.QtPath ?? GetDefaultQtPath(module.QtVersion);
                    sb.AppendLine($"    <LocalDebuggerEnvironment>PATH={Path.Combine(qtPath, "bin")};%PATH%</LocalDebuggerEnvironment>");
                }

                sb.AppendLine("  </PropertyGroup>");
            }
        }

        sb.AppendLine("</Project>");

        await File.WriteAllTextAsync(userPath, sb.ToString(), ct);
    }

    private static Guid GenerateGuid(string name)
    {
        // Generate a deterministic GUID based on name
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes($"Omen.{name}"));
        return new Guid(hash);
    }

    #endregion
}
