// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Executors.Orchestration;

namespace Omen.GUI.Models;

public sealed class OutputLine
{
    public required string Text { get; init; }
    public required OrchestratorEventLevel Level { get; init; }

    /// <summary>
    /// Set only for a line mirrored from a rule-file editor's diagnostics (see
    /// MainWindowViewModel.OnRuleDocumentDiagnosticsChanged) - null for ordinary build output.
    /// Lets the owner replace just one file's previous diagnostic lines on revalidation, and
    /// lets the Problems tab jump back to the file/position on double-click.
    /// </summary>
    public string? SourceFile { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

