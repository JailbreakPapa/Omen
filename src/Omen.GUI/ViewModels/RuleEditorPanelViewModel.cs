// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Executors.Orchestration;
using Omen.GUI.Models;
using Omen.GUI.Services;

namespace Omen.GUI.ViewModels;

/// <summary>
/// Owns every open rule-file document: opening (deduplicated by path), the active tab, saving,
/// and the unsaved-changes prompt for closing a dirty document. MainWindowViewModel owns one of
/// these (same ownership pattern as OptionsPanel/MatrixPanel) and forwards diagnostics into its
/// own OutputLines so the existing Problems tab picks them up without knowing anything about
/// rule documents.
/// </summary>
public sealed partial class RuleEditorPanelViewModel : ViewModelBase
{
    private readonly RuleEditorService _service = new();

    public Action<string, OrchestratorEventLevel>? Report { get; set; }

    /// <summary>Called whenever an open document's diagnostics change, so the owner can mirror
    /// them into the Problems tab. Passed the document rather than raising a bespoke event type,
    /// since the owner needs the document's FilePath to replace its previous lines.</summary>
    public Action<RuleEditorDocument>? DiagnosticsChanged { get; set; }

    public ObservableCollection<RuleEditorDocument> OpenDocuments { get; } = [];

    [ObservableProperty]
    private RuleEditorDocument? _activeDocument;

    /// <summary>The document a close was requested for but not yet resolved - non-null drives
    /// the unsaved-changes prompt overlay, the same on/off-by-null-check pattern
    /// MainWindowViewModel already uses for Settings/Search.</summary>
    [ObservableProperty]
    private RuleEditorDocument? _pendingCloseDocument;

    public bool IsUnsavedPromptOpen => PendingCloseDocument != null;

    partial void OnPendingCloseDocumentChanged(RuleEditorDocument? value) =>
        OnPropertyChanged(nameof(IsUnsavedPromptOpen));

    /// <summary>
    /// Opens a rule file for editing. If it is already open, just activates its tab - this is
    /// the "no duplicate tabs for the same file" guarantee the project tree and search overlay
    /// both rely on.
    /// </summary>
    public RuleEditorDocument OpenFile(string filePath, string? projectRoot)
    {
        var existing = OpenDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ActiveDocument = existing;
            return existing;
        }

        var document = new RuleEditorDocument(_service) { FilePath = filePath, ProjectRoot = projectRoot };
        document.Diagnostics.CollectionChanged += (_, _) => DiagnosticsChanged?.Invoke(document);
        document.Load();

        if (document.LoadError != null)
            Report?.Invoke($"Could not open '{filePath}': {document.LoadError}", OrchestratorEventLevel.Error);

        OpenDocuments.Add(document);
        ActiveDocument = document;
        return document;
    }

    public void Save(RuleEditorDocument document)
    {
        try
        {
            document.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report?.Invoke($"Could not save '{document.FilePath}': {ex.Message}", OrchestratorEventLevel.Error);
        }
    }

    /// <summary>
    /// Requests a document be closed. A clean document closes immediately; a dirty one instead
    /// opens the unsaved-changes prompt and waits for ConfirmCloseSave/ConfirmCloseDiscard/
    /// CancelClose - closing a file that drives the build must never silently discard edits.
    /// </summary>
    public void RequestClose(RuleEditorDocument document)
    {
        if (!document.IsDirty)
        {
            RemoveDocument(document);
            return;
        }

        PendingCloseDocument = document;
    }

    public void ConfirmCloseSave()
    {
        if (PendingCloseDocument is not { } document)
            return;

        Save(document);
        RemoveDocument(document);
        PendingCloseDocument = null;
    }

    public void ConfirmCloseDiscard()
    {
        if (PendingCloseDocument is not { } document)
            return;

        RemoveDocument(document);
        PendingCloseDocument = null;
    }

    public void CancelClose() => PendingCloseDocument = null;

    private void RemoveDocument(RuleEditorDocument document)
    {
        var index = OpenDocuments.IndexOf(document);
        if (index < 0)
            return;

        OpenDocuments.RemoveAt(index);
        document.Dispose();

        if (ActiveDocument == document)
            ActiveDocument = OpenDocuments.Count == 0 ? null
                : OpenDocuments[Math.Min(index, OpenDocuments.Count - 1)];
    }

    /// <summary>
    /// True if any open document has unsaved edits - used to gate the "close project"/app-exit
    /// paths, which would otherwise silently drop edits to files that were never explicitly
    /// closed one at a time.
    /// </summary>
    public bool HasUnsavedChanges => OpenDocuments.Any(d => d.IsDirty);

    public void Reset()
    {
        foreach (var document in OpenDocuments)
            document.Dispose();
        OpenDocuments.Clear();
        ActiveDocument = null;
        PendingCloseDocument = null;
    }
}
