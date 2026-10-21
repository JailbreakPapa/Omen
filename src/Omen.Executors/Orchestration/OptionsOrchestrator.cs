using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Omen.Core.Configuration;
using Omen.Core.Options;
using Omen.Core.Rules;

namespace Omen.Executors.Orchestration;

/// <summary>
/// Discovers a project's declared build options without building anything - the Omen
/// equivalent of a CMake Configure pass. Reuses BuildOrchestrator's resolve-and-instantiate
/// sequence (target file -&gt; rule compilation -&gt; BuildContext -&gt; CreateTargetRules/
/// CreateModuleRules) and stops there: instantiating rules is what runs BuildOptions.Declare
/// calls and populates BuildContext.DeclaredOptions, which is the entire discovery mechanism.
/// </summary>
public sealed class OptionsOrchestrator
{
	public async Task<IReadOnlyList<BuildOptionDeclaration>?> DiscoverAsync(OptionsOrchestratorRequest request, IProgress<OrchestratorEvent>? events, CancellationToken ct = default(CancellationToken))
	{
		string workingDir = ResolveWorkingDir(request.TargetFile);
		OptionCacheStore cacheStore = new OptionCacheStore(CachePath(workingDir));
		var platform = request.Platform ?? Omen.Platforms.PlatformFactory.GetHostPlatform();
		BuildContext context = new BuildContext
		{
			Platform = platform,
			Architecture = request.Architecture ?? Omen.Platforms.PlatformFactory.GetDefaultArchitecture(request.Platform ?? Omen.Platforms.PlatformFactory.GetHostPlatform()),
			Configuration = request.Configuration,
			ProjectRoot = workingDir,
			IntermediateDirectory = Path.Combine(workingDir, "Intermediate", $"{platform}_{request.Configuration}"),
			OutputDirectory = Path.Combine(workingDir, "Binaries", $"{platform}_{request.Configuration}"),
			CachedOptionValues = cacheStore.Load()
		};
		RuleCompiler ruleCompiler = new RuleCompiler(RuleCompiler.CacheDirectoryFor(workingDir));
		CompiledRules compiledRules;
		try
		{
			events?.Report(new OrchestratorEvent("Compiling build rules...", OrchestratorEventLevel.Info));
			compiledRules = await ruleCompiler.CompileRulesAsync(workingDir, ct);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			events?.Report(new OrchestratorEvent("Error compiling rules: " + ex2.Message, OrchestratorEventLevel.Error));
			return null;
		}
		var targets = compiledRules.CreateTargetRules(context);
		compiledRules.CreateModuleRules(context);
		int configuredFiles = ProjectWorkflow.ConfigureFiles(context, compiledRules.Project, null);
		var selectedTypes = compiledRules.FindTargetTypes(request.TargetFile).ToHashSet();
		foreach (var target in targets.Where(t => selectedTypes.Contains(t.GetType())))
			configuredFiles += ProjectWorkflow.ConfigureFiles(context, compiledRules.Project, target, includeProject: false);
		if (configuredFiles > 0) events?.Report(new OrchestratorEvent($"Updated {configuredFiles} configured file(s)", OrchestratorEventLevel.Info));
		ManualOptionsStore manualOptionsStore = new ManualOptionsStore(ManualOptionsPath(workingDir));
		HashSet<string> declaredNames = context.DeclaredOptions.Select((BuildOptionDeclaration d) => d.Name).ToHashSet();
		IEnumerable<BuildOptionDeclaration> manualEntries = from d in manualOptionsStore.All
			where !declaredNames.Contains(d.Name)
			select new BuildOptionDeclaration
			{
				Name = d.Name,
				Description = d.Description,
				Type = d.Type,
				DefaultValue = d.DefaultValue,
				EffectiveValue = (context.CachedOptionValues.TryGetValue(d.Name, out string value) ? value : d.EffectiveValue),
				IsAdvanced = d.IsAdvanced,
				AllowedValues = d.AllowedValues,
				IsManual = true
			};
		List<BuildOptionDeclaration> merged = context.DeclaredOptions.Concat(manualEntries).ToList();
		events?.Report(new OrchestratorEvent($"Found {merged.Count} option(s)", OrchestratorEventLevel.Info));
		return merged;
	}

	/// <summary>
	/// Persists option values, merged over whatever is already cached. Merging rather than
	/// replacing matters as soon as two things write the cache: the GUI saves the options its
	/// panel currently shows, and an option the panel is not showing (a third-party one while
	/// the checkbox is off, or the checkbox itself) must not be dropped by that save.
	/// </summary>
	public void SaveOptions(string targetFile, IReadOnlyDictionary<string, string> values)
	{
		OptionCacheStore store = new OptionCacheStore(CachePath(ResolveWorkingDir(targetFile)));
		Dictionary<string, string> merged = new Dictionary<string, string>(store.Load(), StringComparer.Ordinal);
		foreach (var (name, value) in values)
		{
			merged[name] = value;
		}
		store.Save(merged);
	}

	/// <summary>Drops saved values, so those options fall back to their declared defaults.</summary>
	public void ResetOptions(string targetFile, IEnumerable<string> names)
	{
		OptionCacheStore store = new OptionCacheStore(CachePath(ResolveWorkingDir(targetFile)));
		Dictionary<string, string> remaining = new Dictionary<string, string>(store.Load(), StringComparer.Ordinal);
		foreach (string name in names)
		{
			remaining.Remove(name);
		}
		store.Save(remaining);
	}

	public void AddManualOption(string targetFile, BuildOptionDeclaration declaration)
	{
		ManualOptionsStore manualOptionsStore = new ManualOptionsStore(ManualOptionsPath(ResolveWorkingDir(targetFile)));
		manualOptionsStore.Add(declaration);
		manualOptionsStore.Save();
	}

	public void RemoveManualOption(string targetFile, string name)
	{
		ManualOptionsStore manualOptionsStore = new ManualOptionsStore(ManualOptionsPath(ResolveWorkingDir(targetFile)));
		manualOptionsStore.Remove(name);
		manualOptionsStore.Save();
	}

	private static string ResolveWorkingDir(string targetFile)
	{
		return RuleCompiler.ResolveProjectRoot(targetFile);
	}

	private static string CachePath(string workingDir)
	{
		return Path.Combine(workingDir, "Intermediate", "omen-cache.json");
	}

	private static string ManualOptionsPath(string workingDir)
	{
		return Path.Combine(workingDir, "Intermediate", "omen-manual-options.json");
	}
}
