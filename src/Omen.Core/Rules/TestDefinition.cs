// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Rules;

/// <summary>A process test registered by a project or target, run with <c>omen test</c>.</summary>
public sealed class TestDefinition
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public List<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string?> Environment { get; init; } = [];
    public double TimeoutSeconds { get; init; } = 300;
    public int ExpectedExitCode { get; init; }
    public int? SkipExitCode { get; init; }
    public bool Disabled { get; init; }
    public bool RunSerial { get; init; }
    public List<string> Labels { get; init; } = [];
    /// <summary>Prerequisites run first, including when selected by a name/label filter.</summary>
    public List<string> DependsOn { get; init; } = [];
    public string? PassRegularExpression { get; init; }
    public string? FailRegularExpression { get; init; }
}
