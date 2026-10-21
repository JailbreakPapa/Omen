// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Rules;

/// <summary>Copies a file or directory's contents to a directory below an installation prefix.</summary>
public sealed class InstallRule
{
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public string Component { get; init; } = "Runtime";
    public bool Directory { get; init; }
    public bool Optional { get; init; }
    /// <summary>Optional new file name; valid for individual files only.</summary>
    public string? Rename { get; init; }
    /// <summary>Directory filters against source-relative paths, e.g. *.h or *.hpp.</summary>
    public List<string> Patterns { get; init; } = [];
    public List<string> ExcludePatterns { get; init; } = [];
}
