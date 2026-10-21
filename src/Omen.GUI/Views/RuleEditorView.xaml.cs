// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using Omen.GUI.Controls;
using Omen.GUI.Models;

namespace Omen.GUI.Views;

/// <summary>
/// Hosts one AvalonEdit TextEditor for one RuleEditorDocument. All logic (dirty tracking,
/// debounced validation, completion/quick-info queries) lives on the document/service; this
/// class only wires AvalonEdit's events to that document and hosts the control, per the
/// codebase's "code-behind may wire events, not own logic" rule.
/// </summary>
public partial class RuleEditorView : UserControl
{
    private RuleEditorDocument? _document;
    private bool _suppressEditorTextChanged;
    private CompletionWindow? _completionWindow;

    private IReadOnlyList<RuleDiagnostic> GetDocumentDiagnostics() =>
        (IReadOnlyList<RuleDiagnostic>?)_document?.Diagnostics ?? Array.Empty<RuleDiagnostic>();

    public RuleEditorView()
    {
        InitializeComponent();

        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.TextArea.TextView.BackgroundRenderers.Add(new RuleDiagnosticRenderer(GetDocumentDiagnostics));

        DataContextChanged += OnDataContextChanged;
        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.PreviewKeyDown += OnEditorPreviewKeyDown;
        Editor.MouseHover += OnEditorMouseHover;
        Editor.MouseHoverStopped += (_, _) => Editor.ToolTip = null;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_document != null)
            _document.PropertyChanged -= OnDocumentPropertyChanged;

        _document = DataContext as RuleEditorDocument;

        if (_document != null)
        {
            _suppressEditorTextChanged = true;
            Editor.Text = _document.Text;
            _suppressEditorTextChanged = false;

            _document.PropertyChanged += OnDocumentPropertyChanged;
        }

        Editor.TextArea.TextView.Redraw();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only Text needs to flow model -> editor (e.g. after Save reloads nothing here, but a
        // future "revert" action would). Everything else (diagnostics, IsValidating) is picked
        // up by binding or by the background renderer re-reading the collection on redraw.
        if (e.PropertyName != nameof(RuleEditorDocument.Text) || _document == null)
            return;

        if (Editor.Text == _document.Text)
            return;

        _suppressEditorTextChanged = true;
        var caret = Editor.CaretOffset;
        Editor.Text = _document.Text;
        Editor.CaretOffset = Math.Min(caret, Editor.Text.Length);
        _suppressEditorTextChanged = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorTextChanged || _document == null)
            return;

        _document.Text = Editor.Text;
        Editor.TextArea.TextView.Redraw();
    }

    // ---- Completion ---------------------------------------------------------------------

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == ".")
            _ = ShowCompletionAsync();
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = ShowCompletionAsync();
        }
    }

    private async Task ShowCompletionAsync()
    {
        if (_document == null)
            return;

        var offset = Editor.CaretOffset;
        IReadOnlyList<RuleCompletionItem> items;
        try
        {
            items = await _document.GetCompletionsAsync(offset);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (items.Count == 0 || Editor.CaretOffset != offset)
            return;

        _completionWindow = new CompletionWindow(Editor.TextArea);
        var data = _completionWindow.CompletionList.CompletionData;
        foreach (var item in items)
            data.Add(new RuleCompletionData(item));

        _completionWindow.Closed += (_, _) => _completionWindow = null;
        _completionWindow.Show();
    }

    // ---- Quick info (hover) ---------------------------------------------------------------

    private async void OnEditorMouseHover(object sender, MouseEventArgs e)
    {
        if (_document == null)
            return;

        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position == null)
            return;

        var offset = Editor.Document.GetOffset(position.Value.Location);

        RuleQuickInfo? info;
        try
        {
            info = await _document.GetQuickInfoAsync(offset);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (info == null)
        {
            Editor.ToolTip = null;
            return;
        }

        Editor.ToolTip = new ToolTip
        {
            Content = info.Summary == null ? info.Signature : $"{info.Signature}\n{info.Summary}",
            PlacementTarget = Editor,
            IsOpen = true
        };
        e.Handled = true;
    }
}
