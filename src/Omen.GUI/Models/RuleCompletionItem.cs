// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.GUI.Models;

/// <summary>
/// One completion candidate offered while editing a rule file. <paramref name="InsertText"/> is
/// what gets typed into the document; <paramref name="Detail"/> is the symbol's full display
/// string (signature) shown alongside it, so an author can tell DefineFlavor(PluginFlavorKind)
/// apart from an overload without leaving the list.
/// </summary>
public sealed record RuleCompletionItem(string InsertText, string Kind, string Detail);

/// <summary>
/// Quick info for the symbol under the cursor/mouse: its full signature plus, where the symbol
/// carries one, its XML doc summary.
/// </summary>
public sealed record RuleQuickInfo(string Signature, string? Summary);
