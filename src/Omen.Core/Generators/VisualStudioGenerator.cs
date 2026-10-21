using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Omen.Core.Build;
using Omen.Core.Configuration;
using Omen.Core.Qt;
using Omen.Core.Rules;

namespace Omen.Core.Generators;

/// <summary>
/// Generates Visual Studio solution and project files.
/// Supports C++ (.vcxproj), C# (.csproj), and Qt projects.
/// </summary>
public sealed class VisualStudioGenerator
{
	public enum VisualStudioVersion
	{
		VS2019,
		VS2022,
		VS2026
	}

	private readonly string _projectRoot;

	private readonly VisualStudioVersion _version;

	private Dictionary<string, ModuleRules> _moduleDict = new Dictionary<string, ModuleRules>();

	/// <summary>Module name -&gt; Visual Studio project name for this generation; see ProjectNamesFor.</summary>
	private Dictionary<string, string> _projectNames = new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>Source directories two or more modules declare - see BrowsableFiles.</summary>
	private HashSet<string> _sharedSourceDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// One BuildContext per configuration name, so definitions resolve per config rather than
	/// once for the whole .vcxproj. GlobalDefinitions are toolchain options per compiler family
	/// AND configuration, so resolving once and reusing across configs would leak one config's
	/// defines into the others.
	/// </summary>
	private Dictionary<string, BuildContext> _contextsByConfig = new Dictionary<string, BuildContext>();

	/// <summary>
	/// Today's hardcoded solution/project configuration list, used whenever a caller (the
	/// single-combination GenerateAsync above, or the multi-combination one for any module
	/// not otherwise gated) doesn't supply its own set.
	/// </summary>
	private static readonly IReadOnlyList<(string Configuration, string Platform)> DefaultConfigPlatforms = (from config in new string[3] { "Debug", "Development", "Shipping" }
		from platform in new string[2] { "x64", "ARM64" }
		select (config: config, platform: platform)).ToList();

	/// <summary>
	/// Every header extension the projects list for browsing. O3DE alone has hundreds of
	/// .inl files; listing only .h/.hpp left them (and .hxx/.inc/.ipp) out of every project.
	/// </summary>
	private static readonly string[] HeaderExtensions = new string[7] { ".h", ".hpp", ".hh", ".hxx", ".inl", ".inc", ".ipp" };

	/// <summary>
	/// The files a module contributes to its project for browsing. A prebuilt third-party
	/// package contributes none: it compiles nothing, and its SourceDirectory points at the
	/// package root - or, for an O3DE 3rdParty rule converted with SourceDirectory ".", at the
	/// whole 3rdParty tree. Globbing that puts thousands of files nobody edits into the
	/// project, and the same thousands into every such project, because they all share the one
	/// root. Nine of them accounted for 62% of every item in the generated solution.
	///
	/// For everything else this is the UNION of the module's resolved files (the explicit
	/// SourceFiles list, when one exists) and a glob of its source directory: the explicit
	/// list stays authoritative for what compiles, but a project that hides every file the
	/// list doesn't name is useless as a browsing surface - the FILES_CMAKE-converted
	/// NightFoxCore listed 694 of its 916 on-disk headers and the rest were simply invisible.
	/// </summary>
	private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _browsableFiles = new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

	public VisualStudioGenerator(string projectRoot, VisualStudioVersion version = VisualStudioVersion.VS2022)
	{
		_projectRoot = projectRoot;
		_version = version;
	}

	/// <summary>
	/// Generates a Visual Studio solution from compiled rules.
	/// </summary>
	public async Task GenerateAsync(TargetRules target, IReadOnlyList<ModuleRules> modules, BuildContext context, CancellationToken ct = default(CancellationToken), string? solutionName = null)
	{
		_contextsByConfig = new Dictionary<string, BuildContext>(StringComparer.OrdinalIgnoreCase) { [context.Configuration.ToString()] = context };
		if (solutionName == null)
		{
			solutionName = target.Name;
		}
		string solutionPath = Path.Combine(_projectRoot, solutionName + ".sln");
		string projectsDir = Path.Combine(_projectRoot, "Intermediate", "ProjectFiles");
		Directory.CreateDirectory(projectsDir);
		_moduleDict = modules.ToDictionary((ModuleRules m) => m.Name);
		_projectNames = ProjectNamesFor(modules);
		_browsableFiles.Clear();
		_sharedSourceDirectories = SharedSourceDirectories(modules);
		List<(string Name, string Path, Guid Guid, bool IsCSharp)> projects = new List<(string, string, Guid, bool)>();
		foreach (ModuleRules module in modules)
		{
			if (!(module is ThirdPartyPackageModuleRules))
			{
				Guid projectGuid = GenerateGuid(module.Name);
				if (module.IsCSharpProject)
				{
					string projectPath = Path.Combine(projectsDir, ProjectName(module.Name), ProjectName(module.Name) + ".csproj");
					await GenerateCsprojAsync(module, projectPath, target, modules, ct);
					projects.Add((module.Name, projectPath, projectGuid, true));
				}
				else
				{
					string projectPath = Path.Combine(projectsDir, ProjectName(module.Name), ProjectName(module.Name) + ".vcxproj");
					await GenerateVcxprojAsync(module, projectPath, target, modules, ct);
					await GenerateVcxprojFiltersAsync(module, projectPath + ".filters", ct);
					await GenerateVcxprojUserAsync(module, projectPath + ".user", ct);
					projects.Add((module.Name, projectPath, projectGuid, false));
				}
			}
		}
		RemoveStaleProjectDirectories(projectsDir, projects.Select(((string Name, string Path, Guid Guid, bool IsCSharp) p) => p.Path));
		await GenerateSolutionAsync(solutionPath, solutionName, target, projects, modules, ct);
	}

	/// <summary>
	/// Multi-combination generation - each tuple's Context.Platform/Context.Configuration
	/// becomes one &lt;ProjectConfiguration&gt; entry instead of a hardcoded Debug/Development/
	/// Shipping x x64/ARM64 list. Tuples may span several targets (one per *.target.cs, each
	/// crossed with its own matrix): every target's launch module becomes that target's NMake
	/// project, configured only for the rows that target declares. Modules legitimately vary
	/// per combination (module gating); a module's identity must not.
	/// </summary>
	public async Task GenerateAsync(IReadOnlyList<(TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context)> combinations, CancellationToken ct = default(CancellationToken), string? solutionName = null)
	{
		TargetRules target = combinations[0].Target;
		if (solutionName == null)
		{
			solutionName = target.Name;
		}
		string solutionPath = Path.Combine(_projectRoot, solutionName + ".sln");
		string projectsDir = Path.Combine(_projectRoot, "Intermediate", "ProjectFiles");
		Directory.CreateDirectory(projectsDir);
		List<ModuleRules> allModules = (from m in combinations.SelectMany(((TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context) c) => c.Modules)
			group m by m.Name into g
			select g.First()).ToList();
		_moduleDict = allModules.ToDictionary((ModuleRules m) => m.Name);
		_projectNames = ProjectNamesFor(allModules);
		_browsableFiles.Clear();
		_sharedSourceDirectories = SharedSourceDirectories(allModules);
		_contextsByConfig = (from c in combinations
			group c by c.Context.Configuration.ToString()).ToDictionary((IGrouping<string, (TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context)> g) => g.Key, (IGrouping<string, (TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context)> g) => g.First().Context, StringComparer.OrdinalIgnoreCase);
		List<TargetRules> allTargets = combinations.Select(((TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context) c) => c.Target).DistinctBy((TargetRules t) => t.Name).ToList();
		Dictionary<string, TargetRules> moduleOwningTarget = allModules.ToDictionary((ModuleRules m) => m.Name, (ModuleRules m) => allTargets.FirstOrDefault((TargetRules t) => t.LaunchModuleName == m.Name) ?? allTargets.FirstOrDefault((TargetRules t) => t.ExtraModules.Contains(m.Name)) ?? target);
		List<HashSet<string>> moduleNamesByCombination = combinations.Select(((TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context) c) => c.Modules.Select((ModuleRules m) => m.Name).ToHashSet(StringComparer.Ordinal)).ToList();
		Dictionary<string, IReadOnlyList<(string Configuration, string Platform)>> configPlatformsByModule = ((IEnumerable<ModuleRules>)allModules).ToDictionary((Func<ModuleRules, string>)((ModuleRules m) => m.Name), (Func<ModuleRules, IReadOnlyList<(string, string)>>)((ModuleRules m) =>
		{
			TargetRules owner = moduleOwningTarget[m.Name];
			bool isLaunchModule = IsMainModule(m, owner, allModules);
			return (from c in combinations.Where(((TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context) c, int i) => moduleNamesByCombination[i].Contains(m.Name) && (!isLaunchModule || c.Target.Name == owner.Name))
				select (Configuration: c.Context.Configuration.ToString(), Platform: MapArchitecturePlatform(c.Context.Architecture))).Distinct().ToList();
		}));
		List<(string Configuration, string Platform)> allConfigPlatforms = configPlatformsByModule.Values.SelectMany((IReadOnlyList<(string Configuration, string Platform)> cp) => cp).Distinct().ToList();
		ConcurrentDictionary<int, (string Name, string Path, Guid Guid, bool IsCSharp)> written = new ConcurrentDictionary<int, (string, string, Guid, bool)>();
		await Parallel.ForEachAsync(allModules.Select((ModuleRules m, int i) => (Module: m, Index: i)), ct, async ((ModuleRules Module, int Index) item, CancellationToken token) =>
		{
			var (module, _) = item;
			if (!(module is ThirdPartyPackageModuleRules))
			{
				IReadOnlyList<(string Configuration, string Platform)> moduleConfigPlatforms = configPlatformsByModule[module.Name];
				TargetRules moduleTarget = moduleOwningTarget[module.Name];
				Guid projectGuid = GenerateGuid(module.Name);
				if (module.IsCSharpProject)
				{
					string projectPath = Path.Combine(projectsDir, ProjectName(module.Name), ProjectName(module.Name) + ".csproj");
					await GenerateCsprojAsync(module, projectPath, moduleTarget, allModules, token);
					written[item.Index] = (module.Name, projectPath, projectGuid, true);
				}
				else
				{
					string projectPath = Path.Combine(projectsDir, ProjectName(module.Name), ProjectName(module.Name) + ".vcxproj");
					await GenerateVcxprojAsync(module, projectPath, moduleTarget, allModules, token, moduleConfigPlatforms);
					await GenerateVcxprojFiltersAsync(module, projectPath + ".filters", token);
					await GenerateVcxprojUserAsync(module, projectPath + ".user", token, moduleConfigPlatforms);
					written[item.Index] = (module.Name, projectPath, projectGuid, false);
				}
			}
		});
		List<(string, string, Guid, bool)> projects = (from kv in written
			orderby kv.Key
			select kv.Value).ToList();
		RemoveStaleProjectDirectories(projectsDir, projects.Select(((string Name, string Path, Guid Guid, bool IsCSharp) p) => p.Path));
		await GenerateSolutionAsync(solutionPath, solutionName, target, projects, allModules, ct, allConfigPlatforms, configPlatformsByModule, moduleOwningTarget);
	}

