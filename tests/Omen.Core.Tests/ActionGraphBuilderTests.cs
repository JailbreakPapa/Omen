// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Core.Implementations;
using Omen.Core.Interfaces;
using Omen.Core.Rules;
using Omen.Platforms;

namespace Omen.Core.Tests;

public class ActionGraphBuilderTests : IDisposable
{
    private readonly string _projectRoot;

    public ActionGraphBuilderTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionGraphBuilderTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    private void WriteSourceFile(string moduleSourceDir, string fileName)
    {
        var dir = Path.Combine(_projectRoot, moduleSourceDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "// test source\n");
    }

    private BuildContext CreateContext() => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Development,
        ProjectRoot = _projectRoot,
        OutputDirectory = Path.Combine(_projectRoot, "Binaries"),
        IntermediateDirectory = Path.Combine(_projectRoot, "Intermediate")
    };

    private static ActionGraphBuilder CreateBuilder(BuildContext context) =>
        new(context, new FakeToolchain(), new Sha256DigestCalculator());

    private sealed class TestTarget : TargetRules
    {
        public TestTarget(BuildContext context) : base(context) { }
    }

    private sealed class TestModule : ModuleRules
    {
        public TestModule(BuildContext context) : base(context) { }
    }

    private sealed class FakeToolchain : ToolchainBase
    {
        public override TargetPlatform Platform => TargetPlatform.Windows;
        public override TargetArchitecture Architecture => TargetArchitecture.X64;
        public override string Name => "Fake";
        public override string Version => "1.0";
        public override string CompilerPath => "cl.exe";
        public override string LinkerPath => "link.exe";
        public override string ArchiverPath => "lib.exe";
        public override string ObjectFileExtension => ".obj";
        public override string StaticLibraryExtension => ".lib";
        public override string SharedLibraryExtension => ".dll";
        public override string ExecutableExtension => ".exe";

        public override Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Graph-building tests only; execution is not exercised.");
        public override Task<LinkResult> LinkAsync(LinkRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Graph-building tests only; execution is not exercised.");
        public override Task<ArchiveResult> ArchiveAsync(ArchiveRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Graph-building tests only; execution is not exercised.");
        public override IReadOnlyList<string> GetDefaultCompilerFlags(BuildConfiguration configuration) => [];
        public override IReadOnlyList<string> GetDefaultLinkerFlags(BuildConfiguration configuration) => [];
        protected override IReadOnlyList<CompileDiagnostic> ParseDiagnostics(string output) => [];
    }

    [Fact]
    public void ModuleWithBinaryType_GetsItsOwnLinkAction()
    {
        // Arrange
        var context = CreateContext();
        WriteSourceFile("Source/Runtime", "Runtime.cpp");
        var runtimeModule = new TestModule(context) { SourceDirectory = "Source/Runtime", BinaryType = TargetType.SharedLibrary };
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [runtimeModule]);

        // Assert: one link action for the independent module's own binary, plus the
        // target's own (empty, since all objects were absorbed by the independent module).
        var linkActions = graph.Actions.Where(a => a.Type is ActionType.Link or ActionType.Archive).ToList();
        linkActions.Should().ContainSingle(a => a.Description.Contains("TestModule"));
    }
}
