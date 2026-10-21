using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Omen.Core.Configuration;
using Omen.Core.Options;
using Omen.Core.Qt;

namespace Omen.Core.Rules;

/// <summary>
/// Represents compiled rule files.
/// </summary>
public sealed class CompiledRules
{
	private readonly Assembly _assembly;

	/// <summary>Absolute directory of the rule file each rule type was declared in, keyed by
	/// type full name. Pushed onto the BuildContext before anything is instantiated.</summary>
	private readonly IReadOnlyDictionary<string, string> _ruleFilePaths;

	public IReadOnlyList<string> ModuleFiles { get; }

	public IReadOnlyList<string> TargetFiles { get; }

	public IReadOnlyList<string> PluginFiles { get; }

	/// <summary>
	/// The root project definition (see OmenProjectRules), or null when the project had no
	/// `*.omenproject.cs` file and RuleCompiler fell back to globbing the whole tree.
	/// </summary>
	public OmenProjectRules? Project { get; }

	/// <summary>
	/// The compiled rules assembly, exposed for callers that need to reason about its types
	/// without an instance - e.g. CompiledRules.FindTargetType() below, and the CLI's --dim
	/// dimension-name lookup (DimensionLookup.FindDimensionType), which has no other way to
	/// reach it. Public, not internal: Omen.Executors and Omen.CLI are separate assemblies
	/// from Omen.Core with no InternalsVisibleTo configured between them.
	/// </summary>
	public Assembly Assembly => _assembly;

	internal CompiledRules(Assembly assembly, IReadOnlyList<string> moduleFiles, IReadOnlyList<string> targetFiles, IReadOnlyList<string> pluginFiles, OmenProjectRules? project = null, IReadOnlyDictionary<string, string>? ruleFilePaths = null)
	{
		_assembly = assembly;
		ModuleFiles = moduleFiles;
		TargetFiles = targetFiles;
		PluginFiles = pluginFiles;
		Project = project;
		_ruleFilePaths = ruleFilePaths ?? new Dictionary<string, string>();
	}

	/// <summary>
	/// Creates instances of every hand-written ModuleRules in the compiled assembly,
	/// plus one synthesized ModuleRules per flavor of every discovered PluginRules.
	/// </summary>
	/// <summary>
	/// Puts the project definition's declared options on the context, so the GUI's Options
	/// panel lists them and any rule file can read them through BuildContext.Option. Idempotent:
	/// a context passed to several Create* calls collects them once.
	/// </summary>
	private void MergeProjectOptions(BuildContext context)
	{
		if (Project == null)
		{
			return;
		}
		foreach (BuildOptionDeclaration option in Project.Options)
		{
			if (!context.DeclaredOptions.Any((BuildOptionDeclaration o) => o.Name == option.Name))
			{
				context.DeclaredOptions.Add(option);
			}
		}
	}

	public IReadOnlyList<ModuleRules> CreateModuleRules(BuildContext context)
	{
		MergeProjectOptions(context);
		Type moduleType = typeof(ModuleRules);
		List<ModuleRules> rules = new List<ModuleRules>();
		Type[] types = _assembly.GetTypes();
		foreach (Type type in types)
		{
			if (!type.IsAbstract && moduleType.IsAssignableFrom(type) && !typeof(PluginFlavorModuleRules).IsAssignableFrom(type))
			{
				ConstructorInfo constructor = type.GetConstructor(new Type[1] { typeof(BuildContext) });
				if (constructor == null)
				{
					throw new InvalidOperationException("Module rule type '" + type.Name + "' must have a constructor that takes BuildContext.");
				}
				ModuleRules instance = Instantiate<ModuleRules>(constructor, type, context);
				if (TraitGating.IsSatisfied(instance.RequiredTraits, context))
				{
					rules.Add(instance);
				}
			}
		}
		IReadOnlyList<PluginRules> allPlugins = CreatePluginRules(context);
		foreach (PluginRules plugin in allPlugins)
		{
			foreach (PluginFlavor flavor in plugin.Flavors.Values.Concat(plugin.CustomFlavors.Values))
			{
				if (TraitGating.IsSatisfied(flavor.RequiredTraits, context))
				{
					rules.Add(new PluginFlavorModuleRules(context, plugin, flavor, allPlugins));
				}
			}
		}
		rules.AddRange(ThirdPartyPackageResolver.Synthesize(context, rules));
		QtModuleEnvironment.Apply(context, rules);
		return rules;
	}

