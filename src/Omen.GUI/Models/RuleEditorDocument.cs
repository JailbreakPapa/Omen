// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.GUI.Services;

namespace Omen.GUI.Models;

/// <summary>
/// One open rule file in the editor. Owns its own text/dirty/diagnostics state; the editor
/// control (RuleEditorView) is a dumb text surface that pushes keystrokes in via
/// <see cref="Text"/> and reads <see cref="Diagnostics"/> back out for inline squiggles -
/// all validation logic lives here and in RuleEditorService, not in code-behind.
/// </summary>
public sealed partial class RuleEditorDocument : ObservableObject, IDisposable
{
    private readonly RuleEditorService _service;
    private readonly object _diagnosticsGate = new();
    private long _validationRevision;

    public required string FilePath { get; init; }

    /// <summary>
    /// The project root to validate against, so sibling rule files are part of the compile
    /// context - null for a file opened outside any project (still validates standalone; see
    /// RuleCompiler.BuildCompilationAsync's fallback).
    /// </summary>
    public string? ProjectRoot { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Title shown on the document tab: file name, with a dirty marker.</summary>
    public string DisplayTitle => IsDirty ? FileName + " *" : FileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _text = string.Empty;

    private string _savedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isValidating;

    /// <summary>Set when the file could not be read from disk; surfaced instead of silently
    /// opening an empty document.</summary>
    [ObservableProperty]
    private string? _loadError;

    public ObservableCollection<RuleDiagnostic> Diagnostics { get; } = [];

    public int ErrorCount => Diagnostics.Count(d => d.Severity == RuleDiagnosticSeverity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == RuleDiagnosticSeverity.Warning);

    public string StatusText => IsValidating
        ? "Validating..."
        : ErrorCount > 0
            ? $"{ErrorCount} error(s), {WarningCount} warning(s)"
            : WarningCount > 0
                ? $"{WarningCount} warning(s)"
                : "No problems";

    private CancellationTokenSource? _debounceCts;

    // Debounce while typing so a fast typist doesn't queue up a compile per keystroke; short
    // enough that validation still feels immediate once typing pauses.
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);

    public RuleEditorDocument(RuleEditorService service) => _service = service;

    public void Load()
    {
        try
        {
            _savedText = File.ReadAllText(FilePath);
            LoadError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _savedText = string.Empty;
            LoadError = ex.Message;
        }

        // Setting Text runs OnTextChanged below, which compares against _savedText (just set to
        // the same value) and computes IsDirty = false - loading is never a "dirty" edit. It
        // also kicks off the initial validation pass, which is exactly what should happen on open.
        Text = _savedText;
    }

    partial void OnTextChanged(string value)
    {
        IsDirty = value != _savedText;
        RequestValidate();
    }

    /// <summary>(Re)starts the debounce timer; a call in flight is superseded, not queued.</summary>
    public void RequestValidate()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = ValidateAfterDelayAsync(cts.Token);
    }

    private async Task ValidateAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceDelay, ct);
            await ValidateNowAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit - not an error.
        }
    }

    /// <summary>Validates immediately, skipping the debounce - used for explicit "Validate now"
    /// and right after Save.</summary>
    public async Task ValidateNowAsync(CancellationToken ct = default)
    {
        // An explicit validation supersedes the delayed validation scheduled by TextChanged.
        if (!ct.CanBeCanceled)
            _debounceCts?.Cancel();
        var revision = Interlocked.Increment(ref _validationRevision);
        IsValidating = true;
        try
        {
            var diagnostics = await _service.ValidateAsync(ProjectRoot, FilePath, Text, ct);
            lock (_diagnosticsGate)
            {
                if (ct.IsCancellationRequested || revision != Volatile.Read(ref _validationRevision))
                    return;
                Diagnostics.Clear();
                foreach (var diagnostic in diagnostics)
                    Diagnostics.Add(diagnostic);
            }

            OnPropertyChanged(nameof(ErrorCount));
            OnPropertyChanged(nameof(WarningCount));
            OnPropertyChanged(nameof(StatusText));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (revision == Volatile.Read(ref _validationRevision))
                IsValidating = false;
        }
    }

    public Task<IReadOnlyList<RuleCompletionItem>> GetCompletionsAsync(int position, CancellationToken ct = default) =>
        _service.GetCompletionsAsync(ProjectRoot, FilePath, Text, position, ct);

    public Task<RuleQuickInfo?> GetQuickInfoAsync(int position, CancellationToken ct = default) =>
        _service.GetQuickInfoAsync(ProjectRoot, FilePath, Text, position, ct);

    /// <summary>Writes the current text to disk. Throws on failure - the caller (the panel
    /// ViewModel) is responsible for reporting it rather than this model swallowing it, since a
    /// failed save of a build-driving file is not something to hide.</summary>
    public void Save()
    {
        File.WriteAllText(FilePath, Text);
        _savedText = Text;
        IsDirty = false;
        // Other rule files may be read fresh from disk now that this one is saved - e.g. a
        // sibling .plugin.cs that references a type this file declares.
        RequestValidate();
    }

    public void Dispose() => _debounceCts?.Cancel();
}
