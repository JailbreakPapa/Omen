using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Omen.Core.Configuration;
using Omen.Core.Generators;
using Omen.Core.Graph;
using Omen.Core.Implementations;
using Omen.Core.Interfaces;
using Omen.Core.Options;
using Omen.Core.Reflect;
using Omen.Core.Rules;
using Omen.Platforms;

namespace Omen.Executors.Orchestration;

public sealed class ProjectGenerationOrchestrator
{
	public async Task<bool> GenerateAsync(ProjectGenerationOrchestratorRequest request, IProgress<OrchestratorEvent>? events)
	{
		string workingDir = request.ProjectRoot;
		events?.Report(new OrchestratorEvent($"Generating project files for {request.Ide}...", OrchestratorEventLevel.Info));
		Stopwatch phaseClock = Stopwatch.StartNew();
		RuleCompiler ruleCompiler = new RuleCompiler(RuleCompiler.CacheDirectoryFor(workingDir));
		if ((await ruleCompiler.DiscoverRuleFilesAsync(workingDir)).TargetFiles.Count == 0)
		{
			events?.Report(new OrchestratorEvent("No target file found. Create a .target.cs file first.", OrchestratorEventLevel.Error));
			return false;
		}
		Phase("Rule discovery");
		CompiledRules compiledRules;
		try
		{
			compiledRules = await ruleCompiler.CompileRulesAsync(workingDir);
		}
		catch (Exception ex)
		{
			events?.Report(new OrchestratorEvent("Error compiling rules: " + ex.Message, OrchestratorEventLevel.Error));
			return false;
		}
		Phase("Rule compilation");
		IReadOnlyList<Type> targetTypes = compiledRules.FindTargetTypes();
		if (targetTypes.Count == 0)
		{
			events?.Report(new OrchestratorEvent("No target found.", OrchestratorEventLevel.Error));
			return false;
		}
		List<string> enabledPlugins = compiledRules.Project?.EnabledPlugins;
		PluginVariant variant = compiledRules.Project?.Variant ?? PluginVariant.Unified;
		OptionCacheStore optionCacheStore = new OptionCacheStore(Path.Combine(workingDir, "Intermediate", "omen-cache.json"));
		IReadOnlyDictionary<string, string> cachedOptionValues = optionCacheStore.Load();
		List<(TargetRules, IReadOnlyList<ModuleRules>, BuildContext)> combinations = new List<(TargetRules, IReadOnlyList<ModuleRules>, BuildContext)>();
		Dictionary<TargetPlatform, CompilerFamily?> compilerFamilies = new Dictionary<TargetPlatform, CompilerFamily?>();
		Dictionary<(TargetPlatform, TargetArchitecture, BuildConfiguration, string), (BuildContext Context, IReadOnlyList<TargetRules> Targets, IReadOnlyList<ModuleRules> Modules)> rulesByContext = new Dictionary<(TargetPlatform, TargetArchitecture, BuildConfiguration, string), (BuildContext, IReadOnlyList<TargetRules>, IReadOnlyList<ModuleRules>)>();
		Dictionary<Type, IReadOnlyList<ResolvedTarget>?> moduleRowsByType = new Dictionary<Type, IReadOnlyList<ResolvedTarget>>();
		foreach (Type targetType in targetTypes)
		{
			TargetMatrix matrix = compiledRules.GetSupportedTargets(targetType);
			if (matrix == null)
			{
				BuildConfiguration[] array = new BuildConfiguration[3]
				{
					BuildConfiguration.Debug,
					BuildConfiguration.Development,
					BuildConfiguration.Shipping
				};
				foreach (BuildConfiguration configuration in array)
				{
					TargetArchitecture[] array2 = new TargetArchitecture[2]
					{
						TargetArchitecture.X64,
						TargetArchitecture.ARM64
					};
					foreach (TargetArchitecture architecture in array2)
					{
						(BuildContext, IReadOnlyList<TargetRules>, IReadOnlyList<ModuleRules>) rules = RulesFor(TargetPlatform.Windows, architecture, configuration, Path.Combine(workingDir, "Intermediate"), Path.Combine(workingDir, "Binaries"));
						combinations.Add((rules.Item2.First((TargetRules t) => t.GetType() == targetType), rules.Item3, rules.Item1));
					}
				}
				continue;
			}
			IReadOnlyList<ResolvedTarget> resolvedTargets = matrix.Resolve();
			if (resolvedTargets.Count == 0)
			{
				events?.Report(new OrchestratorEvent("'" + targetType.Name + "'s SupportedTargets matrix resolved to zero combinations - check its Exclude rules. Skipped.", OrchestratorEventLevel.Warning));
				continue;
			}
			foreach (ResolvedTarget resolved in resolvedTargets)
			{
				TargetPlatform platform = resolved.Get<TargetPlatform>();
				BuildConfiguration configuration2 = resolved.Get<BuildConfiguration>();
				(BuildContext, IReadOnlyList<TargetRules>, IReadOnlyList<ModuleRules>) rules2 = RulesFor(platform, resolved.Get<TargetArchitecture>(), configuration2, Path.Combine(workingDir, "Intermediate", $"{platform}_{configuration2}"), Path.Combine(workingDir, "Binaries", $"{platform}_{configuration2}"));
				LinkType? linkType = compiledRules.Project?.LinkTypeOverride;
				if (linkType.HasValue)
				{
					LinkType projectLinkType = linkType.GetValueOrDefault();
					foreach (TargetRules item in rules2.Item2)
					{
						item.LinkType = projectLinkType;
					}
				}
				combinations.Add((rules2.Item2.First((TargetRules t) => t.GetType() == targetType), rules2.Item3.Where((ModuleRules m) => ModuleBuildsIn(m, resolved)).ToList(), rules2.Item1));
			}
		}
		if (combinations.Count == 0)
		{
			events?.Report(new OrchestratorEvent("No target resolved to any build combination.", OrchestratorEventLevel.Error));
			return false;
		}
		Phase($"Rule instantiation ({rulesByContext.Count} build contexts, {combinations.Count} combinations)");
		TargetRules target = combinations[0].Item1;
		IReadOnlyList<ModuleRules> modules = combinations[0].Item2;
		BuildContext context = combinations[0].Item3;
		switch (request.Ide)
		{
		case IdeKind.VS2019:
			await GenerateVisualStudioAsync(workingDir, combinations, VisualStudioGenerator.VisualStudioVersion.VS2019, compiledRules.Project, events);
			break;
		case IdeKind.VS2022:
			await GenerateVisualStudioAsync(workingDir, combinations, VisualStudioGenerator.VisualStudioVersion.VS2022, compiledRules.Project, events);
			break;
		case IdeKind.VS2026:
			await GenerateVisualStudioAsync(workingDir, combinations, VisualStudioGenerator.VisualStudioVersion.VS2026, compiledRules.Project, events);
			break;
		case IdeKind.VSCode:
			await GenerateVSCodeAsync(workingDir, target, modules, events);
			break;
		case IdeKind.CMake:
			await GenerateCMakeAsync(workingDir, target, modules, combinations[0].Item3, events);
			break;
		case IdeKind.Rider:
			await GenerateCMakeAsync(workingDir, target, modules, combinations[0].Item3, events);
			events?.Report(new OrchestratorEvent("Rider can open the generated CMakeLists.txt", OrchestratorEventLevel.Info));
			break;
		}
		Phase("IDE files");
		IToolchain toolchain = PlatformFactory.CreateToolchain(context.Platform, context.Architecture);
		if (toolchain != null && modules.Count > 0)
		{
			Sha256DigestCalculator digestCalculator = new Sha256DigestCalculator();
			ActionGraphBuilder actionGraphBuilder = new ActionGraphBuilder(context, toolchain, digestCalculator)
			{
				ScanIncludes = false
			};
			string annotationCachePath = Path.Combine(context.IntermediateDirectory, ".buildtool", "reflect-annotations.json");
			AnnotationCache.LoadFrom(annotationCachePath);
			ActionGraph graph = actionGraphBuilder.Build(target, modules);
			AnnotationCache.SaveTo(annotationCachePath);
			CompileCommandsWriter.Write(graph, Path.Combine(workingDir, "compile_commands.json"));
			events?.Report(new OrchestratorEvent("Generated compile_commands.json", OrchestratorEventLevel.Success));
			Phase("compile_commands.json");
		}
		return true;
		bool ModuleBuildsIn(ModuleRules module, ResolvedTarget row)
		{
			if (!moduleRowsByType.TryGetValue(module.GetType(), out IReadOnlyList<ResolvedTarget> moduleRows))
			{
				moduleRows = compiledRules.GetSupportedTargets(module.GetType())?.Resolve();
				moduleRowsByType[module.GetType()] = moduleRows;
			}
			return moduleRows?.Any((ResolvedTarget c) => c.Values.All((KeyValuePair<Type, Enum> kv) => row.Values.TryGetValue(kv.Key, out Enum value) && object.Equals(value, kv.Value))) ?? true;
		}
		void Phase(string name)
		{
			events?.Report(new OrchestratorEvent($"  {name}: {phaseClock.Elapsed.TotalSeconds:F1}s", OrchestratorEventLevel.Info));
			phaseClock.Restart();
		}
		(BuildContext Context, IReadOnlyList<TargetRules> Targets, IReadOnlyList<ModuleRules> Modules) RulesFor(TargetPlatform targetPlatform, TargetArchitecture targetArchitecture, BuildConfiguration buildConfiguration, string intermediateDirectory, string outputDirectory)
		{
			(TargetPlatform, TargetArchitecture, BuildConfiguration, string) key = (targetPlatform, targetArchitecture, buildConfiguration, intermediateDirectory);
			if (rulesByContext.TryGetValue(key, out (BuildContext, IReadOnlyList<TargetRules>, IReadOnlyList<ModuleRules>) cached))
			{
				return cached;
			}
			BuildContext buildContext = WithGlobalOptions(new BuildContext
			{
				Platform = targetPlatform,
				Architecture = targetArchitecture,
				Configuration = buildConfiguration,
				ProjectRoot = workingDir,
				IntermediateDirectory = intermediateDirectory,
				OutputDirectory = outputDirectory,
				CachedOptionValues = cachedOptionValues,
				UseUnityBuild = (compiledRules.Project?.UnityBuild ?? true),
				UsePCH = (compiledRules.Project?.PrecompiledHeaders ?? true)
			});
			(BuildContext, IReadOnlyList<TargetRules>, IReadOnlyList<ModuleRules>) rules3 = (buildContext, compiledRules.CreateTargetRules(buildContext), PluginEnablement.FilterEnabledModules(compiledRules.CreateModuleRules(buildContext), enabledPlugins, variant, compiledRules.CreatePluginRules(buildContext)));
			rulesByContext[key] = rules3;
			return rules3;
		}
		BuildContext WithGlobalOptions(BuildContext buildContext)
		{
			if (!compilerFamilies.TryGetValue(buildContext.Platform, out var family))
			{
				family = PlatformFactory.CreateToolchain(buildContext.Platform, buildContext.Architecture)?.CompilerFamily;
				compilerFamilies[buildContext.Platform] = family;
			}
			if (family.HasValue)
			{
				CompilerFamily resolvedFamily = family.GetValueOrDefault();
				GlobalToolchainOptions.ApplyTo(buildContext, compiledRules.Project, resolvedFamily);
			}
			return buildContext;
		}
	}