	/// <summary>
	/// Creates instances of all TargetRules in the compiled assembly.
	/// </summary>
	public IReadOnlyList<TargetRules> CreateTargetRules(BuildContext context)
	{
		MergeProjectOptions(context);
		Type targetType = typeof(TargetRules);
		List<TargetRules> rules = new List<TargetRules>();
		Type[] types = _assembly.GetTypes();
		foreach (Type type in types)
		{
			if (!type.IsAbstract && targetType.IsAssignableFrom(type))
			{
				ConstructorInfo constructor = type.GetConstructor(new Type[1] { typeof(BuildContext) });
				if (constructor == null)
				{
					throw new InvalidOperationException("Target rule type '" + type.Name + "' must have a constructor that takes BuildContext.");
				}
				TargetRules instance = Instantiate<TargetRules>(constructor, type, context);
				rules.Add(instance);
			}
		}
		return rules;
	}

	/// <summary>
	/// Creates instances of all PluginRules in the compiled assembly.
	/// </summary>
	public IReadOnlyList<PluginRules> CreatePluginRules(BuildContext context)
	{
		MergeProjectOptions(context);
		Type pluginType = typeof(PluginRules);
		List<PluginRules> rules = new List<PluginRules>();
		Type[] types = _assembly.GetTypes();
		foreach (Type type in types)
		{
			if (!type.IsAbstract && pluginType.IsAssignableFrom(type))
			{
				ConstructorInfo constructor = type.GetConstructor(new Type[1] { typeof(BuildContext) });
				if (constructor == null)
				{
					throw new InvalidOperationException("Plugin rule type '" + type.Name + "' must have a constructor that takes BuildContext.");
				}
				rules.Add(Instantiate<PluginRules>(constructor, type, context));
			}
		}
		return rules;
	}

	/// <summary>
	/// Reflection wraps whatever a rule constructor throws in a TargetInvocationException,
	/// whose own message is the useless "Exception has been thrown by the target of an
	/// invocation." - which was the entire error a user got when one rule file out of several
	/// hundred threw, with no hint which one. Rethrow the real failure, named against the rule
	/// type that produced it.
	/// </summary>
	private T Instantiate<T>(ConstructorInfo constructor, Type type, BuildContext context)
	{
		context.RuleFilePaths = _ruleFilePaths;
		try
		{
			T val = (T)constructor.Invoke(new object[1] { context });
			AbsolutizeSourceDirectories(val, type, context);
			return val;
		}
		catch (TargetInvocationException ex) when (ex.InnerException != null)
		{
			throw new RuleInstantiationException("'" + type.Name + "' threw while its rules were being evaluated: " + ex.InnerException.Message, ex.InnerException);
		}
	}

	/// <summary>
	/// Rewrites a freshly built rule's SourceDirectory (and each plugin flavor's) to an absolute
	/// path resolved against the rule file that declared it. Done here rather than at every
	/// consumer because there are ten of them, all shaped Path.Combine(projectRoot, ...) - and
	/// Path.Combine already returns the second argument unchanged when it is rooted, so making
	/// the value absolute once fixes all ten without touching any of them.
	/// </summary>
	private static void AbsolutizeSourceDirectories<T>(T instance, Type type, BuildContext context)
	{
		if (!(instance is ModuleRules module))
		{
			if (!(instance is PluginRules plugin))
			{
				return;
			}
			{
				foreach (PluginFlavor flavor in plugin.Flavors.Values.Concat(plugin.CustomFlavors.Values))
				{
					flavor.SourceDirectory = context.ResolveRulePath(type, flavor.SourceDirectory ?? Path.Combine("Source", plugin.Name));
				}
				return;
			}
		}
		module.SourceDirectory = context.ResolveRulePath(type, module.SourceDirectory ?? Path.Combine("Source", module.Name));
		AbsolutizeLibraries(module.PublicLibraries, module.SourceDirectory);
		AbsolutizeLibraries(module.PrivateLibraries, module.SourceDirectory);
	}

