using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Evaluates the restricted CMake subset O3DE's per-package Find modules are written in.
/// This is NOT a CMake interpreter: it covers the package template (variables, the
/// include-guard, config-genexpr library paths, imported targets, interface
/// includes/definitions/link libraries) and throws <see cref="T:Omen.Core.Rules.FindModuleParseException" /> on
/// anything else, per the two-tier design - parse the formulaic bulk, hand-author the tail.
/// </summary>
public sealed class FindModuleParser
{
	private readonly Dictionary<string, string> _vars = new Dictionary<string, string>(StringComparer.Ordinal);

	private readonly Dictionary<string, ThirdPartyTargetDefinition> _targets = new Dictionary<string, ThirdPartyTargetDefinition>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly string _packageDirectory;

	private readonly string _configName;

	private readonly HashSet<string> _parsedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly Func<string, string?>? _locatePackageDirectory;

	private readonly Dictionary<string, (List<string> Parameters, List<(string Name, List<string> Args)> Invocations, int BodyStart, int BodyEnd)> _functions = new Dictionary<string, (List<string>, List<(string, List<string>)>, int, int)>(StringComparer.OrdinalIgnoreCase);

	private string _currentFile = "";

	private int _mutationStamp;

	private static readonly Regex VariableRegex = new Regex("\\$\\{([A-Za-z0-9_\\-]+)\\}", RegexOptions.Compiled);

	/// <param name="locatePackageDirectory">Optional cross-package lookup (target name -&gt; that
	/// target's package directory) for find_package() calls that reach outside this package -
	/// the resolver passes its association-table + store lookup here.</param>
	public FindModuleParser(string packageDirectory, BuildContext context, Func<string, string?>? locatePackageDirectory = null)
	{
		_packageDirectory = packageDirectory;
		_locatePackageDirectory = locatePackageDirectory;
		_configName = MapConfiguration(context.Configuration);
		_vars["CMAKE_CURRENT_LIST_DIR"] = packageDirectory.Replace('\\', '/');
		Dictionary<string, string> vars = _vars;
		vars["CMAKE_SYSTEM_NAME"] = context.Platform switch
		{
			TargetPlatform.Windows => "Windows", 
			TargetPlatform.Linux => "Linux", 
			TargetPlatform.Mac => "Darwin", 
			TargetPlatform.iOS => "iOS", 
			TargetPlatform.Android => "Android", 
			_ => context.Platform.ToString(), 
		};
		bool flag;
		switch (context.Platform)
		{
		case TargetPlatform.Windows:
		case TargetPlatform.Linux:
		case TargetPlatform.Android:
		case TargetPlatform.iOS:
		case TargetPlatform.Mac:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			_vars["PAL_PLATFORM_NAME"] = context.Platform.ToString();
		}
		bool windows = context.Platform == TargetPlatform.Windows;
		_vars["CMAKE_STATIC_LIBRARY_PREFIX"] = (windows ? "" : "lib");
		_vars["CMAKE_STATIC_LIBRARY_SUFFIX"] = (windows ? ".lib" : ".a");
		_vars["CMAKE_SHARED_LIBRARY_PREFIX"] = (windows ? "" : "lib");
		_vars["CMAKE_SHARED_LIBRARY_SUFFIX"] = (windows ? ".dll" : ((context.Platform == TargetPlatform.Mac) ? ".dylib" : ".so"));
		_vars["CMAKE_IMPORT_LIBRARY_SUFFIX"] = (windows ? ".lib" : "");
		_vars["CMAKE_DL_LIBS"] = (windows ? "" : "dl");
		_vars["LY_VERSION_ENGINE_NAME"] = "O3DE";
		if (windows)
		{
			_vars["WIN32"] = "1";
		}
		TargetPlatform platform = context.Platform;
		if (((uint)(platform - 2) <= 3u || platform == TargetPlatform.Mac) ? true : false)
		{
			_vars["UNIX"] = "1";
		}
		platform = context.Platform;
		if ((platform == TargetPlatform.iOS || platform == TargetPlatform.Mac) ? true : false)
		{
			_vars["APPLE"] = "1";
		}
		if (context.Platform == TargetPlatform.Linux)
		{
			_vars["LINUX"] = "1";
		}
		if (context.Platform == TargetPlatform.Android)
		{
			_vars["ANDROID"] = "1";
		}
	}

