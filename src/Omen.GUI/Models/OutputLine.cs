// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Executors.Orchestration;

namespace Omen.GUI.Models;

public sealed class OutputLine
{
    public required string Text { get; init; }
    public required OrchestratorEventLevel Level { get; init; }
}
