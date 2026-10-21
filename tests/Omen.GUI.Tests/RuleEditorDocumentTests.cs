// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.GUI.Models;
using Omen.GUI.Services;

namespace Omen.GUI.Tests;

/// <summary>
/// Covers dirty tracking, load/save, and immediate (non-debounced) validation - the debounce
/// delay itself is not asserted on directly (that would make the suite slow and timing-
/// sensitive); ValidateNowAsync exists specifically so tests can bypass it.
/// </summary>
public class RuleEditorDocumentTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly RuleEditorService _service = new();

    public RuleEditorDocumentTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleEditorDocumentTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }

    private const string ValidModule = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class GoodModule : ModuleRules
        {
            public GoodModule(BuildContext context) : base(context) { }
        }
        """;

    [Fact]
    public void Load_PopulatesTextAndIsNotDirty()
    {
        var file = Path.Combine(_projectRoot, "Good.module.cs");
        File.WriteAllText(file, ValidModule);

        var document = new RuleEditorDocument(_service) { FilePath = file, ProjectRoot = _projectRoot };
        document.Load();

        document.Text.Should().Be(ValidModule);
        document.IsDirty.Should().BeFalse();
        document.DisplayTitle.Should().Be("Good.module.cs");
    }

    [Fact]
    public void Load_MissingFile_SetsLoadErrorInsteadOfThrowing()
    {
        var file = Path.Combine(_projectRoot, "DoesNotExist.module.cs");

        var document = new RuleEditorDocument(_service) { FilePath = file, ProjectRoot = _projectRoot };
        document.Load();

        document.LoadError.Should().NotBeNull();
    }

    [Fact]
    public void TextChanged_MarksDirtyAndDirtyMarkerAppearsInTitle()
    {
        var file = Path.Combine(_projectRoot, "Good.module.cs");
        File.WriteAllText(file, ValidModule);

        var document = new RuleEditorDocument(_service) { FilePath = file, ProjectRoot = _projectRoot };
        document.Load();

        document.Text += "\n// a comment";

        document.IsDirty.Should().BeTrue();
        document.DisplayTitle.Should().Be("Good.module.cs *");

        // Every Text mutation also (re)starts a 400ms debounced validation in the background;
        // Dispose cancels it so it can't run after this test's temp directory is gone.
        document.Dispose();
    }

    [Fact]
    public void TextChanged_BackToSavedText_ClearsDirty()
    {
        var file = Path.Combine(_projectRoot, "Good.module.cs");
        File.WriteAllText(file, ValidModule);

        var document = new RuleEditorDocument(_service) { FilePath = file, ProjectRoot = _projectRoot };
        document.Load();

        document.Text += "\n// a comment";
        document.Text = ValidModule;

        document.IsDirty.Should().BeFalse();
        document.Dispose();
    }

    [Fact]
    public void Save_WritesToDiskAndClearsDirty()
    {
        var file = Path.Combine(_projectRoot, "Good.module.cs");
        File.WriteAllText(file, ValidModule);

        var document = new RuleEditorDocument(_service) { FilePath = file, ProjectRoot = _projectRoot };
        document.Load();
        document.Text = ValidModule + "\n// edited";

        document.Save();

        document.IsDirty.Should().BeFalse();
        File.ReadAllText(file).Should().Be(ValidModule + "\n// edited");
        document.Dispose();
    }

    [Fact]
    public async Task ValidateNowAsync_PopulatesDiagnosticsAndCounts()
    {
        var file = Path.Combine(_projectRoot, "Broken.module.cs");
        File.WriteAllText(file, ValidModule);

        var document = new RuleEditorDocument(_service) { FilePath = file, ProjectRoot = _projectRoot };
        document.Load();
        document.Text = ValidModule.Replace("public GoodModule(BuildContext context) : base(context) { }",
            "public GoodModule(BuildContext context) : base(context) { NotAMethod(); }");

        await document.ValidateNowAsync();

        document.ErrorCount.Should().BeGreaterThan(0);
        document.Diagnostics.Should().Contain(d => d.Message.Contains("NotAMethod"));
        document.StatusText.Should().Contain("error");
        document.Dispose();
    }
}
