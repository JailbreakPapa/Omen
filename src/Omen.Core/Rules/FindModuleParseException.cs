using System;

namespace Omen.Core.Rules;

/// <summary>
/// A Find&lt;X&gt;.cmake used a construct outside the supported O3DE package-template subset.
/// Deliberately loud: the alternative is a silently wrong translation. The fix is always the
/// same - hand-write a module named "3rdParty::X" (the override tier), which the resolver
/// prefers over parsing.
/// </summary>
public sealed class FindModuleParseException : InvalidOperationException
{
	/// <summary>
	/// A Find&lt;X&gt;.cmake used a construct outside the supported O3DE package-template subset.
	/// Deliberately loud: the alternative is a silently wrong translation. The fix is always the
	/// same - hand-write a module named "3rdParty::X" (the override tier), which the resolver
	/// prefers over parsing.
	/// </summary>
	public FindModuleParseException(string message)
		: base(message)
	{
	}
}
