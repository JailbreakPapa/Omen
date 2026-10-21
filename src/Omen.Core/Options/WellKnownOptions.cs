namespace Omen.Core.Options;

/// <summary>
/// Option names Omen itself knows about, so a project definition, the GUI and the CLI agree
/// on the spelling without each hardcoding a string.
/// </summary>
public static class WellKnownOptions
{
	/// <summary>
	/// Whether a project also declares the options its vendored third-party libraries define
	/// (hundreds of them in an engine tree: MINIAUDIO_*, BENCHMARK_*, ASSIMP_*...). A project
	/// definition gates those declarations on this; the GUI has a "Third-party" checkbox and
	/// the CLI an `omen configure --third-party` switch, both of which just set it.
	/// </summary>
	public const string ThirdPartyOptions = "OMEN_THIRDPARTY_OPTIONS";
}
