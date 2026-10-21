// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.GUI.Models;

/// <summary>A command, option, or project file displayed by the search overlay.</summary>
public sealed record QuickSearchResult(string Group, string Name, string Detail, Action Activate);
