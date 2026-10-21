// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.GUI.ViewModels;

namespace Omen.GUI.Tests;

/// <summary>
/// Covers the non-UI half of "unsaved changes safety": opening dedupes by path, a clean
/// document closes immediately, and a dirty one goes through the pending-close prompt rather
/// than being silently dropped - Save/Discard/Cancel all resolve it correctly.
/// </summary>
public class RuleEditorPanelViewModelTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly RuleEditorPanelViewModel _panel = new();

    public RuleEditorPanelViewModelTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleEditorPanelViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _panel.Reset();
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }

    private string WriteModuleFile(string name = "Good.module.cs")
    {
        var file = Path.Combine(_projectRoot, name);
        File.WriteAllText(file, """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class GoodModule : ModuleRules
        {
            public GoodModule(BuildContext context) : base(context) { }
        }
        """);
        return file;
    }

    [Fact]
    public void OpenFile_SameFileTwice_ReturnsSameDocumentAndDoesNotDuplicate()
    {
        var file = WriteModuleFile();

        var first = _panel.OpenFile(file, _projectRoot);
        var second = _panel.OpenFile(file, _projectRoot);

        second.Should().BeSameAs(first);
        _panel.OpenDocuments.Should().ContainSingle();
        _panel.ActiveDocument.Should().Be(first);
    }

    [Fact]
    public void OpenFile_DifferentFiles_ActivatesTheMostRecentlyOpened()
    {
        var a = _panel.OpenFile(WriteModuleFile("A.module.cs"), _projectRoot);
        var b = _panel.OpenFile(WriteModuleFile("B.module.cs"), _projectRoot);

        _panel.OpenDocuments.Should().HaveCount(2);
        _panel.ActiveDocument.Should().Be(b);
        _panel.ActiveDocument.Should().NotBe(a);
    }

    [Fact]
    public void RequestClose_CleanDocument_ClosesImmediatelyWithoutPrompting()
    {
        var document = _panel.OpenFile(WriteModuleFile(), _projectRoot);

        _panel.RequestClose(document);

        _panel.OpenDocuments.Should().BeEmpty();
        _panel.IsUnsavedPromptOpen.Should().BeFalse();
    }

    [Fact]
    public void RequestClose_DirtyDocument_OpensPromptAndDoesNotCloseYet()
    {
        var document = _panel.OpenFile(WriteModuleFile(), _projectRoot);
        document.Text += "\n// edit";

        _panel.RequestClose(document);

        _panel.OpenDocuments.Should().ContainSingle();
        _panel.IsUnsavedPromptOpen.Should().BeTrue();
        _panel.PendingCloseDocument.Should().Be(document);
    }

    [Fact]
    public void ConfirmCloseDiscard_ClosesWithoutWritingToDisk()
    {
        var file = WriteModuleFile();
        var originalContents = File.ReadAllText(file);
        var document = _panel.OpenFile(file, _projectRoot);
        document.Text += "\n// edit";

        _panel.RequestClose(document);
        _panel.ConfirmCloseDiscard();

        _panel.OpenDocuments.Should().BeEmpty();
        _panel.IsUnsavedPromptOpen.Should().BeFalse();
        File.ReadAllText(file).Should().Be(originalContents);
    }

    [Fact]
    public void ConfirmCloseSave_WritesToDiskThenCloses()
    {
        var file = WriteModuleFile();
        var document = _panel.OpenFile(file, _projectRoot);
        var edited = document.Text + "\n// edit";
        document.Text = edited;

        _panel.RequestClose(document);
        _panel.ConfirmCloseSave();

        _panel.OpenDocuments.Should().BeEmpty();
        File.ReadAllText(file).Should().Be(edited);
    }

    [Fact]
    public void CancelClose_LeavesDocumentOpenAndDirty()
    {
        var document = _panel.OpenFile(WriteModuleFile(), _projectRoot);
        document.Text += "\n// edit";

        _panel.RequestClose(document);
        _panel.CancelClose();

        _panel.OpenDocuments.Should().ContainSingle();
        _panel.IsUnsavedPromptOpen.Should().BeFalse();
        document.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void RequestClose_NonActiveTab_ActivatesRemainingNeighbor()
    {
        var a = _panel.OpenFile(WriteModuleFile("A.module.cs"), _projectRoot);
        var b = _panel.OpenFile(WriteModuleFile("B.module.cs"), _projectRoot);
        // b is active; close a (not the active tab) and b must remain active.
        _panel.RequestClose(a);

        _panel.OpenDocuments.Should().ContainSingle().Which.Should().Be(b);
        _panel.ActiveDocument.Should().Be(b);
    }

    [Fact]
    public void HasUnsavedChanges_ReflectsAnyOpenDirtyDocument()
    {
        var document = _panel.OpenFile(WriteModuleFile(), _projectRoot);
        _panel.HasUnsavedChanges.Should().BeFalse();

        document.Text += "\n// edit";
        _panel.HasUnsavedChanges.Should().BeTrue();
    }
}
