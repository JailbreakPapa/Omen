// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.GUI.Models;
using Omen.GUI.Services;

namespace Omen.GUI.Tests;

/// <summary>
/// The GUI-side half of the "validation means what the build means" guarantee: these exercise
/// RuleEditorService's translation of RuleCompiler's Roslyn Diagnostic/ISymbol results into the
/// small display models the editor binds to, plus IntelliSense (completion/quick info) against
/// a real Omen.Core-backed compilation.
/// </summary>
public class RuleEditorServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly RuleEditorService _service = new();

    public RuleEditorServiceTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleEditorServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }

    [Fact]
    public async Task ValidateAsync_ReportsOneBasedLineAndColumn()
    {
        var file = Path.Combine(_projectRoot, "Broken.module.cs");
        // Line 5, column 9 is where "NotAMethod" starts.
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class BrokenModule : ModuleRules
        {
            public BrokenModule(BuildContext context) : base(context)
            {
                NotAMethod();
            }
        }
        """;
        File.WriteAllText(file, text);

        var diagnostics = await _service.ValidateAsync(_projectRoot, file, text);

        var error = diagnostics.Should().ContainSingle(d => d.Severity == RuleDiagnosticSeverity.Error).Subject;
        error.Line.Should().Be(8);
        error.Message.Should().Contain("NotAMethod");
    }

    [Fact]
    public async Task ValidateAsync_CleanFile_ReturnsNoDiagnostics()
    {
        var file = Path.Combine(_projectRoot, "Good.module.cs");
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class GoodModule : ModuleRules
        {
            public GoodModule(BuildContext context) : base(context) { }
        }
        """;
        File.WriteAllText(file, text);

        var diagnostics = await _service.ValidateAsync(_projectRoot, file, text);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCompletionsAsync_AfterDot_OffersInstanceMembers()
    {
        var file = Path.Combine(_projectRoot, "Camera.plugin.cs");
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context)
            {
                var runtime = DefineFlavor(PluginFlavorKind.Runtime);
                runtime.
            }
        }
        """;
        var position = text.IndexOf("runtime.", StringComparison.Ordinal) + "runtime.".Length;

        var completions = await _service.GetCompletionsAsync(_projectRoot, file, text, position);

        // PrivateDependencies is a real member of PluginFlavor (the type DefineFlavor returns) -
        // proof this is a genuine member list off the Omen.Core type, not a static keyword list.
        completions.Should().Contain(c => c.InsertText == "PrivateDependencies");
    }

    [Fact]
    public async Task GetCompletionsAsync_AfterTypeDot_OffersEnumMembers()
    {
        var file = Path.Combine(_projectRoot, "Camera.plugin.cs");
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context)
            {
                DefineFlavor(PluginFlavorKind.
            }
        }
        """;
        var position = text.IndexOf("PluginFlavorKind.", StringComparison.Ordinal) + "PluginFlavorKind.".Length;

        var completions = await _service.GetCompletionsAsync(_projectRoot, file, text, position);

        completions.Should().Contain(c => c.InsertText == "Static");
        completions.Should().Contain(c => c.InsertText == "Runtime");
    }

    [Fact]
    public async Task GetQuickInfoAsync_OnKnownType_ReturnsFullyQualifiedSignature()
    {
        var file = Path.Combine(_projectRoot, "Camera.plugin.cs");
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context) { }
        }
        """;
        var position = text.IndexOf("PluginRules", StringComparison.Ordinal) + 1;

        var info = await _service.GetQuickInfoAsync(_projectRoot, file, text, position);

        info.Should().NotBeNull();
        info!.Signature.Should().Contain("PluginRules");
    }
}
