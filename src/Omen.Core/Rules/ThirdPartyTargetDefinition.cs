using System.Collections.Generic;

namespace Omen.Core.Rules;

/// <summary>
/// What one `3rdParty::X` target amounts to once its package's Find&lt;X&gt;.cmake has been
/// evaluated: real include directories, prebuilt libraries for the current configuration,
/// interface definitions, and references to other 3rdParty targets.
/// </summary>
public sealed class ThirdPartyTargetDefinition
{
	public required string TargetName { get; init; }

	public required string PackageDirectory { get; init; }

	/// <summary>True for an imported executable (a build tool, not a library).</summary>
	public bool IsTool { get; init; }

	public List<string> IncludeDirectories { get; } = new List<string>();

	public List<string> Libraries { get; } = new List<string>();

	public List<string> Definitions { get; } = new List<string>();

	public List<string> SystemLibraries { get; } = new List<string>();

	public List<string> LinkerOptions { get; } = new List<string>();

	/// <summary>Other `3rdParty::Y` targets this one links INTERFACE against.</summary>
	public List<string> Dependencies { get; } = new List<string>();

	/// <summary>Files (DLLs, tools) `ly_add_target_files` stages next to consumers. Recorded;
	/// staging is a follow-up.</summary>
	public List<string> RuntimeFiles { get; } = new List<string>();
}