	private static void AbsolutizeLibraries(List<string> libraries, string? sourceDirectory)
	{
		if (sourceDirectory == null)
		{
			return;
		}
		for (int i = 0; i < libraries.Count; i++)
		{
			if (!Path.IsPathRooted(libraries[i]))
			{
				libraries[i] = Path.GetFullPath(Path.Combine(sourceDirectory, libraries[i]));
			}
		}
	}

	/// <summary>
	/// Every non-abstract TargetRules subclass in the compiled assembly, optionally narrowed to
	/// the ones declared in <paramref name="declaringFile" />. Reflection over the Type only - no
	/// BuildContext, no instance construction - which is what makes reading SupportedTargets
	/// possible before any context exists to build one.
	///
	/// Narrowing matters as soon as a project is bigger than one target: an engine tree with
	/// seven `*Pilot.target.cs` files compiles all seven into one assembly, and "the first type
	/// reflection happens to return" is not a target the user asked for. Callers that know which
	/// target file the user named pass it.
	/// </summary>
	public IReadOnlyList<Type> FindTargetTypes(string? declaringFile = null)
	{
		Type targetType = typeof(TargetRules);
		IEnumerable<Type> candidates = from t in _assembly.GetTypes()
			where !t.IsAbstract && targetType.IsAssignableFrom(t)
			select t;
		if (declaringFile == null)
		{
			return candidates.ToList();
		}
		string wanted = Path.GetFullPath(declaringFile);
		return candidates.Where((Type t) => _ruleFilePaths.TryGetValue(t.FullName ?? t.Name, out string value) && string.Equals(value, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
	}

	/// <summary>
	/// The first non-abstract TargetRules subclass, for callers that genuinely do not care which
	/// (project-file generation, the GUI's matrix panel). A build must not use this - see
	/// <see cref="M:Omen.Core.Rules.CompiledRules.FindTargetTypes(System.String)" />.
	/// </summary>
	public Type? FindTargetType(string? declaringFile = null)
	{
		return FindTargetTypes(declaringFile).FirstOrDefault();
	}

	/// <summary>
	/// Reads a rule type's optional `static TargetMatrix SupportedTargets` property without
	/// constructing an instance. Works for TargetRules, PluginRules, and ModuleRules subclasses
	/// alike (any type with a static property of that exact name and type). Returns null if the
	/// type doesn't declare one - callers treat null as "no matrix, use today's single-context
	/// behavior."
	/// </summary>
	public TargetMatrix? GetSupportedTargets(Type ruleType)
	{
		PropertyInfo property = ruleType.GetProperty("SupportedTargets", BindingFlags.Static | BindingFlags.Public);
		if (property == null || property.PropertyType != typeof(TargetMatrix))
		{
			return null;
		}
		try
		{
			return (TargetMatrix)property.GetValue(null);
		}
		catch (TargetInvocationException ex) when (ex.InnerException != null)
		{
			throw ex.InnerException;
		}
	}

	/// <summary>
	/// Gets a specific target by name.
	/// </summary>
	public TargetRules? GetTarget(string name, BuildContext context)
	{
		return CreateTargetRules(context).FirstOrDefault((TargetRules t) => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Gets a specific module by name.
	/// </summary>
	public ModuleRules? GetModule(string name, BuildContext context)
	{
		return CreateModuleRules(context).FirstOrDefault((ModuleRules m) => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
	}
}
