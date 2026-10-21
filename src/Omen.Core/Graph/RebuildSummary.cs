// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Graph;

/// <summary>What <see cref="ActionGraph.SummarizeRebuilds"/> found: why this build's actions will run.</summary>
public sealed record RebuildSummary(int ToRun, IReadOnlyDictionary<string, int> ByKind, IReadOnlyList<(string Cause, int Count)> TopCauses)
{
    public override string ToString()
    {
        var kinds = string.Join(", ", ByKind.Select(k => $"{k.Value} {k.Key}"));
        var causes = string.Join(Environment.NewLine, TopCauses.Select(c => $"    {c.Count,6}  {c.Cause}"));
        return $"{ToRun} to run: {kinds}{Environment.NewLine}  Top causes:{Environment.NewLine}{causes}";
    }
}