	private static string MapArchitecturePlatform(TargetArchitecture architecture)
	{
		return architecture switch
		{
			TargetArchitecture.X64 => "x64", 
			TargetArchitecture.ARM64 => "ARM64", 
			TargetArchitecture.X86 => "Win32", 
			TargetArchitecture.ARMv7 => "ARM", 
			_ => architecture.ToString(), 
		};
	}

	private async Task GenerateSolutionAsync(string solutionPath, string solutionName, TargetRules target, List<(string Name, string Path, Guid Guid, bool IsCSharp)> projects, IReadOnlyList<ModuleRules> modules, CancellationToken ct, IReadOnlyList<(string Configuration, string Platform)>? allConfigPlatforms = null, IReadOnlyDictionary<string, IReadOnlyList<(string Configuration, string Platform)>>? perModuleConfigPlatforms = null, IReadOnlyDictionary<string, TargetRules>? moduleOwningTarget = null)
	{
		IReadOnlyList<(string, string)> effectiveAllConfigPlatforms = allConfigPlatforms ?? DefaultConfigPlatforms;
		StringBuilder sb = new StringBuilder();
		var (formatVersion, vsVersion, minVsVersion) = _version switch
		{
			VisualStudioVersion.VS2019 => ("12.00", "16", "10.0.40219.1"), 
			VisualStudioVersion.VS2022 => ("12.00", "17", "10.0.40219.1"), 
			VisualStudioVersion.VS2026 => ("12.00", "18", "10.0.40219.1"), 
			_ => ("12.00", "17", "10.0.40219.1"), 
		};
		sb.AppendLine();
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(54, 1, stringBuilder);
		handler.AppendLiteral("Microsoft Visual Studio Solution File, Format Version ");
		handler.AppendFormatted(formatVersion);
		stringBuilder2.AppendLine(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(24, 1, stringBuilder);
		handler.AppendLiteral("# Visual Studio Version ");
		handler.AppendFormatted(vsVersion);
		stringBuilder3.AppendLine(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(32, 1, stringBuilder);
		handler.AppendLiteral("VisualStudioVersion = ");
		handler.AppendFormatted(vsVersion);
		handler.AppendLiteral(".0.00000.0");
		stringBuilder4.AppendLine(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder5 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(29, 1, stringBuilder);
		handler.AppendLiteral("MinimumVisualStudioVersion = ");
		handler.AppendFormatted(minVsVersion);
		stringBuilder5.AppendLine(ref handler);
		Dictionary<string, Guid> folderGuidByPath = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> folderParentByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> projectParentFolder = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var project in projects)
		{
			IReadOnlyList<string> chain = SolutionFolderFor(project.Name, _moduleDict[project.Name]);
			if (chain.Count == 0)
			{
				continue;
			}
			string parentPath = null;
			string currentPath = "";
			foreach (string segment in chain)
			{
				currentPath = ((parentPath == null) ? segment : (parentPath + "\\" + segment));
				if (!folderGuidByPath.ContainsKey(currentPath))
				{
					folderGuidByPath[currentPath] = GenerateGuid("SolutionFolder:" + currentPath);
					folderParentByPath[currentPath] = parentPath;
				}
				parentPath = currentPath;
			}
			projectParentFolder[project.Name] = currentPath;
		}
		foreach (var project2 in projects)
		{
			string displayName = ProjectName(project2.Name);
			projectParentFolder.TryGetValue(project2.Name, out var parentPath2);
			string clashingFolder = ((parentPath2 == null) ? displayName : (parentPath2 + "\\" + displayName));
			while (folderGuidByPath.ContainsKey(clashingFolder))
			{
				projectParentFolder[project2.Name] = clashingFolder;
				clashingFolder = clashingFolder + "\\" + displayName;
			}
		}
		string key;
		Guid value;
		foreach (KeyValuePair<string, Guid> item2 in folderGuidByPath.OrderBy((KeyValuePair<string, Guid> kv) => kv.Key, StringComparer.OrdinalIgnoreCase))
		{
			item2.Deconstruct(out key, out value);
			string folderPath = key;
			Guid folderGuid = value;
			string folderName = folderPath.Substring(folderPath.LastIndexOf('\\') + 1);
			stringBuilder = sb;
			StringBuilder stringBuilder6 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(26, 4, stringBuilder);
			handler.AppendLiteral("Project(\"{");
			handler.AppendFormatted("2150E333-8FDC-42A3-9474-1A3956D46DE8");
			handler.AppendLiteral("}\") = \"");
			handler.AppendFormatted(folderName);
			handler.AppendLiteral("\", \"");
			handler.AppendFormatted(folderName);
			handler.AppendLiteral("\", \"");
			handler.AppendFormatted(FormatGuid(folderGuid));
			handler.AppendLiteral("\"");
			stringBuilder6.AppendLine(ref handler);
			sb.AppendLine("EndProject");
		}
		List<string> ruleFiles = (from t in (moduleOwningTarget?.Values ?? Array.Empty<TargetRules>()).Append(target)
			select t.RuleFilePath into p
			where p != null
			select (p)).Concat(Directory.GetFiles(_projectRoot, "*.omenproject.cs", SearchOption.TopDirectoryOnly)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string p) => p, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (ruleFiles.Count > 0)
		{
			Guid ruleFolderGuid = GenerateGuid("SolutionFolder:Build Rules");
			stringBuilder = sb;
			StringBuilder stringBuilder7 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(26, 4, stringBuilder);
			handler.AppendLiteral("Project(\"{");
			handler.AppendFormatted("2150E333-8FDC-42A3-9474-1A3956D46DE8");
			handler.AppendLiteral("}\") = \"");
			handler.AppendFormatted("Build Rules");
			handler.AppendLiteral("\", \"");
			handler.AppendFormatted("Build Rules");
			handler.AppendLiteral("\", \"");
			handler.AppendFormatted(FormatGuid(ruleFolderGuid));
			handler.AppendLiteral("\"");
			stringBuilder7.AppendLine(ref handler);
			sb.AppendLine("\tProjectSection(SolutionItems) = preProject");
			foreach (string ruleFile in ruleFiles)
			{
				string relative = Path.GetRelativePath(_projectRoot, ruleFile);
				stringBuilder = sb;
				StringBuilder stringBuilder8 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(5, 2, stringBuilder);
				handler.AppendLiteral("\t\t");
				handler.AppendFormatted(relative);
				handler.AppendLiteral(" = ");
				handler.AppendFormatted(relative);
				stringBuilder8.AppendLine(ref handler);
			}
			sb.AppendLine("\tEndProjectSection");
			sb.AppendLine("EndProject");
		}
		Dictionary<string, Guid> projectGuids = projects.ToDictionary(((string Name, string Path, Guid Guid, bool IsCSharp) p) => p.Name, ((string Name, string Path, Guid Guid, bool IsCSharp) p) => p.Guid);
		HashSet<string> launchProjects = (from p in projects
			where !p.IsCSharp
			where (moduleOwningTarget != null) ? IsMainModule(_moduleDict[p.Name], moduleOwningTarget.TryGetValue(p.Name, out TargetRules value2) ? value2 : target, modules) : IsMainModule(_moduleDict[p.Name], target, modules)
			select p.Name).ToHashSet(StringComparer.Ordinal);
		foreach (var project3 in projects)
		{
			string name = project3.Name;
			string path = project3.Path;
			Guid guid = project3.Guid;
			bool item = project3.IsCSharp;
			string relativePath = Path.GetRelativePath(_projectRoot, path);
			string projectTypeGuid = (item ? "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC" : "8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942");
			stringBuilder = sb;
			StringBuilder stringBuilder9 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(26, 4, stringBuilder);
			handler.AppendLiteral("Project(\"{");
			handler.AppendFormatted(projectTypeGuid);
			handler.AppendLiteral("}\") = \"");
			handler.AppendFormatted(ProjectName(name));
			handler.AppendLiteral("\", \"");
			handler.AppendFormatted(relativePath);
			handler.AppendLiteral("\", \"");
			handler.AppendFormatted(FormatGuid(guid));
			handler.AppendLiteral("\"");
			stringBuilder9.AppendLine(ref handler);
			ModuleRules module = _moduleDict[name];
			List<string> deps = (launchProjects.Contains(name) ? new List<string>() : module.AllDependencies.Where((string d) => projectGuids.ContainsKey(d) && !launchProjects.Contains(d)).ToList());
			if (deps.Count > 0)
			{
				sb.AppendLine("\tProjectSection(ProjectDependencies) = postProject");
				foreach (string dep in deps)
				{
					string depGuid = FormatGuid(projectGuids[dep]);
					stringBuilder = sb;
					StringBuilder stringBuilder10 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(5, 2, stringBuilder);
					handler.AppendLiteral("\t\t");
					handler.AppendFormatted(depGuid);
					handler.AppendLiteral(" = ");
					handler.AppendFormatted(depGuid);
					stringBuilder10.AppendLine(ref handler);
				}
				sb.AppendLine("\tEndProjectSection");
			}
			sb.AppendLine("EndProject");
		}
		sb.AppendLine("Global");
		sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
		foreach (var item3 in effectiveAllConfigPlatforms)
		{
			string config = item3.Item1;
			string platform = item3.Item2;
			stringBuilder = sb;
			StringBuilder stringBuilder11 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 4, stringBuilder);
			handler.AppendLiteral("\t\t");
			handler.AppendFormatted(config);
			handler.AppendLiteral("|");
			handler.AppendFormatted(platform);
			handler.AppendLiteral(" = ");
			handler.AppendFormatted(config);
			handler.AppendLiteral("|");
			handler.AppendFormatted(platform);
			stringBuilder11.AppendLine(ref handler);
		}
		sb.AppendLine("\tEndGlobalSection");
		sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
		foreach (var project4 in projects)
		{
			string name2 = project4.Name;
			Guid guid2 = project4.Guid;
			bool isCSharp = project4.IsCSharp;
			TargetRules effectiveTarget = ((moduleOwningTarget != null && moduleOwningTarget.TryGetValue(name2, out TargetRules owningTarget)) ? owningTarget : target);
			bool isBuildable = isCSharp || IsMainModule(_moduleDict[name2], effectiveTarget, modules);
			IReadOnlyList<(string, string)> projectConfigPlatforms = ((perModuleConfigPlatforms != null && perModuleConfigPlatforms.TryGetValue(name2, out IReadOnlyList<(string, string)> moduleConfigPlatforms)) ? moduleConfigPlatforms : effectiveAllConfigPlatforms);
			HashSet<(string, string)> supported = projectConfigPlatforms.ToHashSet();
			foreach (var item4 in effectiveAllConfigPlatforms)
			{
				string config2 = item4.Item1;
				string platform2 = item4.Item2;
				bool isSupported = supported.Contains((config2, platform2));
				string mappedConfig;
				string mappedPlatform;
				if (!isSupported)
				{
					(mappedConfig, mappedPlatform) = projectConfigPlatforms.FirstOrDefault(((string Configuration, string Platform) cp) => cp.Platform == platform2, projectConfigPlatforms[0]);
				}
				else
				{
					key = platform2;
					mappedPlatform = key;
					mappedConfig = config2;
				}
				string projectConfig = (isCSharp ? MapToCSharpConfig(mappedConfig) : mappedConfig);
				string projectPlatform = (isCSharp ? "Any CPU" : mappedPlatform);
				stringBuilder = sb;
				StringBuilder stringBuilder12 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(18, 5, stringBuilder);
				handler.AppendLiteral("\t\t");
				handler.AppendFormatted(FormatGuid(guid2));
				handler.AppendLiteral(".");
				handler.AppendFormatted(config2);
				handler.AppendLiteral("|");
				handler.AppendFormatted(platform2);
				handler.AppendLiteral(".ActiveCfg = ");
				handler.AppendFormatted(projectConfig);
				handler.AppendLiteral("|");
				handler.AppendFormatted(projectPlatform);
				stringBuilder12.AppendLine(ref handler);
				if (isBuildable & isSupported)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder13 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(16, 5, stringBuilder);
					handler.AppendLiteral("\t\t");
					handler.AppendFormatted(FormatGuid(guid2));
					handler.AppendLiteral(".");
					handler.AppendFormatted(config2);
					handler.AppendLiteral("|");
					handler.AppendFormatted(platform2);
					handler.AppendLiteral(".Build.0 = ");
					handler.AppendFormatted(projectConfig);
					handler.AppendLiteral("|");
					handler.AppendFormatted(projectPlatform);
					stringBuilder13.AppendLine(ref handler);
				}
			}
		}
		sb.AppendLine("\tEndGlobalSection");
		sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
		sb.AppendLine("\t\tHideSolutionNode = FALSE");
		sb.AppendLine("\tEndGlobalSection");
		sb.AppendLine("\tGlobalSection(NestedProjects) = preSolution");
		foreach (var (name3, _, guid3, _) in projects)
		{
			if (projectParentFolder.TryGetValue(name3, out var folderPath2))
			{
				stringBuilder = sb;
				StringBuilder stringBuilder14 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(5, 2, stringBuilder);
				handler.AppendLiteral("\t\t");
				handler.AppendFormatted(FormatGuid(guid3));
				handler.AppendLiteral(" = ");
				handler.AppendFormatted(FormatGuid(folderGuidByPath[folderPath2]));
				stringBuilder14.AppendLine(ref handler);
			}
		}
		foreach (KeyValuePair<string, Guid> item5 in folderGuidByPath.OrderBy((KeyValuePair<string, Guid> kv) => kv.Key, StringComparer.OrdinalIgnoreCase))
		{
			item5.Deconstruct(out key, out value);
			string folderPath3 = key;
			Guid folderGuid2 = value;
			string parentPath3 = folderParentByPath[folderPath3];
			if (parentPath3 != null)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder15 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(5, 2, stringBuilder);
				handler.AppendLiteral("\t\t");
				handler.AppendFormatted(FormatGuid(folderGuid2));
				handler.AppendLiteral(" = ");
				handler.AppendFormatted(FormatGuid(folderGuidByPath[parentPath3]));
				stringBuilder15.AppendLine(ref handler);
			}
		}
		sb.AppendLine("\tEndGlobalSection");
		sb.AppendLine("\tGlobalSection(ExtensibilityGlobals) = postSolution");
		stringBuilder = sb;
		StringBuilder stringBuilder16 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder);
		handler.AppendLiteral("\t\tSolutionGuid = ");
		handler.AppendFormatted(FormatGuid(GenerateGuid(solutionName)));
		stringBuilder16.AppendLine(ref handler);
		sb.AppendLine("\tEndGlobalSection");
		sb.AppendLine("EndGlobal");
		await GeneratedFile.WriteAllTextAsync(solutionPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
	}

	private static string MapToCSharpConfig(string config)
	{
		return config switch
		{
			"Debug" => "Debug", 
			"Development" => "Release", 
			"Shipping" => "Release", 
			_ => "Debug", 
		};
	}

	private async Task GenerateCsprojAsync(ModuleRules module, string projectPath, TargetRules target, IReadOnlyList<ModuleRules> allModules, CancellationToken ct)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(projectPath));
		string sourceDir = Path.Combine(_projectRoot, module.SourceDirectory ?? ("Source/" + module.Name));
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
		sb.AppendLine();
		sb.AppendLine("  <PropertyGroup>");
		string outputType = module.Type switch
		{
			ModuleType.Runtime => module.UseWPF ? "WinExe" : "Exe", 
			ModuleType.Editor => module.UseWPF ? "WinExe" : "Exe", 
			ModuleType.ThirdParty => "Library", 
			_ => "Library", 
		};
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(29, 1, stringBuilder);
		handler.AppendLiteral("    <OutputType>");
		handler.AppendFormatted(outputType);
		handler.AppendLiteral("</OutputType>");
		stringBuilder2.AppendLine(ref handler);
		string targetFramework = module.TargetFramework switch
		{
			DotNetFramework.Net60 => "net6.0", 
			DotNetFramework.Net70 => "net7.0", 
			DotNetFramework.Net80 => "net8.0", 
			DotNetFramework.Net90 => "net9.0", 
			DotNetFramework.NetStandard20 => "netstandard2.0", 
			DotNetFramework.NetStandard21 => "netstandard2.1", 
			_ => "net8.0", 
		};
		if (module.UseWPF)
		{
			targetFramework += "-windows";
		}
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(39, 1, stringBuilder);
		handler.AppendLiteral("    <TargetFramework>");
		handler.AppendFormatted(targetFramework);
		handler.AppendLiteral("</TargetFramework>");
		stringBuilder3.AppendLine(ref handler);
		if (module.UseWPF)
		{
			sb.AppendLine("    <UseWPF>true</UseWPF>");
		}
		string langVersion = module.CSharpVersion switch
		{
			CSharpVersion.CSharp10 => "10.0", 
			CSharpVersion.CSharp11 => "11.0", 
			CSharpVersion.CSharp12 => "12.0", 
			CSharpVersion.CSharp13 => "13.0", 
			CSharpVersion.Latest => "latest", 
			_ => "latest", 
		};
		stringBuilder = sb;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder);
		handler.AppendLiteral("    <LangVersion>");
		handler.AppendFormatted(langVersion);
		handler.AppendLiteral("</LangVersion>");
		stringBuilder4.AppendLine(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder5 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(25, 1, stringBuilder);
		handler.AppendLiteral("    <Nullable>");
		handler.AppendFormatted(module.EnableNullable ? "enable" : "disable");
		handler.AppendLiteral("</Nullable>");
		stringBuilder5.AppendLine(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder6 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(37, 1, stringBuilder);
		handler.AppendLiteral("    <ImplicitUsings>");
		handler.AppendFormatted(module.ImplicitUsings ? "enable" : "disable");
		handler.AppendLiteral("</ImplicitUsings>");
		stringBuilder6.AppendLine(ref handler);
		if (module.TreatWarningsAsErrors)
		{
			sb.AppendLine("    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>");
		}
		stringBuilder = sb;
		StringBuilder stringBuilder7 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(35, 1, stringBuilder);
		handler.AppendLiteral("    <RootNamespace>");
		handler.AppendFormatted(module.Name);
		handler.AppendLiteral("</RootNamespace>");
		stringBuilder7.AppendLine(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder8 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(33, 1, stringBuilder);
		handler.AppendLiteral("    <AssemblyName>");
		handler.AppendFormatted(module.Name);
		handler.AppendLiteral("</AssemblyName>");
		stringBuilder8.AppendLine(ref handler);
		sb.AppendLine("    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
		sb.AppendLine("    <OutputPath>$(SolutionDir)Binaries\\$(Configuration)\\</OutputPath>");
		sb.AppendLine("  </PropertyGroup>");
		sb.AppendLine();
		sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)'=='Debug'\">");
		sb.AppendLine("    <DefineConstants>DEBUG;TRACE;OMEN_CONFIG_DEBUG</DefineConstants>");
		sb.AppendLine("    <Optimize>false</Optimize>");
		sb.AppendLine("    <DebugType>full</DebugType>");
		sb.AppendLine("  </PropertyGroup>");
		sb.AppendLine();
		sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)'=='Release'\">");
		sb.AppendLine("    <DefineConstants>TRACE;OMEN_CONFIG_SHIPPING</DefineConstants>");
		sb.AppendLine("    <Optimize>true</Optimize>");
		sb.AppendLine("    <DebugType>pdbonly</DebugType>");
		sb.AppendLine("  </PropertyGroup>");
		sb.AppendLine();
		sb.AppendLine("  <ItemGroup>");
		foreach (string file in module.ResolveSourceFiles(_projectRoot, ".cs"))
		{
			AppendLinkedItem(sb, "Compile", file, sourceDir, projectPath);
		}
		sb.AppendLine("  </ItemGroup>");
		sb.AppendLine();
		if (module.UseWPF)
		{
			IReadOnlyList<string> xamlFiles = module.ResolveSourceFiles(_projectRoot, ".xaml");
			if (xamlFiles.Count > 0)
			{
				sb.AppendLine("  <ItemGroup>");
				foreach (string file2 in xamlFiles)
				{
					string itemType = (Path.GetFileName(file2).Equals("App.xaml", StringComparison.OrdinalIgnoreCase) ? "ApplicationDefinition" : "Page");
					AppendLinkedItem(sb, itemType, file2, sourceDir, projectPath, "      <Generator>MSBuild:Compile</Generator>\r\n      <SubType>Designer</SubType>");
				}
				sb.AppendLine("  </ItemGroup>");
				sb.AppendLine();
			}
		}
		if (module.PackageReferences.Count > 0)
		{
			sb.AppendLine("  <ItemGroup>");
			foreach (string packageReference in module.PackageReferences)
			{
				string[] parts = packageReference.Split('/');
				string packageName = parts[0];
				string version = ((parts.Length > 1) ? parts[1] : "*");
				stringBuilder = sb;
				StringBuilder stringBuilder9 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(46, 2, stringBuilder);
				handler.AppendLiteral("    <PackageReference Include=\"");
				handler.AppendFormatted(packageName);
				handler.AppendLiteral("\" Version=\"");
				handler.AppendFormatted(version);
				handler.AppendLiteral("\" />");
				stringBuilder9.AppendLine(ref handler);
			}
			sb.AppendLine("  </ItemGroup>");
			sb.AppendLine();
		}
		List<string> deps = module.AllDependencies.Where((string d) => _moduleDict.ContainsKey(d) && _moduleDict[d].IsCSharpProject).ToList();
		if (deps.Count > 0)
		{
			sb.AppendLine("  <ItemGroup>");
			foreach (string dep in deps)
			{
				string depPath = Path.Combine(_projectRoot, "Intermediate", "ProjectFiles", ProjectName(dep), ProjectName(dep) + ".csproj");
				string relDepPath = Path.GetRelativePath(Path.GetDirectoryName(projectPath), depPath);
				stringBuilder = sb;
				StringBuilder stringBuilder10 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(35, 1, stringBuilder);
				handler.AppendLiteral("    <ProjectReference Include=\"");
				handler.AppendFormatted(relDepPath);
				handler.AppendLiteral("\" />");
				stringBuilder10.AppendLine(ref handler);
			}
			sb.AppendLine("  </ItemGroup>");
			sb.AppendLine();
		}
		if (module.AssemblyReferences.Count > 0)
		{
			sb.AppendLine("  <ItemGroup>");
			foreach (string assembly in module.AssemblyReferences)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder11 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder);
				handler.AppendLiteral("    <Reference Include=\"");
				handler.AppendFormatted(assembly);
				handler.AppendLiteral("\" />");
				stringBuilder11.AppendLine(ref handler);
			}
			sb.AppendLine("  </ItemGroup>");
			sb.AppendLine();
		}
		sb.AppendLine("</Project>");
		await GeneratedFile.WriteAllTextAsync(projectPath, sb.ToString(), ct);
	}

	/// <summary>
	/// Writes one C# item (Compile/Page/ApplicationDefinition), with a &lt;Link&gt; mirroring
	/// the file's folder under <paramref name="sourceDir" /> when it isn't sitting directly in
	/// that folder's root - the .csproj equivalent of .vcxproj.filters' per-file &lt;Filter&gt;.
	/// </summary>
	private static void AppendLinkedItem(StringBuilder sb, string itemType, string file, string sourceDir, string projectPath, string? extraLines = null)
	{
		string relativeFile = Path.GetRelativePath(Path.GetDirectoryName(projectPath), file);
		string linkDir = Path.GetDirectoryName(Path.GetRelativePath(sourceDir, file))?.Replace('/', '\\');
		StringBuilder stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler;
		if (string.IsNullOrEmpty(linkDir) && extraLines == null)
		{
			stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(19, 2, stringBuilder);
			handler.AppendLiteral("    <");
			handler.AppendFormatted(itemType);
			handler.AppendLiteral(" Include=\"");
			handler.AppendFormatted(relativeFile);
			handler.AppendLiteral("\" />");
			stringBuilder2.AppendLine(ref handler);
			return;
		}
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(17, 2, stringBuilder);
		handler.AppendLiteral("    <");
		handler.AppendFormatted(itemType);
		handler.AppendLiteral(" Include=\"");
		handler.AppendFormatted(relativeFile);
		handler.AppendLiteral("\">");
		stringBuilder3.AppendLine(ref handler);
		if (!string.IsNullOrEmpty(linkDir))
		{
			stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(20, 2, stringBuilder);
			handler.AppendLiteral("      <Link>");
			handler.AppendFormatted(linkDir);
			handler.AppendLiteral("\\");
			handler.AppendFormatted(Path.GetFileName(file));
			handler.AppendLiteral("</Link>");
			stringBuilder4.AppendLine(ref handler);
		}
		if (extraLines != null)
		{
			sb.AppendLine(extraLines);
		}
		stringBuilder = sb;
		StringBuilder stringBuilder5 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
		handler.AppendLiteral("    </");
		handler.AppendFormatted(itemType);
		handler.AppendLiteral(">");
		stringBuilder5.AppendLine(ref handler);
	}

	private async Task GenerateVcxprojAsync(ModuleRules module, string projectPath, TargetRules target, IReadOnlyList<ModuleRules> allModules, CancellationToken ct, IReadOnlyList<(string Configuration, string Platform)>? configPlatforms = null)
	{
		IReadOnlyList<(string, string)> effectiveConfigPlatforms = configPlatforms ?? DefaultConfigPlatforms;
		Directory.CreateDirectory(Path.GetDirectoryName(projectPath));
		Guid projectGuid = GenerateGuid(module.Name);
		List<string> sourceFiles = BrowsableFiles(module, ".cpp", ".cc", ".cxx", ".c").ToList();
		List<string> headerFiles = BrowsableFiles(module, HeaderExtensions).ToList();
		List<string> uiFiles = new List<string>();
		List<string> resourceFiles = new List<string>();
		if (module.IsQtProject)
		{
			uiFiles.AddRange(BrowsableFiles(module, ".ui"));
			resourceFiles.AddRange(BrowsableFiles(module, ".qrc"));
		}
		string toolsVersion = _version switch
		{
			VisualStudioVersion.VS2019 => "16.0", 
			VisualStudioVersion.VS2022 => "17.0", 
			VisualStudioVersion.VS2026 => "18.0", 
			_ => "17.0", 
		};
		string platformToolset = _version switch
		{
			VisualStudioVersion.VS2019 => "v142", 
			VisualStudioVersion.VS2022 => "v143", 
			VisualStudioVersion.VS2026 => "v145", 
			_ => "v143", 
		};
		bool isMainModule = IsMainModule(module, target, allModules);
		string text = ((!isMainModule) ? "StaticLibrary" : (target.Type switch
		{
			TargetType.Executable => "Application", 
			TargetType.SharedLibrary => "DynamicLibrary", 
			TargetType.StaticLibrary => "StaticLibrary", 
			_ => "Application", 
		}));
		string configType = text;
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(108, 1, stringBuilder);
		handler.AppendLiteral("<Project DefaultTargets=\"Build\" ToolsVersion=\"");
		handler.AppendFormatted(toolsVersion);
		handler.AppendLiteral("\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
		stringBuilder2.AppendLine(ref handler);
		sb.AppendLine("  <ItemGroup Label=\"ProjectConfigurations\">");
		foreach (var item in effectiveConfigPlatforms)
		{
			string config = item.Item1;
			string platform = item.Item2;
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(38, 2, stringBuilder);
			handler.AppendLiteral("    <ProjectConfiguration Include=\"");
			handler.AppendFormatted(config);
			handler.AppendLiteral("|");
			handler.AppendFormatted(platform);
			handler.AppendLiteral("\">");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(37, 1, stringBuilder);
			handler.AppendLiteral("      <Configuration>");
			handler.AppendFormatted(config);
			handler.AppendLiteral("</Configuration>");
			stringBuilder4.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder5 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(27, 1, stringBuilder);
			handler.AppendLiteral("      <Platform>");
			handler.AppendFormatted(platform);
			handler.AppendLiteral("</Platform>");
			stringBuilder5.AppendLine(ref handler);
			sb.AppendLine("    </ProjectConfiguration>");
		}
		sb.AppendLine("  </ItemGroup>");
		sb.AppendLine("  <PropertyGroup Label=\"Globals\">");
		stringBuilder = sb;
		StringBuilder stringBuilder6 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(41, 1, stringBuilder);
		handler.AppendLiteral("    <VCProjectVersion>");
		handler.AppendFormatted(toolsVersion);
		handler.AppendLiteral("</VCProjectVersion>");
		stringBuilder6.AppendLine(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder7 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder);
		handler.AppendLiteral("    <ProjectGuid>");
		handler.AppendFormatted(FormatGuid(projectGuid));
		handler.AppendLiteral("</ProjectGuid>");
		stringBuilder7.AppendLine(ref handler);
		if (!module.IsQtProject)
		{
			sb.AppendLine("    <Keyword>Win32Proj</Keyword>");
		}
		stringBuilder = sb;
		StringBuilder stringBuilder8 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(35, 1, stringBuilder);
		handler.AppendLiteral("    <RootNamespace>");
		handler.AppendFormatted(ProjectName(module.Name));
		handler.AppendLiteral("</RootNamespace>");
		stringBuilder8.AppendLine(ref handler);
		sb.AppendLine("    <WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion>");
		if (module.IsQtProject)
		{
			sb.AppendLine("    <Keyword>QtVS_v304</Keyword>");
			sb.AppendLine("    <QtMsBuild Condition=\"'$(QtMsBuild)'==''\">$(MSBuildProjectDirectory)\\QtMsBuild</QtMsBuild>");
		}
		sb.AppendLine("  </PropertyGroup>");
		sb.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.Default.props\" />");
		foreach (var item2 in effectiveConfigPlatforms)
		{
			string config2 = item2.Item1;
			string platform2 = item2.Item2;
			string useDebugLibs = ((config2 == "Debug") ? "true" : "false");
			stringBuilder = sb;
			StringBuilder stringBuilder9 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(87, 2, stringBuilder);
			handler.AppendLiteral("  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='");
			handler.AppendFormatted(config2);
			handler.AppendLiteral("|");
			handler.AppendFormatted(platform2);
			handler.AppendLiteral("'\" Label=\"Configuration\">");
			stringBuilder9.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder10 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(43, 1, stringBuilder);
			handler.AppendLiteral("    <ConfigurationType>");
			handler.AppendFormatted(isMainModule ? "Makefile" : configType);
			handler.AppendLiteral("</ConfigurationType>");
			stringBuilder10.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder11 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(43, 1, stringBuilder);
			handler.AppendLiteral("    <UseDebugLibraries>");
			handler.AppendFormatted(useDebugLibs);
			handler.AppendLiteral("</UseDebugLibraries>");
			stringBuilder11.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder12 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(39, 1, stringBuilder);
			handler.AppendLiteral("    <PlatformToolset>");
			handler.AppendFormatted(platformToolset);
			handler.AppendLiteral("</PlatformToolset>");
			stringBuilder12.AppendLine(ref handler);
			sb.AppendLine("    <CharacterSet>Unicode</CharacterSet>");
			if (config2 != "Debug")
			{
				sb.AppendLine("    <WholeProgramOptimization>true</WholeProgramOptimization>");
			}
			sb.AppendLine("  </PropertyGroup>");
		}
		sb.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.props\" />");
		if (module.IsQtProject)
		{
			sb.AppendLine("  <ImportGroup Condition=\"Exists('$(QtMsBuild)\\qt_defaults.props')\">");
			sb.AppendLine("    <Import Project=\"$(QtMsBuild)\\qt_defaults.props\" />");
			sb.AppendLine("  </ImportGroup>");
		}
		sb.AppendLine("  <ImportGroup Label=\"ExtensionSettings\">");
		sb.AppendLine("  </ImportGroup>");
		foreach (var item3 in effectiveConfigPlatforms)
		{
			string config3 = item3.Item1;
			string platform3 = item3.Item2;
			stringBuilder = sb;
			StringBuilder stringBuilder13 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(86, 2, stringBuilder);
			handler.AppendLiteral("  <ImportGroup Label=\"PropertySheets\" Condition=\"'$(Configuration)|$(Platform)'=='");
			handler.AppendFormatted(config3);
			handler.AppendLiteral("|");
			handler.AppendFormatted(platform3);
			handler.AppendLiteral("'\">");
			stringBuilder13.AppendLine(ref handler);
			sb.AppendLine("    <Import Project=\"$(UserRootDir)\\Microsoft.Cpp.$(Platform).user.props\" Condition=\"exists('$(UserRootDir)\\Microsoft.Cpp.$(Platform).user.props')\" Label=\"LocalAppDataPlatform\" />");
			sb.AppendLine("  </ImportGroup>");
		}
		sb.AppendLine("  <PropertyGroup Label=\"UserMacros\" />");
		if (module.IsQtProject)
		{
			foreach (var item4 in effectiveConfigPlatforms)
			{
				string config4 = item4.Item1;
				string platform4 = item4.Item2;
				stringBuilder = sb;
				StringBuilder stringBuilder14 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(65, 2, stringBuilder);
				handler.AppendLiteral("  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='");
				handler.AppendFormatted(config4);
				handler.AppendLiteral("|");
				handler.AppendFormatted(platform4);
				handler.AppendLiteral("'\">");
				stringBuilder14.AppendLine(ref handler);
				stringBuilder = sb;
				StringBuilder stringBuilder15 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(27, 1, stringBuilder);
				handler.AppendLiteral("    <QtInstall>");
				handler.AppendFormatted(GetQtInstallName(module));
				handler.AppendLiteral("</QtInstall>");
				stringBuilder15.AppendLine(ref handler);
				stringBuilder = sb;
				StringBuilder stringBuilder16 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(27, 1, stringBuilder);
				handler.AppendLiteral("    <QtModules>");
				handler.AppendFormatted(string.Join(";", module.QtModules.Select((string m) => "qt" + m.ToLowerInvariant())));
				handler.AppendLiteral("</QtModules>");
				stringBuilder16.AppendLine(ref handler);
				if (module.EnableMoc)
				{
					sb.AppendLine("    <QtMocEnabled>true</QtMocEnabled>");
				}
				if (module.EnableUic)
				{
					sb.AppendLine("    <QtUicEnabled>true</QtUicEnabled>");
				}
				if (module.EnableRcc)
				{
					sb.AppendLine("    <QtRccEnabled>true</QtRccEnabled>");
				}
				sb.AppendLine("  </PropertyGroup>");
			}
		}
		CompileEnvironment compileEnvironment = ResolveFor(module, target, AnyConfiguration());
		List<string> allIncludePaths = new List<string>(compileEnvironment.IncludePaths);
		List<string> externalIncludePaths = new List<string>(compileEnvironment.ExternalIncludePaths);
		foreach (var item5 in effectiveConfigPlatforms)
		{
			string config5 = item5.Item1;
			string platform5 = item5.Item2;
			stringBuilder = sb;
			StringBuilder stringBuilder17 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(65, 2, stringBuilder);
			handler.AppendLiteral("  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='");
			handler.AppendFormatted(config5);
			handler.AppendLiteral("|");
			handler.AppendFormatted(platform5);
			handler.AppendLiteral("'\">");
			stringBuilder17.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder18 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(53, 1, stringBuilder);
			handler.AppendLiteral("    <OutDir>$(SolutionDir)Binaries\\Windows_");
			handler.AppendFormatted(config5);
			handler.AppendLiteral("\\</OutDir>");
			stringBuilder18.AppendLine(ref handler);
			sb.AppendLine("    <IntDir>$(SolutionDir)Intermediate\\$(ProjectName)\\$(Configuration)\\</IntDir>");
			stringBuilder = sb;
			StringBuilder stringBuilder19 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(29, 1, stringBuilder);
			handler.AppendLiteral("    <TargetName>");
			handler.AppendFormatted(ModulePathName.BinaryName(module.Name));
			handler.AppendLiteral("</TargetName>");
			stringBuilder19.AppendLine(ref handler);
			if (externalIncludePaths.Count > 0)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder20 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(70, 1, stringBuilder);
				handler.AppendLiteral("    <ExternalIncludePath>");
				handler.AppendFormatted(string.Join(";", externalIncludePaths));
				handler.AppendLiteral(";$(ExternalIncludePath)</ExternalIncludePath>");
				stringBuilder20.AppendLine(ref handler);
			}
			sb.AppendLine("  </PropertyGroup>");
		}
		string targetOutputExtension = target.Type switch
		{
			TargetType.Executable => ".exe", 
			TargetType.SharedLibrary => ".dll", 
			TargetType.StaticLibrary => ".lib", 
			_ => ".exe", 
		};
		foreach (var item6 in effectiveConfigPlatforms)
		{
			string config6 = item6.Item1;
			string platform6 = item6.Item2;
			string optimization;
			if (config6 == "Shipping")
			{
				optimization = "MaxSpeed";
			}
			else
			{
				optimization = ((config6 == "Development") ? "MinSpace" : "Disabled");
			}
			string runtimeLib = ((config6 == "Debug") ? "MultiThreadedDebugDLL" : "MultiThreadedDLL");
			string debugInfo = ((config6 == "Debug" || config6 == "Development") ? "true" : "false");
			List<string> definitions = DefinitionsForConfig(module, target, config6);
			definitions.Add("OMEN_CONFIG_" + config6.ToUpperInvariant() + "=1");
			List<string> includePaths = new List<string>(allIncludePaths);
			(List<string> ForcedIncludes, List<string> AdditionalOptions) tuple = IntelliSenseOptions(module, config6);
			List<string> forcedIncludes = tuple.ForcedIncludes;
			List<string> additionalOptions = tuple.AdditionalOptions;
			List<string> projectDefinitions = definitions.Select(ProjectDefinition).ToList();
			if (isMainModule)
			{
				string nmakeOutput = $"$(SolutionDir)Binaries\\Windows_{config6}\\{ModulePathName.BinaryName(module.Name)}{targetOutputExtension}";
				stringBuilder = sb;
				StringBuilder stringBuilder21 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(65, 2, stringBuilder);
				handler.AppendLiteral("  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='");
				handler.AppendFormatted(config6);
				handler.AppendLiteral("|");
				handler.AppendFormatted(platform6);
				handler.AppendLiteral("'\">");
				stringBuilder21.AppendLine(ref handler);
				string cdToSolutionDir = "cd /d \"$(SolutionDir)\" &amp;&amp; ";
				stringBuilder = sb;
				StringBuilder stringBuilder22 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(87, 4, stringBuilder);
				handler.AppendLiteral("    <NMakeBuildCommandLine>");
				handler.AppendFormatted(cdToSolutionDir);
				handler.AppendLiteral("omen build ");
				handler.AppendFormatted(target.Name);
				handler.AppendLiteral(" --configuration ");
				handler.AppendFormatted(config6);
				handler.AppendLiteral(" --arch ");
				handler.AppendFormatted(platform6);
				handler.AppendLiteral("</NMakeBuildCommandLine>");
				stringBuilder22.AppendLine(ref handler);
				stringBuilder = sb;
				StringBuilder stringBuilder23 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(85, 3, stringBuilder);
				handler.AppendLiteral("    <NMakeReBuildCommandLine>");
				handler.AppendFormatted(cdToSolutionDir);
				handler.AppendLiteral("omen rebuild ");
				handler.AppendFormatted(target.Name);
				handler.AppendLiteral(" --configuration ");
				handler.AppendFormatted(config6);
				handler.AppendLiteral("</NMakeReBuildCommandLine>");
				stringBuilder23.AppendLine(ref handler);
				stringBuilder = sb;
				StringBuilder stringBuilder24 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(79, 3, stringBuilder);
				handler.AppendLiteral("    <NMakeCleanCommandLine>");
				handler.AppendFormatted(cdToSolutionDir);
				handler.AppendLiteral("omen clean ");
				handler.AppendFormatted(target.Name);
				handler.AppendLiteral(" --configuration ");
				handler.AppendFormatted(config6);
				handler.AppendLiteral("</NMakeCleanCommandLine>");
				stringBuilder24.AppendLine(ref handler);
				stringBuilder = sb;
				StringBuilder stringBuilder25 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder);
				handler.AppendLiteral("    <NMakeOutput>");
				handler.AppendFormatted(nmakeOutput);
				handler.AppendLiteral("</NMakeOutput>");
				stringBuilder25.AppendLine(ref handler);
				stringBuilder = sb;
				StringBuilder stringBuilder26 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(65, 1, stringBuilder);
				handler.AppendLiteral("    <NMakePreprocessorDefinitions>");
				handler.AppendFormatted(string.Join(";", projectDefinitions));
				handler.AppendLiteral("</NMakePreprocessorDefinitions>");
				stringBuilder26.AppendLine(ref handler);
				stringBuilder = sb;
				StringBuilder stringBuilder27 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(53, 1, stringBuilder);
				handler.AppendLiteral("    <NMakeIncludeSearchPath>");
				handler.AppendFormatted(string.Join(";", includePaths.Concat(externalIncludePaths)));
				handler.AppendLiteral("</NMakeIncludeSearchPath>");
				stringBuilder27.AppendLine(ref handler);
				if (forcedIncludes.Count > 0)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder28 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(47, 1, stringBuilder);
					handler.AppendLiteral("    <NMakeForcedIncludes>");
					handler.AppendFormatted(XmlEscape(string.Join(";", forcedIncludes)));
					handler.AppendLiteral("</NMakeForcedIncludes>");
					stringBuilder28.AppendLine(ref handler);
				}
				stringBuilder = sb;
				StringBuilder stringBuilder29 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(43, 1, stringBuilder);
				handler.AppendLiteral("    <AdditionalOptions>");
				handler.AppendFormatted(XmlEscape(string.Join(" ", additionalOptions.Prepend(StandardFlag(module.CppStandard)))));
				handler.AppendLiteral("</AdditionalOptions>");
				stringBuilder29.AppendLine(ref handler);
				sb.AppendLine("  </PropertyGroup>");
				continue;
			}
			projectDefinitions.Add("%(PreprocessorDefinitions)");
			definitions = projectDefinitions;
			includePaths.Add("%(AdditionalIncludeDirectories)");
			stringBuilder = sb;
			StringBuilder stringBuilder30 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(71, 2, stringBuilder);
			handler.AppendLiteral("  <ItemDefinitionGroup Condition=\"'$(Configuration)|$(Platform)'=='");
			handler.AppendFormatted(config6);
			handler.AppendLiteral("|");
			handler.AppendFormatted(platform6);
			handler.AppendLiteral("'\">");
			stringBuilder30.AppendLine(ref handler);
			sb.AppendLine("    <ClCompile>");
			stringBuilder = sb;
			StringBuilder stringBuilder31 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(35, 1, stringBuilder);
			handler.AppendLiteral("      <Optimization>");
			handler.AppendFormatted(optimization);
			handler.AppendLiteral("</Optimization>");
			stringBuilder31.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder32 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(67, 1, stringBuilder);
			handler.AppendLiteral("      <AdditionalIncludeDirectories>");
			handler.AppendFormatted(string.Join(";", includePaths));
			handler.AppendLiteral("</AdditionalIncludeDirectories>");
			stringBuilder32.AppendLine(ref handler);
			if (externalIncludePaths.Count > 0)
			{
				sb.AppendLine("      <ExternalWarningLevel>TurnOffAllWarnings</ExternalWarningLevel>");
			}
			stringBuilder = sb;
			StringBuilder stringBuilder33 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(57, 1, stringBuilder);
			handler.AppendLiteral("      <PreprocessorDefinitions>");
			handler.AppendFormatted(string.Join(";", definitions));
			handler.AppendLiteral("</PreprocessorDefinitions>");
			stringBuilder33.AppendLine(ref handler);
			sb.AppendLine("      <ConformanceMode>true</ConformanceMode>");
			string cppStd = module.CppStandard switch
			{
				CppStandard.Cpp14 => "stdcpp14", 
				CppStandard.Cpp17 => "stdcpp17", 
				CppStandard.Cpp20 => "stdcpp20", 
				CppStandard.Cpp23 => "stdcpplatest", 
				CppStandard.Latest => "stdcpplatest", 
				_ => "stdcpp20", 
			};
			stringBuilder = sb;
			StringBuilder stringBuilder34 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(43, 1, stringBuilder);
			handler.AppendLiteral("      <LanguageStandard>");
			handler.AppendFormatted(cppStd);
			handler.AppendLiteral("</LanguageStandard>");
			stringBuilder34.AppendLine(ref handler);
			if (forcedIncludes.Count > 0)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder35 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(69, 1, stringBuilder);
				handler.AppendLiteral("      <ForcedIncludeFiles>");
				handler.AppendFormatted(XmlEscape(string.Join(";", forcedIncludes)));
				handler.AppendLiteral(";%(ForcedIncludeFiles)</ForcedIncludeFiles>");
				stringBuilder35.AppendLine(ref handler);
			}
			if (additionalOptions.Any((string o) => (o == "/Zc:preprocessor" || o == "-Zc:preprocessor") ? true : false))
			{
				sb.AppendLine("      <UseStandardPreprocessor>true</UseStandardPreprocessor>");
			}
			if (additionalOptions.Count > 0)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder36 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(66, 1, stringBuilder);
				handler.AppendLiteral("      <AdditionalOptions>");
				handler.AppendFormatted(XmlEscape(string.Join(" ", additionalOptions)));
				handler.AppendLiteral(" %(AdditionalOptions)</AdditionalOptions>");
				stringBuilder36.AppendLine(ref handler);
			}
			stringBuilder = sb;
			StringBuilder stringBuilder37 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(39, 1, stringBuilder);
			handler.AppendLiteral("      <RuntimeLibrary>");
			handler.AppendFormatted(runtimeLib);
			handler.AppendLiteral("</RuntimeLibrary>");
			stringBuilder37.AppendLine(ref handler);
			string warningLevel = module.WarningLevel switch
			{
				WarningLevel.Off => "TurnOffAllWarnings", 
				WarningLevel.Level1 => "Level1", 
				WarningLevel.Level2 => "Level2", 
				WarningLevel.Level3 => "Level3", 
				WarningLevel.Level4 => "Level4", 
				WarningLevel.EnableAll => "EnableAllWarnings", 
				_ => "Level4", 
			};
			stringBuilder = sb;
			StringBuilder stringBuilder38 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(35, 1, stringBuilder);
			handler.AppendLiteral("      <WarningLevel>");
			handler.AppendFormatted(warningLevel);
			handler.AppendLiteral("</WarningLevel>");
			stringBuilder38.AppendLine(ref handler);
			if (module.TreatWarningsAsErrors)
			{
				sb.AppendLine("      <TreatWarningAsError>true</TreatWarningAsError>");
			}
			stringBuilder = sb;
			StringBuilder stringBuilder39 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(45, 1, stringBuilder);
			handler.AppendLiteral("      <ExceptionHandling>");
			handler.AppendFormatted(module.EnableExceptions ? "Sync" : "false");
			handler.AppendLiteral("</ExceptionHandling>");
			stringBuilder39.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder40 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(41, 1, stringBuilder);
			handler.AppendLiteral("      <RuntimeTypeInfo>");
			handler.AppendFormatted(module.EnableRTTI ? "true" : "false");
			handler.AppendLiteral("</RuntimeTypeInfo>");
			stringBuilder40.AppendLine(ref handler);
			sb.AppendLine("      <MultiProcessorCompilation>true</MultiProcessorCompilation>");
			if (config6 == "Debug")
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
			stringBuilder = sb;
			StringBuilder stringBuilder41 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(59, 1, stringBuilder);
			handler.AppendLiteral("      <GenerateDebugInformation>");
			handler.AppendFormatted(debugInfo);
			handler.AppendLiteral("</GenerateDebugInformation>");
			stringBuilder41.AppendLine(ref handler);
			if (config6 == "Shipping")
			{
				sb.AppendLine("      <EnableCOMDATFolding>true</EnableCOMDATFolding>");
				sb.AppendLine("      <OptimizeReferences>true</OptimizeReferences>");
				sb.AppendLine("      <LinkTimeCodeGeneration>UseLinkTimeCodeGeneration</LinkTimeCodeGeneration>");
			}
			List<string> libs = CollectSystemLibraries(module);
			if (module.IsQtProject)
			{
				libs.AddRange(GetQtLibraries(module, config6));
			}
			libs.Add("%(AdditionalDependencies)");
			stringBuilder = sb;
			StringBuilder stringBuilder42 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(55, 1, stringBuilder);
			handler.AppendLiteral("      <AdditionalDependencies>");
			handler.AppendFormatted(string.Join(";", libs));
			handler.AppendLiteral("</AdditionalDependencies>");
			stringBuilder42.AppendLine(ref handler);
			if (module.IsQtProject)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder43 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(99, 1, stringBuilder);
				handler.AppendLiteral("      <AdditionalLibraryDirectories>");
				handler.AppendFormatted(GetQtLibPath(module));
				handler.AppendLiteral(";%(AdditionalLibraryDirectories)</AdditionalLibraryDirectories>");
				stringBuilder43.AppendLine(ref handler);
			}
			sb.AppendLine("    </Link>");
			if (configType == "StaticLibrary")
			{
				sb.AppendLine("    <Lib>");
				sb.AppendLine("    </Lib>");
			}
			sb.AppendLine("  </ItemDefinitionGroup>");
		}
		sb.AppendLine("  <ItemGroup>");
		foreach (string source in sourceFiles)
		{
			string relativePath = Path.GetRelativePath(Path.GetDirectoryName(projectPath), source);
			stringBuilder = sb;
			StringBuilder stringBuilder44 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder);
			handler.AppendLiteral("    <ClCompile Include=\"");
			handler.AppendFormatted(relativePath);
			handler.AppendLiteral("\" />");
			stringBuilder44.AppendLine(ref handler);
		}
		sb.AppendLine("  </ItemGroup>");
		sb.AppendLine("  <ItemGroup>");
		foreach (string header in headerFiles)
		{
			string relativePath2 = Path.GetRelativePath(Path.GetDirectoryName(projectPath), header);
			stringBuilder = sb;
			StringBuilder stringBuilder45 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder);
			handler.AppendLiteral("    <ClInclude Include=\"");
			handler.AppendFormatted(relativePath2);
			handler.AppendLiteral("\" />");
			stringBuilder45.AppendLine(ref handler);
		}
		sb.AppendLine("  </ItemGroup>");
		if (module.RuleFilePath != null)
		{
			sb.AppendLine("  <ItemGroup>");
			stringBuilder = sb;
			StringBuilder stringBuilder46 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
			handler.AppendLiteral("    <None Include=\"");
			handler.AppendFormatted(Path.GetRelativePath(Path.GetDirectoryName(projectPath), module.RuleFilePath));
			handler.AppendLiteral("\" />");
			stringBuilder46.AppendLine(ref handler);
			sb.AppendLine("  </ItemGroup>");
		}
		if (uiFiles.Count > 0)
		{
			sb.AppendLine("  <ItemGroup>");
			foreach (string ui in uiFiles)
			{
				string relativePath3 = Path.GetRelativePath(Path.GetDirectoryName(projectPath), ui);
				stringBuilder = sb;
				StringBuilder stringBuilder47 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(24, 1, stringBuilder);
				handler.AppendLiteral("    <QtUic Include=\"");
				handler.AppendFormatted(relativePath3);
				handler.AppendLiteral("\" />");
				stringBuilder47.AppendLine(ref handler);
			}
			sb.AppendLine("  </ItemGroup>");
		}
		if (resourceFiles.Count > 0)
		{
			sb.AppendLine("  <ItemGroup>");
			foreach (string qrc in resourceFiles)
			{
				string relativePath4 = Path.GetRelativePath(Path.GetDirectoryName(projectPath), qrc);
				stringBuilder = sb;
				StringBuilder stringBuilder48 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(24, 1, stringBuilder);
				handler.AppendLiteral("    <QtRcc Include=\"");
				handler.AppendFormatted(relativePath4);
				handler.AppendLiteral("\" />");
				stringBuilder48.AppendLine(ref handler);
			}
			sb.AppendLine("  </ItemGroup>");
		}
		sb.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.targets\" />");
		if (module.IsQtProject)
		{
			sb.AppendLine("  <ImportGroup Condition=\"Exists('$(QtMsBuild)\\qt.targets')\">");
			sb.AppendLine("    <Import Project=\"$(QtMsBuild)\\qt.targets\" />");
			sb.AppendLine("  </ImportGroup>");
		}
		sb.AppendLine("  <ImportGroup Label=\"ExtensionTargets\">");
		sb.AppendLine("  </ImportGroup>");
		sb.AppendLine("</Project>");
		await GeneratedFile.WriteAllTextAsync(projectPath, sb.ToString(), ct);
	}

	private string GetQtInstallName(ModuleRules module)
	{
		return module.QtVersion switch
		{
			QtVersion.Qt5 => "5.15.2_msvc2019_64", 
			QtVersion.Qt6 => "6.8.3_msvc2022_64", 
			_ => "6.8.3_msvc2022_64", 
		};
	}

	private List<string> GetQtIncludePaths(ModuleRules module)
	{
		string qtPath = module.QtPath ?? GetDefaultQtPath(module.QtVersion);
		List<string> paths = new List<string> { Path.Combine(qtPath, "include") };
		foreach (string mod in module.QtModules)
		{
			paths.Add(Path.Combine(qtPath, "include", "Qt" + mod));
		}
		return paths;
	}

	private string GetQtLibPath(ModuleRules module)
	{
		return Path.Combine(module.QtPath ?? GetDefaultQtPath(module.QtVersion), "lib");
	}

	private List<string> GetQtLibraries(ModuleRules module, string config)
	{
		List<string> libs = new List<string>();
		string suffix = ((config == "Debug") ? "d" : "");
		foreach (string mod in module.QtModules)
		{
			string majorVersion = ((module.QtVersion == QtVersion.Qt5) ? "5" : "6");
			libs.Add($"Qt{majorVersion}{mod}{suffix}.lib");
		}
		return libs;
	}

	/// <summary>
	/// Where Qt lives for this module, when it does not name one itself. Delegates to
	/// QtToolchainResolver so the emitted .vcxproj points at the same Qt the build uses - it
	/// used to hardcode C:\Qt, which is not where O3DE keeps Qt.
	/// </summary>
	private string GetDefaultQtPath(QtVersion version)
	{
		return QtToolchainResolver.DefaultRootFor(version);
	}

	/// <summary>
	/// Determines whether <paramref name="module" /> is the module that actually drives
	/// <paramref name="target" />'s build (the one converted to an NMake/Makefile project,
	/// and the only one whose solution entry is marked buildable). Prefers the target's
	/// explicit <see cref="P:Omen.Core.Rules.TargetRules.LaunchModuleName" /> when set; falls back to
	/// dependency-topology (the root of the graph -- nothing depends on it -- that itself
	/// has dependencies) when unset. The topology fallback is only reliable for
	/// single-target projects: with multiple targets sharing a module dictionary, every
	/// target's root module would otherwise satisfy it independently and collide.
	/// </summary>
	private static bool IsMainModule(ModuleRules module, TargetRules target, IReadOnlyList<ModuleRules> allModules)
	{
		if (target.LaunchModuleName != null)
		{
			return module.Name == target.LaunchModuleName;
		}
		if (!allModules.Any((ModuleRules m) => m.PublicDependencies.Contains(module.Name) || m.PrivateDependencies.Contains(module.Name)))
		{
			if (module.PublicDependencies.Count <= 0)
			{
				return module.PrivateDependencies.Count > 0;
			}
			return true;
		}
		return false;
	}

	/// <summary>
	/// A configuration to resolve config-independent data with. Any registered context will do;
	/// include-path derivation reads only ProjectRoot.
	/// </summary>
	private string AnyConfiguration()
	{
		return _contextsByConfig.Keys.FirstOrDefault() ?? string.Empty;
	}

	private CompileEnvironment ResolveFor(ModuleRules module, TargetRules target, string config)
	{
		BuildContext context = (_contextsByConfig.TryGetValue(config, out BuildContext exact) ? exact : _contextsByConfig.Values.FirstOrDefault());
		if (context == null)
		{
			return new CompileEnvironment(Array.Empty<string>(), Array.Empty<string>());
		}
		return new CompileEnvironmentResolver(context, _moduleDict).Resolve(module, target);
	}

	/// <summary>
	/// The definitions for one configuration, matching what the compiler receives.
	///
	/// The single-combination GenerateAsync overload carries one context but still emits a
	/// PropertyGroup per entry in DefaultConfigPlatforms, so some configurations have no context
	/// of their own. Those borrow the one available context - correct for everything except its
	/// own OMEN_&lt;CONFIG&gt; define, which is corrected here rather than emitted wrong.
	/// </summary>
	private List<string> DefinitionsForConfig(ModuleRules module, TargetRules target, string config)
	{
		List<string> defs = new List<string>(ResolveFor(module, target, config).Definitions);
		if (!_contextsByConfig.ContainsKey(config))
		{
			BuildContext borrowed = _contextsByConfig.Values.FirstOrDefault();
			if (borrowed != null)
			{
				defs.RemoveAll((string d) => d.StartsWith("OMEN_" + borrowed.Configuration.ToString().ToUpperInvariant() + "=", StringComparison.Ordinal));
				defs.Add("OMEN_" + config.ToUpperInvariant() + "=1");
			}
		}
		return defs;
	}

	private List<string> CollectSystemLibraries(ModuleRules module)
	{
		List<string> libs = new List<string>();
		HashSet<string> visited = new HashSet<string>();
		AddModuleLibs(module);
		return libs.Distinct().ToList();
		void AddModuleLibs(ModuleRules mod)
		{
			if (visited.Contains(mod.Name))
			{
				return;
			}
			visited.Add(mod.Name);
			libs.AddRange(mod.PublicSystemLibraries);
			libs.AddRange(mod.PublicLibraries);
			foreach (string depName in mod.AllDependencies)
			{
				if (_moduleDict.TryGetValue(depName, out ModuleRules depModule))
				{
					AddModuleLibs(depModule);
				}
			}
		}
	}

	private IReadOnlyList<string> BrowsableFiles(ModuleRules module, params string[] extensions)
	{
		return _browsableFiles.GetOrAdd(module.Name + "|" + string.Join(",", extensions), (string _) => BrowsableFilesUncached(module, extensions));
	}

	private IReadOnlyList<string> BrowsableFilesUncached(ModuleRules module, params string[] extensions)
	{
		if (module != null && module.Type == ModuleType.ThirdParty)
		{
			TargetType? binaryType = module.BinaryType;
			if (binaryType.HasValue && binaryType == TargetType.HeaderOnly)
			{
				return Array.Empty<string>();
			}
		}
		string sourceDir = SourceDirectoryOf(module);
		if (module.SourceFiles.Count > 0 && _sharedSourceDirectories.Contains(sourceDir))
		{
			return (from f in module.SourceFiles
				where extensions.Any((string e) => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))
				select Path.GetFullPath(Path.Combine(sourceDir, f))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string f) => f, StringComparer.OrdinalIgnoreCase).ToList();
		}
		List<string> files = new List<string>(module.ResolveSourceFiles(_projectRoot, extensions));
		if (Directory.Exists(sourceDir))
		{
			string[] array = extensions;
			foreach (string extension in array)
			{
				files.AddRange(from f in Directory.GetFiles(sourceDir, "*" + extension, SearchOption.AllDirectories)
					where f.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
					select f);
			}
		}
		return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string f) => f, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private async Task GenerateVcxprojFiltersAsync(ModuleRules module, string filtersPath, CancellationToken ct)
	{
		string sourceDir = Path.GetFullPath(Path.Combine(_projectRoot, module.SourceDirectory ?? ("Source/" + module.Name)));
		List<string> sourceFiles = BrowsableFiles(module, ".cpp", ".cc", ".cxx", ".c").ToList();
		List<string> headerFiles = BrowsableFiles(module, HeaderExtensions).ToList();
		List<string> uiFiles = new List<string>();
		List<string> resourceFiles = new List<string>();
		if (module.IsQtProject)
		{
			uiFiles.AddRange(BrowsableFiles(module, ".ui"));
			resourceFiles.AddRange(BrowsableFiles(module, ".qrc"));
		}
		HashSet<string> filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
		sb.AppendLine("<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
		foreach (string file in sourceFiles.Concat(headerFiles).Concat(uiFiles).Concat(resourceFiles))
		{
			string filterPath = FilterPathFor(file, sourceDir);
			if (filterPath != null)
			{
				string[] array = filterPath.Split('\\');
				string current = "";
				string[] array2 = array;
				foreach (string part in array2)
				{
					current = ((current.Length == 0) ? part : (current + "\\" + part));
					filters.Add(current);
				}
			}
		}
		if (module.RuleFilePath != null)
		{
			filters.Add("Build Rules");
		}
		sb.AppendLine("  <ItemGroup>");
		foreach (string filter in filters.OrderBy((string f) => f, StringComparer.OrdinalIgnoreCase))
		{
			string filterGuid = FormatGuid(GenerateGuid(filter));
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
			handler.AppendLiteral("    <Filter Include=\"");
			handler.AppendFormatted(filter);
			handler.AppendLiteral("\">");
			stringBuilder2.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(43, 1, stringBuilder);
			handler.AppendLiteral("      <UniqueIdentifier>");
			handler.AppendFormatted(filterGuid);
			handler.AppendLiteral("</UniqueIdentifier>");
			stringBuilder3.AppendLine(ref handler);
			sb.AppendLine("    </Filter>");
		}
		sb.AppendLine("  </ItemGroup>");
		sb.AppendLine("  <ItemGroup>");
		foreach (string source in sourceFiles)
		{
			string relativePath = Path.GetRelativePath(Path.GetDirectoryName(filtersPath), source);
			string filterPath2 = FilterPathFor(source, sourceDir);
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(26, 1, stringBuilder);
			handler.AppendLiteral("    <ClCompile Include=\"");
			handler.AppendFormatted(relativePath);
			handler.AppendLiteral("\">");
			stringBuilder4.AppendLine(ref handler);
			if (!string.IsNullOrEmpty(filterPath2))
			{
				stringBuilder = sb;
				StringBuilder stringBuilder5 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
				handler.AppendLiteral("      <Filter>");
				handler.AppendFormatted(filterPath2);
				handler.AppendLiteral("</Filter>");
				stringBuilder5.AppendLine(ref handler);
			}
			sb.AppendLine("    </ClCompile>");
		}
		sb.AppendLine("  </ItemGroup>");
		sb.AppendLine("  <ItemGroup>");
		foreach (string header in headerFiles)
		{
			string relativePath2 = Path.GetRelativePath(Path.GetDirectoryName(filtersPath), header);
			string filterPath3 = FilterPathFor(header, sourceDir);
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder6 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(26, 1, stringBuilder);
			handler.AppendLiteral("    <ClInclude Include=\"");
			handler.AppendFormatted(relativePath2);
			handler.AppendLiteral("\">");
			stringBuilder6.AppendLine(ref handler);
			if (!string.IsNullOrEmpty(filterPath3))
			{
				stringBuilder = sb;
				StringBuilder stringBuilder7 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
				handler.AppendLiteral("      <Filter>");
				handler.AppendFormatted(filterPath3);
				handler.AppendLiteral("</Filter>");
				stringBuilder7.AppendLine(ref handler);
			}
			sb.AppendLine("    </ClInclude>");
		}
		sb.AppendLine("  </ItemGroup>");
		if (module.RuleFilePath != null)
		{
			sb.AppendLine("  <ItemGroup>");
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder8 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(21, 1, stringBuilder);
			handler.AppendLiteral("    <None Include=\"");
			handler.AppendFormatted(Path.GetRelativePath(Path.GetDirectoryName(filtersPath), module.RuleFilePath));
			handler.AppendLiteral("\">");
			stringBuilder8.AppendLine(ref handler);
			sb.AppendLine("      <Filter>Build Rules</Filter>");
			sb.AppendLine("    </None>");
			sb.AppendLine("  </ItemGroup>");
		}
		if (uiFiles.Count > 0)
		{
			sb.AppendLine("  <ItemGroup>");
			foreach (string ui in uiFiles)
			{
				string relativePath3 = Path.GetRelativePath(Path.GetDirectoryName(filtersPath), ui);
				string filterPath4 = FilterPathFor(ui, sourceDir);
				StringBuilder stringBuilder = sb;
				StringBuilder stringBuilder9 = stringBuilder;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder);
				handler.AppendLiteral("    <QtUic Include=\"");
				handler.AppendFormatted(relativePath3);
				handler.AppendLiteral("\">");
				stringBuilder9.AppendLine(ref handler);
				if (!string.IsNullOrEmpty(filterPath4))
				{
					stringBuilder = sb;
					StringBuilder stringBuilder10 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
					handler.AppendLiteral("      <Filter>");
					handler.AppendFormatted(filterPath4);
					handler.AppendLiteral("</Filter>");
					stringBuilder10.AppendLine(ref handler);
				}
				sb.AppendLine("    </QtUic>");
			}
			sb.AppendLine("  </ItemGroup>");
		}
		if (resourceFiles.Count > 0)
		{
			sb.AppendLine("  <ItemGroup>");
			foreach (string qrc in resourceFiles)
			{
				string relativePath4 = Path.GetRelativePath(Path.GetDirectoryName(filtersPath), qrc);
				string filterPath5 = FilterPathFor(qrc, sourceDir);
				StringBuilder stringBuilder = sb;
				StringBuilder stringBuilder11 = stringBuilder;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder);
				handler.AppendLiteral("    <QtRcc Include=\"");
				handler.AppendFormatted(relativePath4);
				handler.AppendLiteral("\">");
				stringBuilder11.AppendLine(ref handler);
				if (!string.IsNullOrEmpty(filterPath5))
				{
					stringBuilder = sb;
					StringBuilder stringBuilder12 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
					handler.AppendLiteral("      <Filter>");
					handler.AppendFormatted(filterPath5);
					handler.AppendLiteral("</Filter>");
					stringBuilder12.AppendLine(ref handler);
				}
				sb.AppendLine("    </QtRcc>");
			}
			sb.AppendLine("  </ItemGroup>");
		}
		sb.AppendLine("</Project>");
		await GeneratedFile.WriteAllTextAsync(filtersPath, sb.ToString(), ct);
	}

	private async Task GenerateVcxprojUserAsync(ModuleRules module, string userPath, CancellationToken ct, IReadOnlyList<(string Configuration, string Platform)>? configPlatforms = null)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
		sb.AppendLine("<Project ToolsVersion=\"Current\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
		foreach (var item in configPlatforms ?? DefaultConfigPlatforms)
		{
			string config = item.Configuration;
			string platform = item.Platform;
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(65, 2, stringBuilder);
			handler.AppendLiteral("  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='");
			handler.AppendFormatted(config);
			handler.AppendLiteral("|");
			handler.AppendFormatted(platform);
			handler.AppendLiteral("'\">");
			stringBuilder2.AppendLine(ref handler);
			sb.AppendLine("    <LocalDebuggerWorkingDirectory>$(SolutionDir)</LocalDebuggerWorkingDirectory>");
			sb.AppendLine("    <DebuggerFlavor>WindowsLocalDebugger</DebuggerFlavor>");
			if (module.IsQtProject)
			{
				string qtPath = module.QtPath ?? GetDefaultQtPath(module.QtVersion);
				stringBuilder = sb;
				StringBuilder stringBuilder3 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(69, 1, stringBuilder);
				handler.AppendLiteral("    <LocalDebuggerEnvironment>PATH=");
				handler.AppendFormatted(Path.Combine(qtPath, "bin"));
				handler.AppendLiteral(";%PATH%</LocalDebuggerEnvironment>");
				stringBuilder3.AppendLine(ref handler);
			}
			sb.AppendLine("  </PropertyGroup>");
		}
		sb.AppendLine("</Project>");
		await GeneratedFile.WriteAllTextAsync(userPath, sb.ToString(), ct);
	}

	private static Guid GenerateGuid(string name)
	{
		using MD5 md5 = MD5.Create();
		return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("Omen." + name)));
	}

	/// <summary>Uppercase braced form - the format Visual Studio itself writes everywhere a
	/// GUID appears in a solution or project file.</summary>
	private static string FormatGuid(Guid guid)
	{
		return guid.ToString("B").ToUpperInvariant();
	}

	private string SourceDirectoryOf(ModuleRules module)
	{
		return Path.GetFullPath(Path.Combine(_projectRoot, module.SourceDirectory ?? ("Source/" + module.Name)));
	}

	private HashSet<string> SharedSourceDirectories(IEnumerable<ModuleRules> modules)
	{
		return (from g in modules.GroupBy(SourceDirectoryOf, StringComparer.OrdinalIgnoreCase)
			where g.Count() > 1
			select g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// A module's Visual Studio project name - its display name in Solution Explorer, and its
	/// project file and directory name.
	/// </summary>
	private string ProjectName(string moduleName)
	{
		if (!_projectNames.TryGetValue(moduleName, out string name))
		{
			return ModulePathName.Sanitize(moduleName);
		}
		return name;
	}

	/// <summary>
	/// Project names without the leading namespace: "NF::Slate.EditorLib" is Slate.EditorLib,
	/// the name people know the module by, not NF__Slate.EditorLib. Only the first qualifier
	/// goes - "3rdParty::googletest::GTest" keeps googletest__GTest, since a bare "GTest" or
	/// "Handler" says nothing.
	///
	/// Visual Studio needs project names unique, so a short name two modules would share
	/// ("AZ::Foo" and "NF::Foo", or "NF::Foo" and a plain "Foo") keeps the fully qualified
	/// name for both. If that still leaves a clash, every project keeps its qualified name.
	/// </summary>
	private static Dictionary<string, string> ProjectNamesFor(IEnumerable<ModuleRules> modules)
	{
		List<string> projectModules = (from m in modules
			where !(m is ThirdPartyPackageModuleRules)
			select m.Name).Distinct(StringComparer.Ordinal).ToList();
		Dictionary<string, int> shortNameUses = projectModules.GroupBy(Unqualified, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, string> g) => g.Key, (IGrouping<string, string> g) => g.Count(), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> names = projectModules.ToDictionary((string name) => name, (string name) => (shortNameUses[Unqualified(name)] != 1) ? ModulePathName.Sanitize(name) : Unqualified(name), StringComparer.Ordinal);
		if (names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
		{
			return projectModules.ToDictionary((string name) => name, ModulePathName.Sanitize, StringComparer.Ordinal);
		}
		return names;
		static string Unqualified(string moduleName)
		{
			int separator = moduleName.IndexOf("::", StringComparison.Ordinal);
			return ModulePathName.Sanitize((separator < 0) ? moduleName : moduleName.Substring(separator + 2));
		}
	}

	/// <summary>
	/// Deletes project directories under Intermediate\ProjectFiles that this generation did not
	/// write - a renamed module (or the NF__-qualified names projects used to carry) otherwise
	/// leaves its old project behind indefinitely. Only directories holding a generated
	/// .vcxproj or .csproj are touched; one Visual Studio still has locked is left for next time.
	/// </summary>
	private static void RemoveStaleProjectDirectories(string projectsDir, IEnumerable<string> writtenProjectPaths)
	{
		if (!Directory.Exists(projectsDir))
		{
			return;
		}
		HashSet<string> keep = writtenProjectPaths.Select((string p) => Path.GetFullPath(Path.GetDirectoryName(p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
		string[] directories = Directory.GetDirectories(projectsDir);
		foreach (string directory in directories)
		{
			if (keep.Contains(Path.GetFullPath(directory)) || (Directory.GetFiles(directory, "*.vcxproj").Length == 0 && Directory.GetFiles(directory, "*.csproj").Length == 0))
			{
				continue;
			}
			try
			{
				string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
				for (int num2 = 0; num2 < files.Length; num2++)
				{
					File.SetAttributes(files[num2], FileAttributes.Normal);
				}
				Directory.Delete(directory, recursive: true);
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
	}

	private static string XmlEscape(string text)
	{
		return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
	}

	/// <summary>
	/// A preprocessor definition as a project file states it: the literal value. The rules
	/// carry AZ_BUILD_CONFIGURATION_TYPE=\"debug\" with command-line escaping (cl's response
	/// file needs the backslashes), but MSBuild and IntelliSense take the value verbatim and
	/// escape it themselves - written as-is, IntelliSense saw a stray-backslash token.
	/// </summary>
	private static string ProjectDefinition(string definition)
	{
		return XmlEscape(definition.Replace("\\\"", "\""));
	}

	private static string StandardFlag(CppStandard standard)
	{
		return standard switch
		{
			CppStandard.Cpp14 => "/std:c++14", 
			CppStandard.Cpp17 => "/std:c++17", 
			CppStandard.Cpp20 => "/std:c++20", 
			_ => "/std:c++latest", 
		};
	}

	/// <summary>
	/// The compiler options a module compiles with, split for the project file: forced
	/// includes (/FI, which have their own property) and everything else IntelliSense should
	/// see. Same source and order as the compile action: the configuration's engine-wide
	/// options, then the module's own. Driver-only switches (/nologo, /c, /MP) are dropped.
	/// </summary>
	private (List<string> ForcedIncludes, List<string> AdditionalOptions) IntelliSenseOptions(ModuleRules module, string config)
	{
		IEnumerable<string> enumerable = ((_contextsByConfig.TryGetValue(config, out BuildContext exact) ? exact : _contextsByConfig.Values.FirstOrDefault())?.GlobalCompilerOptions ?? Array.Empty<string>()).Concat(module.AdditionalCompilerFlags);
		List<string> forcedIncludes = new List<string>();
		List<string> options = new List<string>();
		foreach (string flag in enumerable)
		{
			if (flag.StartsWith("/FI", StringComparison.Ordinal) || flag.StartsWith("-FI", StringComparison.Ordinal))
			{
				string path = flag.Substring(3).Trim().Trim('"');
				if (path.Length > 0 && !forcedIncludes.Contains(path, StringComparer.OrdinalIgnoreCase))
				{
					forcedIncludes.Add(path.Replace('/', '\\'));
				}
				continue;
			}
			bool flag2;
			switch (flag)
			{
			case "/nologo":
			case "/c":
			case "/MP":
			case "-c":
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			if (!flag2 && !options.Contains(flag, StringComparer.Ordinal))
			{
				options.Add(flag);
			}
		}
		return (ForcedIncludes: forcedIncludes, AdditionalOptions: options);
	}

	/// <summary>
	/// The solution-folder chain a module's project nests under, mirroring the source tree
	/// (the Sharpmake convention). Derivation from the module's source directory: a trailing
	/// segment that IS the module keeps the project one level up (Code/Framework/AtomCore -&gt;
	/// Code\Framework); otherwise the chain stops at the first structural segment - Source/
	/// Private/Public/Include/Platform anywhere, Code past the first position - so a gem's
	/// Gems/Atom/RHI/Code/Source/... collapses to Gems\Atom\RHI. ThirdParty wrappers all
	/// group under one ThirdParty folder; a directory outside the project root under
	/// External. Empty means the project sits at the solution root.
	/// </summary>
	private IReadOnlyList<string> SolutionFolderFor(string moduleName, ModuleRules module)
	{
		if (module.Type == ModuleType.ThirdParty)
		{
			return new[] { "ThirdParty" };
		}
		string sourceDir = module.SourceDirectory ?? ("Source/" + module.Name);
		string relative = Path.GetRelativePath(_projectRoot, Path.GetFullPath(Path.Combine(_projectRoot, sourceDir)));
		if (relative == ".")
		{
			return Array.Empty<string>();
		}
		List<string> segments = (from text in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			where text.Length > 0 && text != "."
			select text).ToList();
		if (segments.Count == 0)
		{
			return Array.Empty<string>();
		}
		if (segments[0] == "..")
		{
			return new[] { "External" };
		}
		string baseName = ModulePathName.BinaryName(moduleName);
		if (!segments[segments.Count - 1].Equals(baseName, StringComparison.OrdinalIgnoreCase))
		{
			if (!segments[segments.Count - 1].Equals(ModulePathName.Sanitize(moduleName), StringComparison.OrdinalIgnoreCase))
			{
				for (int i = 0; i < segments.Count; i++)
				{
					string s = segments[i];
					if (s.Equals("Source", StringComparison.OrdinalIgnoreCase) || s.Equals("src", StringComparison.OrdinalIgnoreCase) || s.Equals("Private", StringComparison.OrdinalIgnoreCase) || s.Equals("Public", StringComparison.OrdinalIgnoreCase) || s.Equals("Include", StringComparison.OrdinalIgnoreCase) || s.Equals("Platform", StringComparison.OrdinalIgnoreCase) || (i > 0 && s.Equals("Code", StringComparison.OrdinalIgnoreCase)))
					{
						return segments.Take(i).ToList();
					}
				}
				return segments;
			}
		}
		segments.RemoveAt(segments.Count - 1);
		return segments;
	}

	/// <summary>
	/// The filter (Solution Explorer folder inside a project) a file belongs to: its
	/// directory relative to the module's source dir, or relative to the project root for a
	/// file living elsewhere - a filter path is a display name and must never contain "..".
	/// Null when the file sits at the filter root.
	/// </summary>
	private string? FilterPathFor(string file, string sourceDir)
	{
		string relative = Path.GetRelativePath(sourceDir, file);
		if (relative.StartsWith("..", StringComparison.Ordinal))
		{
			relative = Path.GetRelativePath(_projectRoot, file);
		}
		if (relative.StartsWith("..", StringComparison.Ordinal))
		{
			return "External";
		}
		string dir = Path.GetDirectoryName(relative)?.Replace('/', '\\');
		if (!string.IsNullOrEmpty(dir))
		{
			return dir;
		}
		return null;
	}
}