	/// <summary>O3DE package configs are debug/profile/release; map Omen's onto them.</summary>
	private static string MapConfiguration(BuildConfiguration configuration)
	{
		return configuration switch
		{
			BuildConfiguration.Debug => "debug", 
			BuildConfiguration.Development => "profile", 
			_ => "release", 
		};
	}

	/// <summary>
	/// Parses every Find*.cmake in the package directory and returns the targets they define,
	/// keyed by full name ("3rdParty::Lua"), aliases resolved.
	/// </summary>
	public IReadOnlyDictionary<string, ThirdPartyTargetDefinition> ParsePackage()
	{
		string[] files = Directory.GetFiles(_packageDirectory, "Find*.cmake");
		foreach (string file in files)
		{
			ParseFile(file);
		}
		foreach (var (alias, real) in _aliases)
		{
			if (!_targets.ContainsKey(alias) && _targets.TryGetValue(real, out ThirdPartyTargetDefinition def))
			{
				_targets[alias] = def;
			}
		}
		return _targets;
	}

	private void ParseFile(string path)
	{
		if (!_parsedFiles.Add(Path.GetFullPath(path)))
		{
			return;
		}
		string previousFile = _currentFile;
		string previousListDir = _vars["CMAKE_CURRENT_LIST_DIR"];
		_currentFile = Path.GetFileName(path);
		_vars["CMAKE_CURRENT_LIST_DIR"] = (Path.GetDirectoryName(path) ?? _packageDirectory).Replace('\\', '/');
		try
		{
			ExecuteBlock(Tokenize(File.ReadAllText(path)).ToList(), 0, int.MaxValue);
		}
		finally
		{
			_currentFile = previousFile;
			_vars["CMAKE_CURRENT_LIST_DIR"] = previousListDir;
		}
	}

