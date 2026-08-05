// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Executors.Orchestration;

/// <summary>
/// Severity of an orchestrator event, decided once by the code that knows whether
/// something failed - not inferred later by scanning text for the word "error".
/// </summary>
public enum OrchestratorEventLevel
{
    Info,
    Warning,
    Error,
    Success
}

/// <summary>
/// A single human-readable event reported by a build/clean/generate orchestrator.
/// The CLI renders these through AnsiConsole; the GUI appends them to its output pane.
/// Both consume the identical stream - there is one implementation of what happened
/// during an operation, not two that can describe it differently.
/// </summary>
public sealed class OrchestratorEvent(string message, OrchestratorEventLevel level)
{
    public string Message { get; } = message;
    public OrchestratorEventLevel Level { get; } = level;
}
