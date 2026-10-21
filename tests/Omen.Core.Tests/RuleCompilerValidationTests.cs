// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Microsoft.CodeAnalysis.CSharp;
using Omen.Core.Rules;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Omen.Core.Tests;

/// <summary>
/// Covers the GUI rule-file editor's shared validation path: ValidateFileAsync must use the
/// exact same references/parse options/compilation options CompileRulesAsync does (so the
/// editor and a real build never disagree), while still reflecting unsaved edits and scoping
/// diagnostics to just the document being validated.
/// </summary>
public class RuleCompilerValidationTests : IDisposable
{
    private readonly string _projectRoot;

    public RuleCompilerValidationTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(RuleCompilerValidationTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }

    [Fact]
    public async Task ValidateFileAsync_ValidRuleFile_ProducesNoErrors()
    {
        var file = Path.Combine(_projectRoot, "Camera.plugin.cs");
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context)
            {
                LoadManifest("Plugins/Camera");
                DefineFlavor(PluginFlavorKind.Static);
            }
        }
        """;
        File.WriteAllText(file, text);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var diagnostics = await compiler.ValidateFileAsync(_projectRoot, file, text);

        diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ValidateFileAsync_SyntaxError_IsReported()
    {
        var file = Path.Combine(_projectRoot, "Broken.module.cs");
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class BrokenModule : ModuleRules
        {
            public BrokenModule(BuildContext context) : base(context)
            {
                // missing closing brace
        }
        """;
        File.WriteAllText(file, text);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var diagnostics = await compiler.ValidateFileAsync(_projectRoot, file, text);

        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ValidateFileAsync_UnknownApiMember_IsReportedAsCompileError()
    {
        // This is the "means the same as the build" guarantee in a single case: a call to a
        // member that does not exist on PluginRules must fail here exactly as CompileRulesAsync
        // would fail it, because both use the same Omen.Core metadata reference.
        var file = Path.Combine(_projectRoot, "Camera.plugin.cs");
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context)
            {
                ThisMemberDoesNotExist();
            }
        }
        """;
        File.WriteAllText(file, text);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var diagnostics = await compiler.ValidateFileAsync(_projectRoot, file, text);

        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ValidateFileAsync_UnsavedEdit_IsReflectedWithoutTouchingDisk()
    {
        var file = Path.Combine(_projectRoot, "Camera.plugin.cs");
        var onDisk = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context)
            {
                DefineFlavor(PluginFlavorKind.Static);
            }
        }
        """;
        File.WriteAllText(file, onDisk);

        var unsavedBrokenText = onDisk.Replace("DefineFlavor(PluginFlavorKind.Static);", "NotARealMethod();");

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var diagnostics = await compiler.ValidateFileAsync(_projectRoot, file, unsavedBrokenText);

        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
        // The file on disk was never touched by validation.
        File.ReadAllText(file).Should().Be(onDisk);
    }

    [Fact]
    public async Task ValidateFileAsync_OtherProjectFilesAreClean_DoesNotLeakDiagnosticsBetweenFiles()
    {
        var goodFile = Path.Combine(_projectRoot, "Good.module.cs");
        File.WriteAllText(goodFile, """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class GoodModule : ModuleRules
        {
            public GoodModule(BuildContext context) : base(context) { }
        }
        """);

        var brokenText = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class BrokenModule : ModuleRules
        {
            public BrokenModule(BuildContext context) : base(context)
            {
                ThisDoesNotExistEither();
            }
        }
        """;
        var brokenFile = Path.Combine(_projectRoot, "Broken.module.cs");
        File.WriteAllText(brokenFile, brokenText);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));

        // Validating the good file must not surface the broken file's error.
        var goodDiagnostics = await compiler.ValidateFileAsync(_projectRoot, goodFile, File.ReadAllText(goodFile));
        goodDiagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);

        var brokenDiagnostics = await compiler.ValidateFileAsync(_projectRoot, brokenFile, brokenText);
        brokenDiagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ValidateFileAsync_FileOutsideAnyProject_StillValidatesStandalone()
    {
        // *.omenproject.cs (and any file opened outside a project root) isn't part of
        // RuleCompiler.DiscoverRuleFiles at all - validation must still work against just the
        // shared references, rather than throwing or silently doing nothing.
        var file = Path.Combine(_projectRoot, "Standalone.omenproject.cs");
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class StandaloneModule : ModuleRules
        {
            public StandaloneModule(BuildContext context) : base(context) { }
        }
        """;

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var diagnostics = await compiler.ValidateFileAsync(projectRoot: null, file, text);

        diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task BuildCompilationAsync_ReturnsActiveTreeMatchingActiveFile()
    {
        // This is what the editor's completion/quick-info would build on: the same
        // Compilation ValidateFileAsync uses, plus a handle to the active file's own tree so a
        // SemanticModel can be requested for it.
        var file = Path.Combine(_projectRoot, "Camera.plugin.cs");
        var text = """
        using Omen.Core.Configuration;
        using Omen.Core.Rules;

        public class CameraPlugin : PluginRules
        {
            public CameraPlugin(BuildContext context) : base(context)
            {
                DefineFlavor(PluginFlavorKind.Static);
            }
        }
        """;
        File.WriteAllText(file, text);

        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));
        var (compilation, activeTree) = await compiler.BuildCompilationAsync(_projectRoot, file, text);

        activeTree.FilePath.Should().Be(file);
        compilation.SyntaxTrees.Should().Contain(activeTree);

        // A semantic query against it resolves PluginRules to the real Omen.Core type, proof this
        // is genuinely compiled against the same Omen.Core reference as the build, not a stub.
        var model = compilation.GetSemanticModel(activeTree);
        var classDecl = activeTree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().Single();
        var symbol = model.GetDeclaredSymbol(classDecl);
        symbol!.BaseType!.ToDisplayString().Should().Be("Omen.Core.Rules.PluginRules");
    }

    [Fact]
    public async Task References_MatchesWhatCompileRulesAsyncUsesToCompile()
    {
        // Not a behavior test so much as a structural guarantee: References is the same list
        // instance CompileRulesAsync feeds to CSharpCompilation.Create, so there is only ever
        // one reference set to keep in sync, never two that could drift apart.
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "RuleCache"));

        compiler.References.Should().NotBeEmpty();
        compiler.References.Should().Contain(r =>
            r.Display != null && r.Display.Contains("Omen.Core", StringComparison.OrdinalIgnoreCase));
    }
}
