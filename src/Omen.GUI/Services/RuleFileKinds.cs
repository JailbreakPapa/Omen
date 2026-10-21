// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.GUI.Services;

/// <summary>
/// The four rule-file extensions the editor opens: RuleCompiler compiles the first three as
/// part of a project (see RuleCompiler.DiscoverRuleFiles); *.omenproject.cs is editor-only today
/// (no compiler support yet) but still gets syntax highlighting and standalone validation - see
/// RuleCompiler.BuildCompilationAsync's "not found under projectRoot" fallback.
/// </summary>
public static class RuleFileKinds
{
    private static readonly string[] Suffixes = [".plugin.cs", ".module.cs", ".target.cs", ".omenproject.cs"];

    public static bool IsRuleFile(string path) =>
        Suffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
