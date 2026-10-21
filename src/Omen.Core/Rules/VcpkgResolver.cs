using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Resolves <c>Vcpkg::&lt;port&gt;</c> dependencies against a vcpkg installed tree - no CMake
/// integration needed, because vcpkg's own install database says everything required:
/// <c>installed/vcpkg/info/&lt;port&gt;_&lt;version&gt;_&lt;triplet&gt;.list</c> lists every file the
/// port installed (libs, DLLs, headers), and <c>installed/vcpkg/status</c> carries each
/// port's dependency list. The installed root is found from, in order: the project's
/// manifest-mode <c>vcpkg_installed/</c> directory, then <c>%VCPKG_ROOT%/installed</c>.
/// </summary>
public static class VcpkgResolver
{
	public const string Prefix = "Vcpkg::";

	public static string? FindInstalledRoot(string projectRoot)
	{
		string manifestMode = Path.Combine(projectRoot, "vcpkg_installed");
		if (Directory.Exists(manifestMode))
		{
			return manifestMode;
		}
		string vcpkgRoot = Environment.GetEnvironmentVariable("VCPKG_ROOT");
		if (vcpkgRoot != null && vcpkgRoot.Length > 0)
		{
			string classic = Path.Combine(vcpkgRoot, "installed");
			if (Directory.Exists(classic))
			{
				return classic;
			}
		}
		return null;
	}

	/// <summary>VCPKG_DEFAULT_TRIPLET wins; otherwise vcpkg's own default per platform/arch.</summary>
	public static string Triplet(BuildContext context)
	{
		string env = Environment.GetEnvironmentVariable("VCPKG_DEFAULT_TRIPLET");
		if (env != null && env.Length > 0)
		{
			return env;
		}
		string arch = context.Architecture switch
		{
			TargetArchitecture.X86 => "x86", 
			TargetArchitecture.ARM64 => "arm64", 
			_ => "x64", 
		};
		switch (context.Platform)
		{
		case TargetPlatform.Windows:
			return arch + "-windows";
		case TargetPlatform.Linux:
		case TargetPlatform.FreeBSD:
			return arch + "-linux";
		case TargetPlatform.Mac:
			return arch + "-osx";
		case TargetPlatform.Android:
			return arch + "-android";
		case TargetPlatform.iOS:
			return "arm64-ios";
		default:
			return arch + "-" + context.Platform.ToString().ToLowerInvariant();
		}
	}

	/// <summary>
	/// Resolves one port to a target definition, or null when the port isn't installed for
	/// the triplet (left to the unresolved-dependency warning, same as a missing O3DE
	/// package). <paramref name="dependsByPort" /> is the parsed status database - get it once
	/// per batch from <see cref="M:Omen.Core.Rules.VcpkgResolver.ReadStatusDependencies(System.String)" />.
	/// </summary>
	public static ThirdPartyTargetDefinition? Resolve(string name, BuildContext context, string installedRoot, IReadOnlyDictionary<string, List<string>> dependsByPort)
	{
		string port = name.Substring("Vcpkg::".Length);
		string triplet = Triplet(context);
		string infoDir = Path.Combine(installedRoot, "vcpkg", "info");
		if (!Directory.Exists(infoDir))
		{
			return null;
		}
		string listFile = Directory.GetFiles(infoDir, port + "_*_" + triplet + ".list").FirstOrDefault();
		if (listFile == null)
		{
			return null;
		}
		bool debug = context.Configuration == BuildConfiguration.Debug;
		string libPrefix = triplet + "/" + (debug ? "debug/lib/" : "lib/");
		string binPrefix = triplet + "/" + (debug ? "debug/bin/" : "bin/");
		ThirdPartyTargetDefinition definition = new ThirdPartyTargetDefinition
		{
			TargetName = name,
			PackageDirectory = Path.Combine(installedRoot, triplet)
		};
		definition.IncludeDirectories.Add(Path.Combine(installedRoot, triplet, "include"));
		foreach (string item in File.ReadLines(listFile))
		{
			string entry = item.Trim();
			if (entry.StartsWith(libPrefix, StringComparison.OrdinalIgnoreCase) && (entry.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".a", StringComparison.OrdinalIgnoreCase)))
			{
				definition.Libraries.Add(Path.Combine(installedRoot, entry.Replace('/', Path.DirectorySeparatorChar)));
			}
			else if (entry.StartsWith(binPrefix, StringComparison.OrdinalIgnoreCase) && (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)))
			{
				definition.RuntimeFiles.Add(Path.Combine(installedRoot, entry.Replace('/', Path.DirectorySeparatorChar)));
			}
		}
		if (dependsByPort.TryGetValue(port, out List<string> depends))
		{
			definition.Dependencies.AddRange(from d in depends
				where !d.StartsWith("vcpkg-", StringComparison.OrdinalIgnoreCase)
				where !d.Equals(port, StringComparison.OrdinalIgnoreCase)
				select "Vcpkg::" + d);
		}
		return definition;
	}

	/// <summary>
	/// Parses <c>installed/vcpkg/status</c> (Debian-control-style stanzas) into port -&gt;
	/// dependency ports, merging a port's feature stanzas into the same entry. Version
	/// constraints and triplet suffixes on Depends entries are stripped - only the port name
	/// matters for resolution.
	/// </summary>
	public static IReadOnlyDictionary<string, List<string>> ReadStatusDependencies(string installedRoot)
	{
		Dictionary<string, List<string>> result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		string statusPath = Path.Combine(installedRoot, "vcpkg", "status");
		if (!File.Exists(statusPath))
		{
			return result;
		}
		string currentPort = null;
		foreach (string line in File.ReadLines(statusPath))
		{
			if (line.StartsWith("Package:", StringComparison.OrdinalIgnoreCase))
			{
				currentPort = line.Substring("Package:".Length).Trim();
				if (!result.ContainsKey(currentPort))
				{
					result[currentPort] = new List<string>();
				}
			}
			else if (currentPort != null && line.StartsWith("Depends:", StringComparison.OrdinalIgnoreCase))
			{
				string[] array = line.Substring("Depends:".Length).Split(',');
				for (int i = 0; i < array.Length; i++)
				{
					string dep = array[i].Trim().Split(' ', '(', '[', ':')[0];
					if (dep.Length > 0 && !result[currentPort].Contains(dep, StringComparer.OrdinalIgnoreCase))
					{
						result[currentPort].Add(dep);
					}
				}
			}
			else if (line.Length == 0)
			{
				currentPort = null;
			}
		}
		return result;
	}
}
