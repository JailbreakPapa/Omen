// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Omen.GUI.Models;

namespace Omen.GUI.Controls;

/// <summary>
/// Paints a translucent highlight under each open diagnostic's span - the rule editor's
/// "inline" half of diagnostics display (the Problems tab is the other half). A flat
/// translucent fill rather than a squiggly underline: AvalonEdit has no built-in squiggle
/// primitive, and hand-rolling wavy-line geometry earns its keep only if a human asks for that
/// specific look - the highlight already reads clearly as "something is wrong here", and pairs
/// with the hover tooltip (see RuleEditorView.OnEditorMouseHover) for the actual message.
/// </summary>
public sealed class RuleDiagnosticRenderer(Func<IReadOnlyList<RuleDiagnostic>> getDiagnostics) : IBackgroundRenderer
{
    private static readonly SolidColorBrush ErrorFill = MakeFill(Colors.Red, 0.18);
    private static readonly SolidColorBrush WarningFill = MakeFill(Colors.Gold, 0.16);

    private static SolidColorBrush MakeFill(Color color, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var diagnostics = getDiagnostics();
        if (diagnostics.Count == 0)
            return;

        textView.EnsureVisualLines();
        var textLength = textView.Document?.TextLength ?? 0;
        if (textLength == 0)
            return;

        foreach (var diagnostic in diagnostics)
        {
            var start = Math.Clamp(diagnostic.StartOffset, 0, textLength);
            var end = Math.Clamp(diagnostic.StartOffset + Math.Max(diagnostic.Length, 1), start, textLength);
            if (end <= start)
                continue;

            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
            builder.AddSegment(textView, new TextSegment { StartOffset = start, Length = end - start });
            var geometry = builder.CreateGeometry();
            if (geometry != null)
            {
                var fill = diagnostic.Severity == RuleDiagnosticSeverity.Error ? ErrorFill : WarningFill;
                drawingContext.DrawGeometry(fill, null, geometry);
            }
        }
    }
}
