using System;

namespace Omen.Core.Rules;

/// <summary>
/// Exception thrown when rule compilation fails.
/// </summary>
/// <summary>
/// A rule file compiled fine but threw while being instantiated - a bad path, a missing
/// plugin.json, a null dereference in someone's *.plugin.cs. Distinct from
/// RuleCompilationException, which means the C# itself did not compile.
/// </summary>
public sealed class RuleInstantiationException : Exception
{
	/// <summary>
	/// Exception thrown when rule compilation fails.
	/// </summary>
	/// <summary>
	/// A rule file compiled fine but threw while being instantiated - a bad path, a missing
	/// plugin.json, a null dereference in someone's *.plugin.cs. Distinct from
	/// RuleCompilationException, which means the C# itself did not compile.
	/// </summary>
	public RuleInstantiationException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
