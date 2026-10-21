// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Rules;

public enum ConfiguredNewlineStyle { Preserve, LF, CRLF }

/// <summary>A UTF-8 configuration template rendered during configure and before a build.</summary>
public sealed class ConfiguredFile
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public Dictionary<string, string> Variables { get; init; } = [];
    public bool AtOnly { get; init; }
    public bool Strict { get; init; }
    public ConfiguredNewlineStyle NewlineStyle { get; init; }
    public bool EscapeQuotes { get; init; }
    public bool CopyOnly { get; init; }
}
