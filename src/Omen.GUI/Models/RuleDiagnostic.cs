// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.GUI.Models;

public enum RuleDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// One Roslyn diagnostic against an open rule file, translated to 1-based line/column for
/// display and a text-offset span for inline squiggle rendering. Produced by
/// <see cref="Services.RuleEditorService"/> from the exact same <c>Microsoft.CodeAnalysis
/// .Diagnostic</c> a real build would report - see RuleCompiler.ValidateFileAsync.
/// </summary>
public sealed record RuleDiagnostic(
    RuleDiagnosticSeverity Severity,
    string Message,
    int Line,
    int Column,
    int StartOffset,
    int Length);