	private static async Task GenerateVisualStudioAsync(string projectRoot, IReadOnlyList<(TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context)> combinations, VisualStudioGenerator.VisualStudioVersion version, OmenProjectRules? project, IProgress<OrchestratorEvent>? events)
	{
		TargetRules target = combinations[0].Target;
		IReadOnlyList<ModuleRules> modules = combinations[0].Modules;
		string solutionName = project?.SolutionName ?? project?.Name ?? target.Name;
		VisualStudioGenerator generator = new VisualStudioGenerator(projectRoot, version);
		if (combinations.Count != 1)
		{
			await generator.GenerateAsync(combinations, default, solutionName);
		}
		else
		{
			await generator.GenerateAsync(combinations[0].Target, combinations[0].Modules, combinations[0].Context, default, solutionName);
		}
		string solutionPath = Path.Combine(projectRoot, solutionName + ".sln");
		events?.Report(new OrchestratorEvent("Generated Visual Studio solution: " + solutionPath, OrchestratorEventLevel.Success));
		int projectCount = modules.Count((ModuleRules m) => !m.Name.StartsWith("3rdParty::", StringComparison.OrdinalIgnoreCase) && !m.Name.StartsWith("Vcpkg::", StringComparison.OrdinalIgnoreCase));
		events?.Report(new OrchestratorEvent($"{projectCount} project(s) generated", OrchestratorEventLevel.Info));
		events?.Report(new OrchestratorEvent("Project files in: Intermediate/ProjectFiles/", OrchestratorEventLevel.Info));
	}

