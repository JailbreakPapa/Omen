using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Omen.Core.Rules;

public sealed class RuleCompilationException : Exception
{
	public IReadOnlyList<Diagnostic> Diagnostics { get; }

	public RuleCompilationException(string message, IEnumerable<Diagnostic> diagnostics)
		: base(message)
	{
		Diagnostics = diagnostics.ToList();
	}
}
