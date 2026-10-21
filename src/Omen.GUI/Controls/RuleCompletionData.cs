// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Omen.GUI.Models;

namespace Omen.GUI.Controls;

/// <summary>One row in the completion popup, wrapping a RuleCompletionItem for AvalonEdit's
/// CompletionWindow. Completion(...) replaces the identifier the user was typing rather than
/// just inserting at the caret, so accepting a completion mid-word replaces the whole word
/// instead of duplicating its tail.</summary>
public sealed class RuleCompletionData(RuleCompletionItem item) : ICompletionData
{
    public string Text => item.InsertText;
    public object Content => Text;
    public object? Description => item.Detail;
    public double Priority => 0;
    public ImageSource? Image => null;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}
