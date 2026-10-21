using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Omen.Core.Options;

namespace Omen.Core.Rules;

/// <summary>
/// Compiles .module.cs and .target.cs rule files using Roslyn.
/// </summary>
public sealed class RuleCompiler
{
	/// <summary>
	/// The exact language version rule files are parsed/compiled against. Shared so anything
	/// that parses a rule file outside a full CompileRulesAsync (e.g. a GUI editor's live
	/// diagnostics) parses it the same way the build does.
	/// </summary>
	public static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12);

	/// <summary>
	/// The exact compilation options rule files are compiled with. Shared for the same reason
	/// as <see cref="F:Omen.Core.Rules.RuleCompiler.ParseOptions" /> - a second, hand-rolled options object in the GUI could
	/// silently drift from what a real build uses.
	/// </summary>
	public static readonly CSharpCompilationOptions CompilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, reportSuppressedDiagnostics: false, null, null, null, null, OptimizationLevel.Release);

	private readonly string _cacheDirectory;

	private readonly List<MetadataReference> _references;

	/// <summary>
	/// The single root project-definition file RuleCompiler looks for directly in the project
	/// root (never recursively - one file, at the top). See OmenProjectRules for the shape and
	/// why the whole-tree glob below is only the fallback for a project that doesn't have one.
	/// </summary>
	private const string ProjectFilePattern = "*.omenproject.cs";

	private const string PluginFilePattern = "*.plugin.cs";

	/// <summary>
	/// Compiles just the root project-definition file and instantiates its (sole, non-abstract,
	/// parameterless-constructible) OmenProjectRules subclass. Deliberately a separate, much
	/// smaller compile from the real one below: which directories to glob has to be known
	/// *before* the full file list can be assembled, so this can't be folded into the same pass.
	/// Synchronous (File.ReadAllText, not ReadAllTextAsync) - see DiscoverRuleFilesCore's remark
	/// on why discovery itself is sync.
	/// </summary>
	private static readonly ConcurrentDictionary<string, (DateTime WriteTime, string OptionsKey, OmenProjectRules Rules)> ProjectDefinitions = new ConcurrentDictionary<string, (DateTime, string, OmenProjectRules)>(StringComparer.OrdinalIgnoreCase);

	private static readonly TimeSpan DiscoveryCacheLifetime = TimeSpan.FromSeconds(5.0);

	private readonly ConcurrentDictionary<string, (DateTime AsOf, RuleFileDiscovery Discovery)> _discoveryCache = new ConcurrentDictionary<string, (DateTime, RuleFileDiscovery)>();

	private readonly ConcurrentDictionary<string, (DateTime WriteTimeUtc, SyntaxTree Tree)> _treeCache = new ConcurrentDictionary<string, (DateTime, SyntaxTree)>();

	/// <summary>
	/// The project's compilation with every rule file parsed from disk. Roslyn is built for
	/// incremental edits: rebuilding a compilation over ~230 trees per keystroke is what makes an
	/// editor stutter, whereas ReplaceSyntaxTree reuses everything the edit did not touch.
	/// Rebuilt only when discovery changes; the tree cache underneath it keys on last-write time,
	/// so an external edit to another file is still picked up.
	/// </summary>
	private readonly ConcurrentDictionary<string, (string[] Files, CSharpCompilation Compilation)> _baseCompilationCache = new ConcurrentDictionary<string, (string[], CSharpCompilation)>();

	private static readonly Lazy<List<MetadataReference>> SharedReferences = new Lazy<List<MetadataReference>>(BuildMetadataReferencesCore);

	/// <summary>
	/// The exact metadata references rule files are compiled against (the full trusted-platform
	/// assembly set plus Omen.Core itself). Exposed so a single-document validator - in the GUI
	/// or anywhere else - resolves types and members exactly as the real build would, rather
	/// than maintaining a second, possibly-divergent reference list.
	/// </summary>
	public IReadOnlyList<MetadataReference> References => _references;

	public RuleCompiler(string? cacheDirectory = null)
	{
		_cacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "Omen", "RuleCache");
		Directory.CreateDirectory(_cacheDirectory);
		_references = BuildMetadataReferences();
	}

	/// <summary>The rule cache directory for a project root. Was hand-assembled at six call
	/// sites; a clear that misses one is worse than useless.</summary>
	public static string CacheDirectoryFor(string projectRoot)
	{
		return Path.Combine(projectRoot, "Intermediate", "RuleCache");
	}

	/// <summary>
	/// Deletes a project's on-disk rule cache: the compiled rule assemblies and their sidecar
	/// type-to-file maps. Everything in here is derived, so removing it only costs one slower
	/// build. Returns the number of files removed; a file locked by another process is skipped
	/// rather than failing the whole clear.
	/// </summary>
	public static int ClearCacheDirectory(string cacheDirectory)
	{
		if (!Directory.Exists(cacheDirectory))
		{
			return 0;
		}
		int removed = 0;
		string[] files = Directory.GetFiles(cacheDirectory);
		foreach (string file in files)
		{
			try
			{
				File.Delete(file);
				removed++;
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		return removed;
	}

	/// <summary>
	/// Drops this instance's in-memory caches. The GUI keeps one RuleCompiler for the process
	/// (RuleEditorService), so clearing only the disk cache would leave the editor validating
	/// against rule files it parsed minutes ago.
	/// </summary>
	public void ClearMemoryCaches()
	{
		_discoveryCache.Clear();
		_baseCompilationCache.Clear();
	}

	/// <summary>
	/// The directory a target file's build is rooted at: the nearest ancestor holding a
	/// <c>*.omenproject.cs</c>, else the target file's own directory.
	///
	/// Every path in a rule file - ModuleRules.SourceDirectory, include paths, the
	/// Intermediate/Binaries trees - is resolved relative to this. Declaring a target inside a
	/// plugin is normal (plugins are the project's ExternalSubdirectories), and rooting that build at
	/// the plugin instead of at the project makes every engine module undiscoverable: AZ::AzCore's
	/// SourceDirectory "Code/Framework/AzCore" is relative to the engine root, so from a plugin it
	/// points nowhere and its headers are never on the include path. A project with no
	/// *.omenproject.cs keeps exactly the previous behavior.
	/// </summary>
	public static string ResolveProjectRoot(string targetFile)
	{
		string directory = Path.GetDirectoryName(targetFile);
		if (string.IsNullOrEmpty(directory))
		{
			return Path.GetPathRoot(targetFile) ?? targetFile;
		}
		for (DirectoryInfo candidate = new DirectoryInfo(directory); candidate != null; candidate = candidate.Parent)
		{
			if (candidate.GetFiles("*.omenproject.cs").Length != 0 || candidate.GetFiles("*.plugin.cs").Length != 0)
			{
				return candidate.FullName;
			}
		}
		return directory;
	}

	/// <summary>
	/// Finds every *.module.cs / *.target.cs / *.plugin.cs rule file a project's build would
	/// compile, plus the project definition file(s) found at the root (0 or 1). The single
	/// place CompileRulesAsync and any external caller (e.g. the GUI editor, which needs the
	/// "rest of the project" file set to validate one open document in context) discover rule
	/// files, so both always see exactly the same set - including honouring a
	/// `*.omenproject.cs`'s declared SourceRoots/ExternalSubdirectories instead of a whole-tree
	/// glob. With no project definition file present, behavior is unchanged from before this
	/// file existed: the entire project tree is globbed, so every pre-existing project keeps
	/// compiling as-is.
	///
	/// Synchronous by design, unlike the CompileRulesAsync/CompileFilesAsync file reads: the one
	/// file this reads and compiles (the project definition, see LoadProjectDefinition) is small
	/// and the result is what BuildCompilationAsync's editor path needs to cache and reuse
	/// synchronously inside its own Task.Run (see DiscoverRuleFilesCached below) - an async
	/// version would just be `Task.Run` wrapped around this same body, which callers that do
	/// want off-thread execution (CompileRulesAsync included) do explicitly instead.
	/// </summary>
	private RuleFileDiscovery DiscoverRuleFilesCore(string projectRoot, CancellationToken ct)
	{
		string[] projectFiles = Directory.GetFiles(projectRoot, "*.omenproject.cs", SearchOption.TopDirectoryOnly);
		if (projectFiles.Length > 1)
		{
			throw new InvalidOperationException($"Found {projectFiles.Length} '{"*.omenproject.cs"}' files in '{projectRoot}' - " + "a project may have at most one root project definition.");
		}
		OmenProjectRules project = null;
		List<string> excluded = new List<string>();
		List<string> scanRoots;
		if (projectFiles.Length == 1)
		{
			project = LoadProjectDefinition(projectFiles[0], ct, OptionValuesFor(projectRoot));
			IReadOnlyList<string> fetchedDirectories = ContentFetcher.EnsureFetched(project.FetchContent, projectRoot);
			scanRoots = (from root in project.SourceRoots.Concat(project.ExternalSubdirectories)
				select (!Path.IsPathRooted(root)) ? Path.Combine(projectRoot, root) : root).Concat(fetchedDirectories).ToList();
			excluded = project.ExcludedDirectories.Select((string directory) => Path.GetFullPath(Path.IsPathRooted(directory) ? directory : Path.Combine(projectRoot, directory))).ToList();
		}
		else
		{
			int num = 1;
			List<string> list = new List<string>(num);
			CollectionsMarshal.SetCount(list, num);
			CollectionsMarshal.AsSpan(list)[0] = projectRoot;
			scanRoots = list;
		}
		var (moduleFiles, targetFiles, pluginFiles) = GlobRuleFiles(scanRoots, excluded);
		return new RuleFileDiscovery(moduleFiles, targetFiles, pluginFiles, projectFiles, project);
	}

	private static (List<string> Modules, List<string> Targets, List<string> Plugins) GlobRuleFiles(List<string> roots, List<string> excludedDirectories)
	{
		List<string> modules = new List<string>();
		List<string> targets = new List<string>();
		List<string> plugins = new List<string>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		EnumerationOptions options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.ReparsePoint
		};
		foreach (string item in roots.Where(Directory.Exists))
		{
			foreach (string file in Directory.EnumerateFiles(item, "*.cs", options))
			{
				List<string> bucket;
				if (file.EndsWith(".module.cs", StringComparison.OrdinalIgnoreCase))
				{
					bucket = modules;
				}
				else if (file.EndsWith(".target.cs", StringComparison.OrdinalIgnoreCase))
				{
					bucket = targets;
				}
				else
				{
					if (!file.EndsWith(".plugin.cs", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					bucket = plugins;
				}
				string full = Path.GetFullPath(file);
				if (!excludedDirectories.Any((string excluded) => IsUnder(full, excluded)) && seen.Add(full))
				{
					bucket.Add(full);
				}
			}
		}
		return (Modules: modules, Targets: targets, Plugins: plugins);
	}

	/// <summary>
	/// Async wrapper around <see cref="M:Omen.Core.Rules.RuleCompiler.DiscoverRuleFilesCore(System.String,System.Threading.CancellationToken)" /> for callers (CompileRulesAsync,
	/// tests, any future async caller) that want discovery off the calling thread but don't need
	/// <see cref="M:Omen.Core.Rules.RuleCompiler.DiscoverRuleFilesCached(System.String,System.DateTime,System.Threading.CancellationToken)" />'s reuse-within-5-seconds behavior.
	/// </summary>
	public Task<RuleFileDiscovery> DiscoverRuleFilesAsync(string projectRoot, CancellationToken ct = default(CancellationToken))
	{
		return Task.Run(() => DiscoverRuleFilesCached(projectRoot, DateTime.UtcNow, ct), ct);
	}

	/// <summary>
	/// Compiles and loads rule files from a project directory.
	/// </summary>
	public async Task<CompiledRules> CompileRulesAsync(string projectRoot, CancellationToken ct = default(CancellationToken))
	{
		RuleFileDiscovery discovery = await DiscoverRuleFilesAsync(projectRoot, ct);
		IReadOnlyList<string> moduleFiles = discovery.ModuleFiles;
		IReadOnlyList<string> targetFiles = discovery.TargetFiles;
		IReadOnlyList<string> pluginFiles = discovery.PluginFiles;
		IReadOnlyList<string> pluginFiles2 = pluginFiles;
		IReadOnlyList<string> targetFiles2 = targetFiles;
		IReadOnlyList<string> moduleFiles2 = moduleFiles;
		if (moduleFiles2.Count == 0 && targetFiles2.Count == 0 && pluginFiles2.Count == 0)
		{
			throw new InvalidOperationException("No rule files found in '" + projectRoot + "'.");
		}
		List<string> allFiles = moduleFiles2.Concat(targetFiles2).Concat(pluginFiles2).Concat(discovery.ProjectFiles)
			.ToList();
		string cacheKey = ComputeCacheKey(allFiles);
		string cachedAssemblyPath = Path.Combine(_cacheDirectory, cacheKey + ".dll");
		string mapPath = Path.Combine(_cacheDirectory, cacheKey + ".rulemap.json");
		Assembly assembly = null;
		IReadOnlyDictionary<string, string> ruleFilePaths = null;
		if (File.Exists(cachedAssemblyPath) && File.Exists(mapPath) && IsCacheValid(cachedAssemblyPath, allFiles))
		{
			try
			{
				assembly = LoadAssembly(cachedAssemblyPath);
				ruleFilePaths = LoadRuleFilePaths(mapPath);
			}
			catch (IOException)
			{
				assembly = null;
			}
			catch (UnauthorizedAccessException)
			{
				assembly = null;
			}
		}
		if (assembly == null || ruleFilePaths == null)
		{
			(assembly, ruleFilePaths) = await CompileFilesAsync(allFiles, cachedAssemblyPath, mapPath, ct);
		}
		return new CompiledRules(assembly, moduleFiles2, targetFiles2, pluginFiles2, discovery.Project, ruleFilePaths);
	}

	/// <summary>
	/// Globs one rule-file pattern across every scan root, skipping a root that doesn't exist
	/// (an ExternalSubdirectories entry pointing at an unregistered/missing plugin path is a no-op,
	/// not a hard failure - mirroring how a missing add_subdirectory target is only ever an
	/// error at the point something tries to depend on it, never at discovery time).
	/// </summary>
	private static List<string> GlobRoots(List<string> roots, string searchPattern, List<string> excludedDirectories)
	{
		return (from file in roots.Where(Directory.Exists).SelectMany((string root) => Directory.GetFiles(root, searchPattern, SearchOption.AllDirectories)).Select(Path.GetFullPath)
			where !excludedDirectories.Any((string excluded) => IsUnder(file, excluded))
			select file).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>Path containment by directory boundary, so an excluded "Tools" never also
	/// swallows a sibling "ToolsExtra".</summary>
	private static bool IsUnder(string file, string directory)
	{
		return file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>The saved option values for a project - the Omen half of CMakeCache.txt.</summary>
	internal static IReadOnlyDictionary<string, string> OptionValuesFor(string projectRoot)
	{
		return new OptionCacheStore(Path.Combine(projectRoot, "Intermediate", "omen-cache.json")).Load();
	}

	private OmenProjectRules LoadProjectDefinition(string projectFile, CancellationToken ct, IReadOnlyDictionary<string, string> optionValues)
	{
		DateTime writeTime = File.GetLastWriteTimeUtc(projectFile);
		string optionsKey = string.Join("\u0001", from kv in optionValues.OrderBy((KeyValuePair<string, string> kv) => kv.Key, StringComparer.Ordinal)
			select kv.Key + "=" + kv.Value);
		if (ProjectDefinitions.TryGetValue(projectFile, out (DateTime, string, OmenProjectRules) cached) && cached.Item1 == writeTime && cached.Item2 == optionsKey)
		{
			return cached.Item3;
		}
		OmenProjectRules.LoadingOptionValues = optionValues;
		OmenProjectRules rules;
		try
		{
			rules = CompileProjectDefinition(projectFile, ct);
		}
		finally
		{
			OmenProjectRules.LoadingOptionValues = null;
		}
		ProjectDefinitions[projectFile] = (writeTime, optionsKey, rules);
		return rules;
	}

	private OmenProjectRules CompileProjectDefinition(string projectFile, CancellationToken ct)
	{
		SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(projectFile), ParseOptions, projectFile, null, ct);
		CSharpCompilation compilation = CSharpCompilation.Create($"OmenProjectDefinition_{Guid.NewGuid():N}", new[] { syntaxTree }, _references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		using MemoryStream ms = new MemoryStream();
		EmitResult result = compilation.Emit(ms, null, null, null, null, null, null, null, null, null, ct);
		if (!result.Success)
		{
			List<string> errors = (from d in result.Diagnostics
				where d.Severity == DiagnosticSeverity.Error
				select $"{d.Location}: {d.GetMessage()}").ToList();
			throw new RuleCompilationException("Failed to compile project definition '" + projectFile + "':\n" + string.Join("\n", errors), result.Diagnostics);
		}
		ms.Seek(0L, SeekOrigin.Begin);
		Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
		Type projectType = typeof(OmenProjectRules);
		Type type = assembly.GetTypes().FirstOrDefault((Type t) => !t.IsAbstract && projectType.IsAssignableFrom(t));
		if (type == null)
		{
			throw new InvalidOperationException("'" + projectFile + "' does not declare a non-abstract OmenProjectRules subclass.");
		}
		ConstructorInfo? constructor = type.GetConstructor(Type.EmptyTypes);
		if (constructor == null)
		{
			throw new InvalidOperationException("Project rule type '" + type.Name + "' must have a parameterless constructor.");
		}
		return (OmenProjectRules)constructor.Invoke(null);
	}

	/// <summary>
	/// Validates one rule file's current (possibly unsaved) text against the rest of its
	/// project, using the exact same parse options, compilation options, and metadata
	/// references as <see cref="M:Omen.Core.Rules.RuleCompiler.CompileRulesAsync(System.String,System.Threading.CancellationToken)" /> - so a file that is clean here will not
	/// then fail the real build, and vice versa. Every other rule file in the project is read
	/// from disk; only <paramref name="filePath" /> uses <paramref name="currentText" />, so
	/// unsaved edits are reflected immediately. No assembly is emitted and nothing is cached -
	/// this is a lighter-weight, diagnostics-only compile meant to run on every keystroke/save.
	/// Diagnostics are filtered to <paramref name="filePath" />'s own syntax tree; other files'
	/// errors are the project build's job to report, not this document's.
	/// </summary>
	public async Task<ImmutableArray<Diagnostic>> ValidateFileAsync(string? projectRoot, string filePath, string currentText, CancellationToken ct = default(CancellationToken))
	{
		var (compilation, activeTree) = await BuildCompilationAsync(projectRoot, filePath, currentText, ct);
		return await Task.Run(() =>
		{
			SemanticModel semanticModel = compilation.GetSemanticModel(activeTree);
			CancellationToken cancellationToken = ct;
			ImmutableArray<Diagnostic> diagnostics = semanticModel.GetDiagnostics(null, cancellationToken);
			return activeTree.GetDiagnostics(ct).Concat(diagnostics).ToImmutableArray();
		}, ct);
	}

	/// <summary>
	/// Discovers this project's rule files, reusing a recent result. A short lifetime rather than
	/// an indefinite cache: a rule file added while the editor is open should show up on its own,
	/// and five seconds is far longer than a burst of typing but far shorter than a person
	/// noticing staleness. Goes through the same <see cref="M:Omen.Core.Rules.RuleCompiler.DiscoverRuleFilesCore(System.String,System.Threading.CancellationToken)" /> the build
	/// uses, so a project with a root `*.omenproject.cs` is scanned by SourceRoots/
	/// ExternalSubdirectories here too, not the old whole-tree glob - the entire reason
	/// ValidateFileAsync/BuildCompilationAsync and CompileRulesAsync now share one discovery path.
	/// </summary>
	private RuleFileDiscovery DiscoverRuleFilesCached(string projectRoot, DateTime nowUtc, CancellationToken ct)
	{
		if (_discoveryCache.TryGetValue(projectRoot, out (DateTime, RuleFileDiscovery) cached) && nowUtc - cached.Item1 < DiscoveryCacheLifetime)
		{
			return cached.Item2;
		}
		RuleFileDiscovery discovery = DiscoverRuleFilesCore(projectRoot, ct);
		_discoveryCache[projectRoot] = (nowUtc, discovery);
		return discovery;
	}

	/// <summary>
	/// Parses a file the user is not editing, reusing the previous syntax tree while the file's
	/// last-write time is unchanged. Parsing every rule file in the project on every keystroke is
	/// the other half of what made this slow.
	/// </summary>
	private SyntaxTree ParseCached(string path, CancellationToken ct)
	{
		DateTime writeTime = File.GetLastWriteTimeUtc(path);
		if (_treeCache.TryGetValue(path, out (DateTime, SyntaxTree) cached) && cached.Item1 == writeTime)
		{
			return cached.Item2;
		}
		SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path, null, ct);
		_treeCache[path] = (writeTime, tree);
		return tree;
	}

	/// <summary>
	/// Builds the same <see cref="T:Microsoft.CodeAnalysis.Compilation" /> <see cref="M:Omen.Core.Rules.RuleCompiler.ValidateFileAsync(System.String,System.String,System.String,System.Threading.CancellationToken)" /> validates
	/// against - same file set, same parse/compilation options, same references - and hands it
	/// back together with the active file's own <see cref="T:Microsoft.CodeAnalysis.SyntaxTree" />. This is the seam
	/// IntelliSense (completion, quick info, signature help) is meant to build on: a
	/// SemanticModel/LookupSymbols query against this exact Compilation sees the same types and
	/// members a real build would, with no separate analysis engine or reference list involved.
	///
	/// The entire body runs on the thread pool. Discovery, file I/O and parsing are all far too
	/// slow to touch a UI thread on a tree this size, and awaiting file reads on the caller's
	/// thread marshals a continuation back to the dispatcher per file - which for ~230 rule files
	/// is a freeze, not a pause.
	/// </summary>
	public Task<(Compilation Compilation, SyntaxTree ActiveTree)> BuildCompilationAsync(string? projectRoot, string filePath, string currentText, CancellationToken ct = default(CancellationToken))
	{
		return Task.Run(() =>
		{
			string fullActivePath = Path.GetFullPath(filePath);
			SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(currentText, ParseOptions, filePath, null, ct);
			if (projectRoot == null || !Directory.Exists(projectRoot))
			{
				return ((Compilation, SyntaxTree activeTree))(CSharpCompilation.Create($"OmenRuleValidation_{Guid.NewGuid():N}", new[] { syntaxTree }, _references, CompilationOptions), activeTree: syntaxTree);
			}
			RuleFileDiscovery ruleFileDiscovery = DiscoverRuleFilesCached(projectRoot, DateTime.UtcNow, ct);
			string[] files = ruleFileDiscovery.ModuleFiles.Concat(ruleFileDiscovery.TargetFiles).Concat(ruleFileDiscovery.PluginFiles).ToArray();
			CSharpCompilation orBuildBaseCompilation = GetOrBuildBaseCompilation(projectRoot, files, ct);
			SyntaxTree syntaxTree2 = orBuildBaseCompilation.SyntaxTrees.FirstOrDefault((SyntaxTree t) => string.Equals(Path.GetFullPath(t.FilePath), fullActivePath, StringComparison.OrdinalIgnoreCase));
			return ((Compilation, SyntaxTree activeTree))((syntaxTree2 != null) ? orBuildBaseCompilation.ReplaceSyntaxTree(syntaxTree2, syntaxTree) : orBuildBaseCompilation.AddSyntaxTrees(syntaxTree), activeTree: syntaxTree);
		}, ct);
	}

	/// <summary>
	/// Builds - or reuses - the all-files-from-disk compilation the active document is edited into.
	/// Keyed on the discovered file list, so adding or removing a rule file rebuilds it and an edit
	/// to an unopened file is picked up through <see cref="M:Omen.Core.Rules.RuleCompiler.ParseCached(System.String,System.Threading.CancellationToken)" />'s write-time check.
	/// </summary>
	private CSharpCompilation GetOrBuildBaseCompilation(string projectRoot, string[] files, CancellationToken ct)
	{
		List<SyntaxTree> trees = new List<SyntaxTree>(files.Length);
		foreach (string file in files)
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				trees.Add(ParseCached(file, ct));
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		if (_baseCompilationCache.TryGetValue(projectRoot, out (string[], CSharpCompilation) cached) && cached.Item1.SequenceEqual(files) && cached.Item2.SyntaxTrees.SequenceEqual(trees))
		{
			return cached.Item2;
		}
		CSharpCompilation compilation = CSharpCompilation.Create($"OmenRuleProject_{Guid.NewGuid():N}", trees, _references, CompilationOptions);
		_baseCompilationCache[projectRoot] = (files, compilation);
		return compilation;
	}

	private async Task<(Assembly Assembly, IReadOnlyDictionary<string, string> RuleFilePaths)> CompileFilesAsync(List<string> files, string outputPath, string mapPath, CancellationToken ct)
	{
		List<SyntaxTree> syntaxTrees = new List<SyntaxTree>();
		foreach (string file in files)
		{
			SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(file, ct), ParseOptions, file);
			syntaxTrees.Add(syntaxTree);
		}
		Dictionary<string, string> ruleFilePaths = MapDeclaredTypesToFiles(syntaxTrees);
		CSharpCompilation compilation = CSharpCompilation.Create($"OmenRules_{Guid.NewGuid():N}", syntaxTrees, _references, CompilationOptions);
		using MemoryStream ms = new MemoryStream();
		EmitResult result = compilation.Emit(ms, null, null, null, null, null, null, null, null, null, ct);
		if (!result.Success)
		{
			List<string> errors = (from d in result.Diagnostics
				where d.Severity == DiagnosticSeverity.Error
				select $"{d.Location}: {d.GetMessage()}").ToList();
			throw new RuleCompilationException("Failed to compile rule files:\n" + string.Join("\n", errors), result.Diagnostics);
		}
		ms.Seek(0L, SeekOrigin.Begin);
		TryWriteCache(outputPath, mapPath, ms, ruleFilePaths);
		ms.Seek(0L, SeekOrigin.Begin);
		return (Assembly: AssemblyLoadContext.Default.LoadFromStream(ms), RuleFilePaths: ruleFilePaths);
	}

	/// <summary>
	/// Publishes the compiled rule assembly and its rule-file map into the shared cache, or gives
	/// up quietly. Both are written to a temp file unique to this process and then moved into
	/// place; a move that loses to a concurrent writer leaves the winner's identical bytes alone.
	/// </summary>
	private static void TryWriteCache(string outputPath, string mapPath, MemoryStream assembly, IReadOnlyDictionary<string, string> ruleFilePaths)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
			PublishAtomically(outputPath, (string temp) =>
			{
				File.WriteAllBytes(temp, assembly.ToArray());
			});
			PublishAtomically(mapPath, (string temp) =>
			{
				File.WriteAllText(temp, JsonSerializer.Serialize(ruleFilePaths));
			});
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static void PublishAtomically(string finalPath, Action<string> write)
	{
		string temp = $"{finalPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
		try
		{
			write(temp);
			File.Move(temp, finalPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temp))
			{
				try
				{
					File.Delete(temp);
				}
				catch (IOException)
				{
				}
			}
		}
	}

	/// <summary>
	/// Every type declared across the compilation, mapped to the absolute path of the file
	/// declaring it. Syntax only - no binding - so this is cheap next to the compile itself.
	/// </summary>
	private static Dictionary<string, string> MapDeclaredTypesToFiles(IEnumerable<SyntaxTree> syntaxTrees)
	{
		Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (SyntaxTree tree in syntaxTrees)
		{
			if (string.IsNullOrEmpty(tree.FilePath))
			{
				continue;
			}
			string path = Path.GetFullPath(tree.FilePath);
			foreach (BaseTypeDeclarationSyntax declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
			{
				map[FullTypeName(declaration)] = path;
			}
		}
		return map;
	}

	private static string FullTypeName(BaseTypeDeclarationSyntax declaration)
	{
		List<string> parts = new List<string> { declaration.Identifier.ValueText };
		for (SyntaxNode node = declaration.Parent; node != null; node = node.Parent)
		{
			if (!(node is BaseTypeDeclarationSyntax outer))
			{
				if (node is BaseNamespaceDeclarationSyntax ns)
				{
					parts.Insert(0, ns.Name.ToString());
				}
			}
			else
			{
				parts.Insert(0, outer.Identifier.ValueText);
			}
		}
		return string.Join(".", parts);
	}

	private static IReadOnlyDictionary<string, string> LoadRuleFilePaths(string mapPath)
	{
		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mapPath)) ?? new Dictionary<string, string>();
		}
		catch (Exception ex) when ((ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) ? true : false)
		{
			return new Dictionary<string, string>();
		}
	}

	private Assembly LoadAssembly(string path)
	{
		using MemoryStream ms = new MemoryStream(File.ReadAllBytes(path));
		return AssemblyLoadContext.Default.LoadFromStream(ms);
	}

	private static List<MetadataReference> BuildMetadataReferences()
	{
		return SharedReferences.Value;
	}

	private static List<MetadataReference> BuildMetadataReferencesCore()
	{
		List<MetadataReference> references = new List<MetadataReference>();
		string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString();
		if (trustedAssemblies != null)
		{
			string[] array = trustedAssemblies.Split(Path.PathSeparator);
			foreach (string assembly in array)
			{
				if (File.Exists(assembly))
				{
					try
					{
						references.Add(MetadataReference.CreateFromFile(assembly));
					}
					catch
					{
					}
				}
			}
		}
		Assembly coreAssembly = typeof(ModuleRules).Assembly;
		references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
		return references;
	}

	private string ComputeCacheKey(List<string> files)
	{
		using SHA256 sha = SHA256.Create();
		string content = string.Join("|", from f in files
			orderby f
			select $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}");
		string coreAssemblyPath = typeof(ModuleRules).Assembly.Location;
		content += $"|{coreAssemblyPath}:{File.GetLastWriteTimeUtc(coreAssemblyPath).Ticks}";
		return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Substring(0, 16);
	}

	private bool IsCacheValid(string cachedPath, List<string> sourceFiles)
	{
		DateTime cacheTime = File.GetLastWriteTimeUtc(cachedPath);
		return sourceFiles.All((string f) => File.GetLastWriteTimeUtc(f) < cacheTime);
	}
}