	private static async Task GenerateVSCodeAsync(string projectRoot, TargetRules target, IReadOnlyList<ModuleRules> modules, IProgress<OrchestratorEvent>? events)
	{
		string vscodeDir = Path.Combine(projectRoot, ".vscode");
		Directory.CreateDirectory(vscodeDir);
		List<string> includePaths = new List<string> { "${workspaceFolder}/**" };
		foreach (ModuleRules module in modules)
		{
			string sourceDir = module.SourceDirectory ?? ("Source/" + module.Name);
			includePaths.Add("${workspaceFolder}/" + sourceDir + "/**");
		}
		string cppProperties = $"{{\r\n    \"configurations\": [\r\n        {{\r\n            \"name\": \"Win32\",\r\n            \"includePath\": [\r\n                {string.Join(",\n                        ", includePaths.Select((string p) => "\"" + p + "\""))}\r\n            ],\r\n            \"defines\": [\r\n                \"_DEBUG\",\r\n                \"UNICODE\",\r\n                \"_UNICODE\"\r\n            ],\r\n            \"windowsSdkVersion\": \"10.0.22621.0\",\r\n            \"compilerPath\": \"cl.exe\",\r\n            \"cStandard\": \"c17\",\r\n            \"cppStandard\": \"c++20\",\r\n            \"intelliSenseMode\": \"windows-msvc-x64\"\r\n        }},\r\n        {{\r\n            \"name\": \"Linux\",\r\n            \"includePath\": [\r\n                {string.Join(",\n                        ", includePaths.Select((string p) => "\"" + p + "\""))}\r\n            ],\r\n            \"defines\": [],\r\n            \"compilerPath\": \"/usr/bin/clang\",\r\n            \"cStandard\": \"c17\",\r\n            \"cppStandard\": \"c++20\",\r\n            \"intelliSenseMode\": \"linux-clang-x64\"\r\n        }}\r\n    ],\r\n    \"version\": 4\r\n}}";
		await GeneratedFile.WriteAllTextAsync(Path.Combine(vscodeDir, "c_cpp_properties.json"), cppProperties);
		string tasks = "{\r\n    \"version\": \"2.0.0\",\r\n    \"tasks\": [\r\n        {\r\n            \"label\": \"Omen: Build (Debug)\",\r\n            \"type\": \"shell\",\r\n            \"command\": \"omen\",\r\n            \"args\": [\"build\", \"-c\", \"Debug\"],\r\n            \"group\": {\r\n                \"kind\": \"build\",\r\n                \"isDefault\": true\r\n            },\r\n            \"problemMatcher\": [\"$msCompile\"]\r\n        },\r\n        {\r\n            \"label\": \"Omen: Build (Development)\",\r\n            \"type\": \"shell\",\r\n            \"command\": \"omen\",\r\n            \"args\": [\"build\", \"-c\", \"Development\"],\r\n            \"group\": \"build\",\r\n            \"problemMatcher\": [\"$msCompile\"]\r\n        },\r\n        {\r\n            \"label\": \"Omen: Build (Shipping)\",\r\n            \"type\": \"shell\",\r\n            \"command\": \"omen\",\r\n            \"args\": [\"build\", \"-c\", \"Shipping\"],\r\n            \"group\": \"build\",\r\n            \"problemMatcher\": [\"$msCompile\"]\r\n        },\r\n        {\r\n            \"label\": \"Omen: Clean\",\r\n            \"type\": \"shell\",\r\n            \"command\": \"omen\",\r\n            \"args\": [\"clean\"],\r\n            \"problemMatcher\": []\r\n        },\r\n        {\r\n            \"label\": \"Omen: Rebuild\",\r\n            \"type\": \"shell\",\r\n            \"command\": \"omen\",\r\n            \"args\": [\"rebuild\", \"-c\", \"Debug\"],\r\n            \"problemMatcher\": [\"$msCompile\"]\r\n        }\r\n    ]\r\n}";
		await GeneratedFile.WriteAllTextAsync(Path.Combine(vscodeDir, "tasks.json"), tasks);
		string launch = $"{{\r\n    \"version\": \"0.2.0\",\r\n    \"configurations\": [\r\n        {{\r\n            \"name\": \"{target.Name} (Debug)\",\r\n            \"type\": \"cppvsdbg\",\r\n            \"request\": \"launch\",\r\n            \"program\": \"${{workspaceFolder}}/Binaries/Windows_Debug/{target.Name}.exe\",\r\n            \"args\": [],\r\n            \"stopAtEntry\": false,\r\n            \"cwd\": \"${{workspaceFolder}}\",\r\n            \"environment\": [],\r\n            \"console\": \"integratedTerminal\",\r\n            \"preLaunchTask\": \"Omen: Build (Debug)\"\r\n        }},\r\n        {{\r\n            \"name\": \"{target.Name} (Development)\",\r\n            \"type\": \"cppvsdbg\",\r\n            \"request\": \"launch\",\r\n            \"program\": \"${{workspaceFolder}}/Binaries/Windows_Development/{target.Name}.exe\",\r\n            \"args\": [],\r\n            \"stopAtEntry\": false,\r\n            \"cwd\": \"${{workspaceFolder}}\",\r\n            \"environment\": [],\r\n            \"console\": \"integratedTerminal\",\r\n            \"preLaunchTask\": \"Omen: Build (Development)\"\r\n        }}\r\n    ]\r\n}}";
		await GeneratedFile.WriteAllTextAsync(Path.Combine(vscodeDir, "launch.json"), launch);
		events?.Report(new OrchestratorEvent("Generated VS Code configuration in .vscode/", OrchestratorEventLevel.Success));
		events?.Report(new OrchestratorEvent("c_cpp_properties.json (IntelliSense)", OrchestratorEventLevel.Info));
		events?.Report(new OrchestratorEvent("tasks.json (Build tasks)", OrchestratorEventLevel.Info));
		events?.Report(new OrchestratorEvent("launch.json (Debug configurations)", OrchestratorEventLevel.Info));
	}

	private static async Task GenerateCMakeAsync(string projectRoot, TargetRules target, IReadOnlyList<ModuleRules> modules, BuildContext context, IProgress<OrchestratorEvent>? events)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("# Generated by Omen Build System");
		sb.AppendLine("cmake_minimum_required(VERSION 3.20)");
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder);
		handler.AppendLiteral("project(");
		handler.AppendFormatted(target.Name);
		handler.AppendLiteral(" CXX)");
		stringBuilder2.AppendLine(ref handler);
		sb.AppendLine();
		sb.AppendLine("set(CMAKE_CXX_STANDARD 20)");
		sb.AppendLine("set(CMAKE_CXX_STANDARD_REQUIRED ON)");
		sb.AppendLine("set(CMAKE_EXPORT_COMPILE_COMMANDS ON)");
		sb.AppendLine();
		List<string> list = new List<string>();
		list.Add("OMEN_" + context.Configuration.ToString().ToUpperInvariant() + "=1");
		list.Add("OMEN_" + context.Platform.ToString().ToUpperInvariant() + "=1");
		list.AddRange(context.GlobalDefinitions);
		list.AddRange(target.GlobalDefinitions);
		sb.AppendLine("add_compile_definitions(");
		foreach (string definition in list.Distinct())
		{
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder);
			handler.AppendLiteral("    ");
			handler.AppendFormatted(definition);
			stringBuilder3.AppendLine(ref handler);
		}
		sb.AppendLine(")");
		sb.AppendLine();
		foreach (ModuleRules module in modules)
		{
			string sourceDir = module.SourceDirectory ?? ("Source/" + module.Name);
			stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder);
			handler.AppendLiteral("# Module: ");
			handler.AppendFormatted(module.Name);
			stringBuilder4.AppendLine(ref handler);
			List<string> sources = (from f in module.ResolveSourceFiles(projectRoot, ".cpp", ".c")
				select Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).ToList();
			if (sources.Count <= 0)
			{
				continue;
			}
			string libType = ((module.Type == ModuleType.Runtime) ? "SHARED" : "STATIC");
			stringBuilder = sb;
			StringBuilder stringBuilder5 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 2, stringBuilder);
			handler.AppendLiteral("add_library(");
			handler.AppendFormatted(module.Name);
			handler.AppendLiteral(" ");
			handler.AppendFormatted(libType);
			stringBuilder5.AppendLine(ref handler);
			foreach (string source in sources)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder6 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder);
				handler.AppendLiteral("    ");
				handler.AppendFormatted(source);
				stringBuilder6.AppendLine(ref handler);
			}
			sb.AppendLine(")");
			sb.AppendLine();
			stringBuilder = sb;
			StringBuilder stringBuilder7 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder);
			handler.AppendLiteral("target_include_directories(");
			handler.AppendFormatted(module.Name);
			handler.AppendLiteral(" PUBLIC");
			stringBuilder7.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder8 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder);
			handler.AppendLiteral("    ");
			handler.AppendFormatted(sourceDir);
			stringBuilder8.AppendLine(ref handler);
			foreach (string inc in module.PublicIncludePaths)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder9 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(5, 2, stringBuilder);
				handler.AppendLiteral("    ");
				handler.AppendFormatted(sourceDir);
				handler.AppendLiteral("/");
				handler.AppendFormatted(inc);
				stringBuilder9.AppendLine(ref handler);
			}
			sb.AppendLine(")");
			if (module.PrivateIncludePaths.Count > 0)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder10 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(35, 1, stringBuilder);
				handler.AppendLiteral("target_include_directories(");
				handler.AppendFormatted(module.Name);
				handler.AppendLiteral(" PRIVATE");
				stringBuilder10.AppendLine(ref handler);
				foreach (string inc2 in module.PrivateIncludePaths)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder11 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(5, 2, stringBuilder);
					handler.AppendLiteral("    ");
					handler.AppendFormatted(sourceDir);
					handler.AppendLiteral("/");
					handler.AppendFormatted(inc2);
					stringBuilder11.AppendLine(ref handler);
				}
				sb.AppendLine(")");
			}
			if (module.PublicDefinitions.Count > 0 || module.PrivateDefinitions.Count > 0)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder12 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(27, 1, stringBuilder);
				handler.AppendLiteral("target_compile_definitions(");
				handler.AppendFormatted(module.Name);
				stringBuilder12.AppendLine(ref handler);
				foreach (string def in module.PublicDefinitions)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder13 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
					handler.AppendLiteral("    PUBLIC ");
					handler.AppendFormatted(def);
					stringBuilder13.AppendLine(ref handler);
				}
				foreach (string def2 in module.PrivateDefinitions)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder14 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder);
					handler.AppendLiteral("    PRIVATE ");
					handler.AppendFormatted(def2);
					stringBuilder14.AppendLine(ref handler);
				}
				sb.AppendLine(")");
			}
			if (module.PublicDependencies.Count > 0)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder15 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(29, 1, stringBuilder);
				handler.AppendLiteral("target_link_libraries(");
				handler.AppendFormatted(module.Name);
				handler.AppendLiteral(" PUBLIC");
				stringBuilder15.AppendLine(ref handler);
				foreach (string dep in module.PublicDependencies)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder16 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder);
					handler.AppendLiteral("    ");
					handler.AppendFormatted(dep);
					stringBuilder16.AppendLine(ref handler);
				}
				sb.AppendLine(")");
			}
			if (module.IsPrivateTarget)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder17 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(59, 1, stringBuilder);
				handler.AppendLiteral("set_target_properties(");
				handler.AppendFormatted(module.Name);
				handler.AppendLiteral(" PROPERTIES O3DE_PRIVATE_TARGET TRUE)");
				stringBuilder17.AppendLine(ref handler);
			}
			sb.AppendLine();
		}
		sb.AppendLine("# Main executable");
		stringBuilder = sb;
		StringBuilder stringBuilder18 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder);
		handler.AppendLiteral("add_executable(");
		handler.AppendFormatted(target.Name);
		handler.AppendLiteral("_exe");
		stringBuilder18.AppendLine(ref handler);
		sb.AppendLine("    # Add your main.cpp here");
		sb.AppendLine(")");
		sb.AppendLine();
		stringBuilder = sb;
		StringBuilder stringBuilder19 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder);
		handler.AppendLiteral("target_link_libraries(");
		handler.AppendFormatted(target.Name);
		handler.AppendLiteral("_exe PRIVATE");
		stringBuilder19.AppendLine(ref handler);
		foreach (ModuleRules module2 in modules)
		{
			stringBuilder = sb;
			StringBuilder stringBuilder20 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder);
			handler.AppendLiteral("    ");
			handler.AppendFormatted(module2.Name);
			stringBuilder20.AppendLine(ref handler);
		}
		sb.AppendLine(")");
		sb.AppendLine();
		stringBuilder = sb;
		StringBuilder stringBuilder21 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(53, 2, stringBuilder);
		handler.AppendLiteral("set_target_properties(");
		handler.AppendFormatted(target.Name);
		handler.AppendLiteral("_exe PROPERTIES OUTPUT_NAME \"");
		handler.AppendFormatted(target.Name);
		handler.AppendLiteral("\")");
		stringBuilder21.AppendLine(ref handler);
		await GeneratedFile.WriteAllTextAsync(Path.Combine(projectRoot, "CMakeLists.txt"), sb.ToString());
		events?.Report(new OrchestratorEvent("Generated CMakeLists.txt", OrchestratorEventLevel.Success));
		events?.Report(new OrchestratorEvent($"{modules.Count} module(s) configured", OrchestratorEventLevel.Info));
		events?.Report(new OrchestratorEvent("Use: cmake -B build && cmake --build build", OrchestratorEventLevel.Info));
	}
}