	/// <summary>
	/// Interprets invocations[start..): if/elseif/else, foreach replay, function/macro
	/// definition skipping. Returns true when a return() was hit (stop the whole file).
	/// </summary>
	private bool ExecuteBlock(List<(string Name, List<string> Args)> invocations, int start, int endExclusive)
	{
		Stack<(bool, bool, bool)> conditionStack = new Stack<(bool, bool, bool)>();
		bool active = true;
		for (int i = start; i < Math.Min(endExclusive, invocations.Count); i++)
		{
			var (name, args) = invocations[i];
			switch (name.ToLowerInvariant())
			{
			case "if":
			{
				bool branch = active && EvaluateCondition(args);
				conditionStack.Push((active, branch, branch));
				active = branch;
				continue;
			}
			case "elseif":
			{
				(bool, bool, bool) tuple3 = conditionStack.Pop();
				bool parent2 = tuple3.Item1;
				bool taken2 = tuple3.Item2;
				bool branch3 = parent2 && !taken2 && EvaluateCondition(args);
				conditionStack.Push((parent2, taken2 | branch3, branch3));
				active = branch3;
				continue;
			}
			case "else":
			{
				(bool, bool, bool) tuple2 = conditionStack.Pop();
				bool parent = tuple2.Item1;
				bool taken = tuple2.Item2;
				bool branch2 = parent && !taken;
				conditionStack.Push((parent, true, branch2));
				active = branch2;
				continue;
			}
			case "endif":
				active = conditionStack.Pop().Item1;
				continue;
			case "macro":
			case "function":
			{
				int end = FindMatchingEnd(invocations, i, name.ToLowerInvariant(), name.Equals("function", StringComparison.OrdinalIgnoreCase) ? "endfunction" : "endmacro");
				if (active && args.Count > 0)
				{
					_functions[args[0]] = (args.Skip(1).ToList(), invocations, i + 1, end);
				}
				i = end;
				continue;
			}
			case "foreach":
			{
				int end2 = FindMatchingEnd(invocations, i, "foreach", "endforeach");
				if (active)
				{
					List<string> expanded = args.Select(Expand).ToList();
					string loopVar = expanded[0];
					IEnumerable<string> enumerable = ((expanded.Count > 2 && expanded[1].Equals("IN", StringComparison.OrdinalIgnoreCase)) ? expanded.Skip(3).SelectMany((string l) => _vars.GetValueOrDefault(l, "").Split(';', StringSplitOptions.RemoveEmptyEntries)) : expanded.Skip(1).SelectMany((string v) => v.Split(';', StringSplitOptions.RemoveEmptyEntries)));
					bool hadValue = _vars.TryGetValue(loopVar, out string saved);
					foreach (string item in enumerable)
					{
						_vars[loopVar] = item;
						if (ExecuteBlock(invocations, i + 1, end2))
						{
							return true;
						}
					}
					if (hadValue)
					{
						_vars[loopVar] = saved;
					}
					else
					{
						_vars.Remove(loopVar);
					}
				}
				i = end2;
				continue;
			}
			}
			if (active)
			{
				if (name.Equals("return", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				Execute(name.ToLowerInvariant(), args.Select(Expand).ToList(), args);
			}
		}
		return false;
	}

	private int FindMatchingEnd(List<(string Name, List<string> Args)> invocations, int start, string open, string close)
	{
		int depth = 0;
		for (int i = start; i < invocations.Count; i++)
		{
			string name = invocations[i].Name;
			if (name.Equals(open, StringComparison.OrdinalIgnoreCase))
			{
				depth++;
			}
			else if (name.Equals(close, StringComparison.OrdinalIgnoreCase) && --depth == 0)
			{
				return i;
			}
		}
		throw new FindModuleParseException($"{_currentFile}: '{open}' with no matching '{close}'.");
	}

	private void Execute(string command, List<string> args, IReadOnlyList<string> rawArgs)
	{
		switch (command)
		{
		case "set":
		{
			IEnumerable<string> values = from a in args.Skip(1)
				where !a.Equals("PARENT_SCOPE", StringComparison.OrdinalIgnoreCase)
				select a;
			if (args.Count >= 1)
			{
				_vars[args[0]] = string.Join(";", values);
			}
			break;
		}
		case "list":
			if (args.Count >= 2 && args[0].Equals("APPEND", StringComparison.OrdinalIgnoreCase))
			{
				string existing = _vars.GetValueOrDefault(args[1], "");
				_vars[args[1]] = string.Join(";", from v in new string[1] { existing }.Concat(args.Skip(2))
					where v.Length > 0
					select v);
				break;
			}
			throw Unsupported("list(" + string.Join(" ", args) + ") - only APPEND is supported");
		case "add_library":
		case "add_executable":
			if (args.Count >= 3 && args[1].Equals("ALIAS", StringComparison.OrdinalIgnoreCase))
			{
				_aliases[args[0]] = args[2];
				break;
			}
			if (!args.Contains("IMPORTED") && !args.Contains("INTERFACE"))
			{
				throw Unsupported(command + "(" + string.Join(" ", args) + ") - only IMPORTED/INTERFACE/ALIAS targets belong in a Find module");
			}
			_targets[args[0]] = new ThirdPartyTargetDefinition
			{
				TargetName = args[0],
				PackageDirectory = _vars["CMAKE_CURRENT_LIST_DIR"].Replace('/', Path.DirectorySeparatorChar),
				IsTool = (command == "add_executable")
			};
			break;
		case "set_target_properties":
		{
			ThirdPartyTargetDefinition target2 = RequireTarget(args[0]);
			int num = args.FindIndex((string a) => a.Equals("PROPERTIES", StringComparison.OrdinalIgnoreCase));
			if (num < 0)
			{
				throw Unsupported("set_target_properties without PROPERTIES");
			}
			for (int i = num + 1; i + 1 < args.Count; i += 2)
			{
				ApplyTargetProperty(target2, args[i], args[i + 1]);
			}
			break;
		}
		case "ly_target_include_system_directories":
			RequireTarget(ArgAfter(args, "TARGET")).IncludeDirectories.AddRange(ListArgsAfter(args, "INTERFACE", "PUBLIC"));
			break;
		case "target_include_directories":
			RequireTarget(args[0]).IncludeDirectories.AddRange(ListArgsAfter(args, "INTERFACE", "PUBLIC"));
			break;
		case "target_compile_definitions":
			RequireTarget(args[0]).Definitions.AddRange(ListArgsAfter(args, "INTERFACE", "PUBLIC"));
			break;
		case "target_link_options":
			RequireTarget(args[0]).LinkerOptions.AddRange(ListArgsAfter(args, "INTERFACE", "PUBLIC"));
			break;
		case "target_link_libraries":
		{
			ThirdPartyTargetDefinition target3 = RequireTarget(args[0]);
			foreach (string item in ListArgsAfter(args, "INTERFACE", "PUBLIC", "PRIVATE"))
			{
				if (item.Contains("::", StringComparison.Ordinal))
				{
					LinkTarget(target3, item);
				}
				else if (item.Contains('/') || item.Contains('\\'))
				{
					target3.Libraries.Add(NormalizePath(item));
				}
				else
				{
					target3.SystemLibraries.Add(item);
				}
			}
			break;
		}
		case "ly_add_target_files":
			RequireTarget(ArgAfter(args, "TARGETS")).RuntimeFiles.AddRange(ListArgsAfter(args, "FILES").Select(NormalizePath));
			break;
		case "find_package":
		{
			string listDir = _vars["CMAKE_CURRENT_LIST_DIR"].Replace('/', Path.DirectorySeparatorChar);
			List<string> searchDirs = new List<string> { listDir };
			string configDir = _vars.GetValueOrDefault(args[0] + "_DIR");
			if (configDir != null && configDir.Length > 0)
			{
				searchDirs.Add(configDir.Replace('/', Path.DirectorySeparatorChar));
			}
			string otherDir = _locatePackageDirectory?.Invoke(args[0]);
			if (otherDir != null)
			{
				searchDirs.Add(otherDir);
			}
			foreach (string candidate in searchDirs.SelectMany((string dir) => new string[3]
			{
				Path.Combine(dir, "Find" + args[0] + ".cmake"),
				Path.Combine(dir, args[0] + "-config.cmake"),
				Path.Combine(dir, args[0] + "Config.cmake")
			}))
			{
				if (File.Exists(candidate))
				{
					ParseFile(candidate);
				}
			}
			break;
		}
		case "include":
		{
			string target = args[0].Replace('/', Path.DirectorySeparatorChar);
			if (File.Exists(target))
			{
				ParseFile(target);
			}
			break;
		}
		default:
		{
			if (_functions.TryGetValue(command, out (List<string>, List<(string, List<string>)>, int, int) fn))
			{
				CallUserFunction(command, fn, args);
				break;
			}
			throw Unsupported($"command '{command}({string.Join(" ", rawArgs)})'");
		}
		case "cmake_policy":
		case "message":
		case "ly_download_associated_package":
		case "ly_pip_install_local_package_editable":
		case "mark_as_advanced":
		case "cmake_minimum_required":
		case "include_guard":
		case "unset":
			break;
		}
		if (command != null)
		{
			int length = command.Length;
			if (length <= 19)
			{
				if (length != 11)
				{
					if (length != 14)
					{
						if (length == 19)
						{
							char c = command[0];
							if (c != 'l')
							{
								if (c == 't' && command == "target_link_options")
								{
									goto IL_0aa5;
								}
							}
							else if (command == "ly_add_target_files")
							{
								goto IL_0aa5;
							}
						}
					}
					else if (command == "add_executable")
					{
						goto IL_0aa5;
					}
				}
				else if (command == "add_library")
				{
					goto IL_0aa5;
				}
			}
			else if (length != 21)
			{
				if (length != 26)
				{
					if (length == 36 && command == "ly_target_include_system_directories")
					{
						goto IL_0aa5;
					}
				}
				else
				{
					char c = command[7];
					if (c != 'c')
					{
						if (c == 'i' && command == "target_include_directories")
						{
							goto IL_0aa5;
						}
					}
					else if (command == "target_compile_definitions")
					{
						goto IL_0aa5;
					}
				}
			}
			else
			{
				char c = command[0];
				if (c != 's')
				{
					if (c == 't' && command == "target_link_libraries")
					{
						goto IL_0aa5;
					}
				}
				else if (command == "set_target_properties")
				{
					goto IL_0aa5;
				}
			}
		}
		bool flag = false;
		goto IL_0aad;
		IL_0aa5:
		flag = true;
		goto IL_0aad;
		IL_0aad:
		if (flag)
		{
			_mutationStamp++;
		}
	}

	/// <summary>
	/// Replays a user-defined function/macro body with positional parameters bound. A body
	/// that hits an unsupported construct BEFORE touching any target is treated as an inert
	/// probe (png's check_if_libm_required pattern - configure-time feature checks whose
	/// no-op outcome is the right answer for a prebuilt package); one that fails after
	/// mutating a target rethrows, because swallowing that would silently lose real data.
	/// </summary>
	private void CallUserFunction(string name, (List<string> Parameters, List<(string Name, List<string> Args)> Invocations, int BodyStart, int BodyEnd) fn, List<string> args)
	{
		Dictionary<string, string> saved = new Dictionary<string, string>();
		for (int p = 0; p < fn.Parameters.Count; p++)
		{
			saved[fn.Parameters[p]] = _vars.GetValueOrDefault(fn.Parameters[p]);
			_vars[fn.Parameters[p]] = ((p < args.Count) ? args[p] : "");
		}
		int stampBefore = _mutationStamp;
		try
		{
			ExecuteBlock(fn.Invocations, fn.BodyStart, fn.BodyEnd);
		}
		catch (FindModuleParseException) when (_mutationStamp == stampBefore)
		{
		}
		finally
		{
			foreach (var (key, value) in saved)
			{
				if (value == null)
				{
					_vars.Remove(key);
				}
				else
				{
					_vars[key] = value;
				}
			}
		}
	}

	private void ApplyTargetProperty(ThirdPartyTargetDefinition target, string property, string value)
	{
		if (property.Equals("IMPORTED_LOCATION", StringComparison.OrdinalIgnoreCase))
		{
			if (value.Length > 0)
			{
				(target.IsTool ? target.RuntimeFiles : target.Libraries).Add(NormalizePath(value));
			}
			return;
		}
		string configSuffix = "IMPORTED_LOCATION_";
		if (property.StartsWith(configSuffix, StringComparison.OrdinalIgnoreCase))
		{
			if (property.Substring(configSuffix.Length).Equals(_configName, StringComparison.OrdinalIgnoreCase) && value.Length > 0)
			{
				(target.IsTool ? target.RuntimeFiles : target.Libraries).Add(NormalizePath(value));
			}
		}
		else if (property.Equals("INTERFACE_IMPORTED_LOCATION", StringComparison.OrdinalIgnoreCase))
		{
			target.RuntimeFiles.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(NormalizePath));
		}
		else if (property.Equals("INTERFACE_COMPILE_DEFINITIONS", StringComparison.OrdinalIgnoreCase))
		{
			target.Definitions.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries));
		}
		else if (property.Equals("INTERFACE_INCLUDE_DIRECTORIES", StringComparison.OrdinalIgnoreCase))
		{
			target.IncludeDirectories.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries));
		}
		else if (!property.StartsWith("IMPORTED_", StringComparison.OrdinalIgnoreCase))
		{
			throw Unsupported("target property '" + property + "'");
		}
	}

	private bool EvaluateCondition(IReadOnlyList<string> tokens)
	{
		if (tokens.Any((string t) => (t == "(" || t == ")") ? true : false))
		{
			throw Unsupported("parenthesized condition (" + string.Join(" ", tokens) + ")");
		}
		return SplitOn(tokens, "OR").Any((IReadOnlyList<string> part) => SplitOn(part, "AND").All(EvaluateSimple));
	}

	private bool EvaluateSimple(IReadOnlyList<string> tokens)
	{
		if (tokens.Count == 0)
		{
			return false;
		}
		if (tokens[0].Equals("NOT", StringComparison.OrdinalIgnoreCase))
		{
			return !EvaluateSimple(tokens.Skip(1).ToList());
		}
		if (tokens.Count == 1)
		{
			string expanded = Expand(tokens[0]);
			string value;
			if (_vars.TryGetValue(expanded, out string varValue))
			{
				value = varValue;
			}
			else
			{
				value = (tokens[0].Contains("${") ? "" : expanded);
			}
			if (!tokens[0].Contains("${") && !_vars.ContainsKey(expanded) && !expanded.Equals("TRUE", StringComparison.OrdinalIgnoreCase) && !expanded.Equals("ON", StringComparison.OrdinalIgnoreCase) && !expanded.Equals("YES", StringComparison.OrdinalIgnoreCase) && expanded != "1")
			{
				value = "";
			}
			if (value.Length > 0 && !value.Equals("FALSE", StringComparison.OrdinalIgnoreCase) && !value.Equals("OFF", StringComparison.OrdinalIgnoreCase) && value != "0")
			{
				return !value.EndsWith("-NOTFOUND", StringComparison.Ordinal);
			}
			return false;
		}
		if (tokens.Count == 2)
		{
			string keyword = tokens[0].ToUpperInvariant();
			string operand = Expand(tokens[1]);
			return keyword switch
			{
				"TARGET" => _targets.ContainsKey(operand) || _aliases.ContainsKey(operand), 
				"DEFINED" => _vars.ContainsKey(Expand(tokens[1])) || _vars.ContainsKey(tokens[1]), 
				"COMMAND" => operand.StartsWith("ly_", StringComparison.Ordinal), 
				"EXISTS" => File.Exists(operand) || Directory.Exists(operand), 
				_ => throw Unsupported("condition '" + string.Join(" ", tokens) + "'"), 
			};
		}
		if (tokens.Count == 3 && tokens[1].Equals("STREQUAL", StringComparison.OrdinalIgnoreCase))
		{
			return Expand(tokens[0]).Equals(Expand(tokens[2]), StringComparison.Ordinal);
		}
		throw Unsupported("condition '" + string.Join(" ", tokens) + "'");
	}

	private static List<IReadOnlyList<string>> SplitOn(IReadOnlyList<string> tokens, string keyword)
	{
		List<IReadOnlyList<string>> parts = new List<IReadOnlyList<string>>();
		List<string> current = new List<string>();
		foreach (string token in tokens)
		{
			if (token.Equals(keyword, StringComparison.OrdinalIgnoreCase))
			{
				parts.Add(current);
				current = new List<string>();
			}
			else
			{
				current.Add(token);
			}
		}
		parts.Add(current);
		return parts;
	}

	private string Expand(string value)
	{
		for (int pass = 0; pass < 10; pass++)
		{
			if (!value.Contains("${"))
			{
				break;
			}
			value = VariableRegex.Replace(value, (Match m) => _vars.GetValueOrDefault(m.Groups[1].Value, ""));
		}
		if (!value.Contains("$<"))
		{
			return value;
		}
		return ExpandGeneratorExpressions(value);
	}

	private string ExpandGeneratorExpressions(string value)
	{
		for (int pass = 0; pass < 10; pass++)
		{
			if (!value.Contains("$<"))
			{
				break;
			}
			int start = value.LastIndexOf("$<", StringComparison.Ordinal);
			int end = FindGenexprEnd(value, start);
			string text = value;
			int num = start + 2;
			string inner = text.Substring(num, end - num);
			value = value.Substring(0, start) + EvaluateGenexpr(inner) + value.Substring(end + 1);
		}
		if (value.Contains("$<"))
		{
			throw Unsupported("generator expression nest too deep in '" + value + "'");
		}
		return value;
	}

	private static int FindGenexprEnd(string value, int start)
	{
		int depth = 0;
		for (int i = start; i < value.Length; i++)
		{
			if (i + 1 < value.Length && value[i] == '$' && value[i + 1] == '<')
			{
				depth++;
				i++;
			}
			else if (value[i] == '>' && --depth == 0)
			{
				return i;
			}
		}
		throw new FindModuleParseException("Unterminated generator expression in '" + value + "'");
	}

	private string EvaluateGenexpr(string body)
	{
		if (body == "CONFIG")
		{
			return _configName;
		}
		if (body.StartsWith("CONFIG:", StringComparison.Ordinal))
		{
			if (!body.Substring("CONFIG:".Length).Split(',').Any((string c) => c.Equals(_configName, StringComparison.OrdinalIgnoreCase)))
			{
				return "0";
			}
			return "1";
		}
		if (body.StartsWith("NOT:", StringComparison.Ordinal))
		{
			if (!(body.Substring("NOT:".Length) == "1"))
			{
				return "1";
			}
			return "0";
		}
		if (body.StartsWith("BOOL:", StringComparison.Ordinal))
		{
			if (body.Substring("BOOL:".Length).Length <= 0 || !(body.Substring("BOOL:".Length) != "0"))
			{
				return "0";
			}
			return "1";
		}
		if (body.StartsWith("IF:", StringComparison.Ordinal))
		{
			string[] parts = body.Substring("IF:".Length).Split(',', 3);
			if (parts.Length != 3)
			{
				throw Unsupported("$<IF:" + body + ">");
			}
			if (!(parts[0] == "1"))
			{
				return parts[2];
			}
			return parts[1];
		}
		if (body.StartsWith("LOWER_CASE:", StringComparison.Ordinal))
		{
			return body.Substring("LOWER_CASE:".Length).ToLowerInvariant();
		}
		if (body.StartsWith("CXX_COMPILER_ID:", StringComparison.Ordinal))
		{
			string assumed = (_vars.ContainsKey("WIN32") ? "MSVC" : "Clang");
			if (!body.Substring("CXX_COMPILER_ID:".Length).Split(',').Any((string id) => id.Equals(assumed, StringComparison.OrdinalIgnoreCase)))
			{
				return "0";
			}
			return "1";
		}
		int colon = body.IndexOf(':');
		if (colon == 1 && (body[0] == '1' || body[0] == '0'))
		{
			if (body[0] != '1')
			{
				return "";
			}
			return body.Substring(colon + 1);
		}
		throw Unsupported("generator expression '$<" + body + ">'");
	}

	private IEnumerable<(string Name, List<string> Args)> Tokenize(string content)
	{
		int i = 0;
		while (i < content.Length)
		{
			char c = content[i];
			if (c == '#')
			{
				for (; i < content.Length && content[i] != '\n'; i++)
				{
				}
				continue;
			}
			if (char.IsWhiteSpace(c))
			{
				i++;
				continue;
			}
			int nameStart = i;
			for (; i < content.Length && (char.IsLetterOrDigit(content[i]) || content[i] == '_'); i++)
			{
			}
			int num = nameStart;
			string name = content.Substring(num, i - num);
			for (; i < content.Length && char.IsWhiteSpace(content[i]); i++)
			{
			}
			if (name.Length == 0 || i >= content.Length || content[i] != '(')
			{
				FindModuleParser findModuleParser = this;
				num = nameStart;
				throw findModuleParser.Unsupported("unrecognized syntax near '" + content.Substring(num, Math.Min(content.Length, nameStart + 40) - num) + "'");
			}
			i++;
			int depth = 1;
			List<string> args = new List<string>();
			StringBuilder current = new StringBuilder();
			bool inQuotes = false;
			for (; i < content.Length; i++)
			{
				if (depth <= 0)
				{
					break;
				}
				char ch = content[i];
				if (inQuotes)
				{
					if (ch == '"')
					{
						inQuotes = false;
					}
					else
					{
						current.Append(ch);
					}
					continue;
				}
				switch (ch)
				{
				case '"':
					inQuotes = true;
					break;
				case '#':
					for (; i < content.Length && content[i] != '\n'; i++)
					{
					}
					break;
				case '(':
					depth++;
					current.Append(ch);
					break;
				case ')':
					depth--;
					if (depth > 0)
					{
						current.Append(ch);
					}
					break;
				default:
					if (char.IsWhiteSpace(ch))
					{
						Flush(args, current);
					}
					else
					{
						current.Append(ch);
					}
					break;
				}
			}
			Flush(args, current);
			yield return (Name: name, Args: args);
		}
		static void Flush(List<string> list, StringBuilder stringBuilder)
		{
			if (stringBuilder.Length > 0)
			{
				list.Add(stringBuilder.ToString());
				stringBuilder.Clear();
			}
		}
	}

	/// <summary>
	/// An item with "::" in a target_link_libraries call is a CMake target, never a library file
	/// - passing it through as one put "ZLIB::ZLIB" on the link line as ZLIB::ZLIB.lib.
	///
	/// Two kinds, and they resolve differently. A target from another package
	/// (FindFreetype and FindTIFF both find_package(ZLIB) and link ZLIB::ZLIB, which that
	/// package aliases onto 3rdParty::ZLIB) becomes a dependency for the resolver to synthesize
	/// on its own. A target declared in this same package (FindOpenSSL's OpenSSL::SSL links
	/// OpenSSL::Crypto) cannot: no package is named after it, so nothing downstream could ever
	/// resolve it, and its contents are folded in here instead. That is what INTERFACE linking
	/// means, and by the time one sibling links another the sibling is fully populated.
	/// </summary>
	private void LinkTarget(ThirdPartyTargetDefinition target, string item)
	{
		string canonical = (_aliases.TryGetValue(item, out string real) ? real : item);
		if (canonical.StartsWith("3rdParty::", StringComparison.OrdinalIgnoreCase))
		{
			target.Dependencies.Add(canonical);
			return;
		}
		if (!_targets.TryGetValue(canonical, out ThirdPartyTargetDefinition sibling))
		{
			target.Dependencies.Add(item);
			return;
		}
		target.IncludeDirectories.AddRange(sibling.IncludeDirectories);
		target.Libraries.AddRange(sibling.Libraries);
		target.Definitions.AddRange(sibling.Definitions);
		target.SystemLibraries.AddRange(sibling.SystemLibraries);
		target.LinkerOptions.AddRange(sibling.LinkerOptions);
		target.RuntimeFiles.AddRange(sibling.RuntimeFiles);
		target.Dependencies.AddRange(sibling.Dependencies);
	}

	private ThirdPartyTargetDefinition RequireTarget(string name)
	{
		if (_aliases.TryGetValue(name, out string real))
		{
			name = real;
		}
		if (_targets.TryGetValue(name, out ThirdPartyTargetDefinition target))
		{
			return target;
		}
		throw Unsupported("reference to undeclared target '" + name + "'");
	}

	private static string ArgAfter(List<string> args, string keyword)
	{
		int index = args.FindIndex((string a) => a.Equals(keyword, StringComparison.OrdinalIgnoreCase));
		if (index < 0 || index + 1 >= args.Count)
		{
			throw new FindModuleParseException($"Expected '{keyword} <value>' in ({string.Join(" ", args)})");
		}
		return args[index + 1];
	}

	/// <summary>Args after the first of the given scope keywords, up to the next keyword.</summary>
	private static IEnumerable<string> ListArgsAfter(List<string> args, params string[] scopeKeywords)
	{
		HashSet<string> keywords = new HashSet<string>(new string[] { "INTERFACE", "PUBLIC", "PRIVATE", "SYSTEM", "TARGET", "TARGETS", "FILES", "OUTPUT_SUBDIRECTORY" }, StringComparer.OrdinalIgnoreCase);
		bool collecting = false;
		foreach (string arg in args.Skip(1))
		{
			if (keywords.Contains(arg))
			{
				collecting = scopeKeywords.Contains(arg, StringComparer.OrdinalIgnoreCase);
			}
			else if (collecting && arg.Length > 0)
			{
				string[] array = arg.Split(';', StringSplitOptions.RemoveEmptyEntries);
				for (int i = 0; i < array.Length; i++)
				{
					yield return array[i];
				}
			}
		}
	}

	private static string NormalizePath(string path)
	{
		return path.Replace('/', Path.DirectorySeparatorChar);
	}

	private FindModuleParseException Unsupported(string what)
	{
		return new FindModuleParseException($"{_currentFile} ({Path.GetFileName(_packageDirectory)}): unsupported {what}. This package is outside the supported Find-module template - hand-write a module named \"3rdParty::<Target>\" for it (a ModuleRules with the package's include paths and libraries); the resolver prefers an existing module over parsing.");
	}
}
