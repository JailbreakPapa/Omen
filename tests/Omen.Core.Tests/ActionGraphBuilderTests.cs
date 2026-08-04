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

    private static BuildContext CreateTestContext(string projectRoot) => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Development,
        ProjectRoot = projectRoot,
        OutputDirectory = Path.Combine(projectRoot, "Binaries"),
        IntermediateDirectory = Path.Combine(projectRoot, "Intermediate")
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

    private sealed class TestModuleB : ModuleRules
    {
        public TestModuleB(BuildContext context) : base(context) { }
    }

    private sealed class TestModuleC : ModuleRules
    {
        public TestModuleC(BuildContext context) : base(context) { }
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
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Runtime", "Runtime.cpp");
        var runtimeModule = new TestModule(context) { SourceDirectory = "Source/Runtime", BinaryType = TargetType.SharedLibrary };
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [runtimeModule]);

        // Assert: one link action for the independent module's own binary, plus the target's
        // own link action, which must depend on it and link against its produced artifact.
        var linkActions = graph.Actions.Where(a => a.Type is ActionType.Link or ActionType.Archive).ToList();
        var moduleLinkAction = linkActions.Should().ContainSingle(a => a.Description.Contains("TestModule")).Which;
        var targetLinkAction = linkActions.Single(a => a.ModuleName is null);

        targetLinkAction.Dependencies.Should().Contain(moduleLinkAction);

        var expectedModuleLibrary = Path.ChangeExtension(Path.Combine(context.OutputDirectory, runtimeModule.Name + ".dll"), ".lib");
        targetLinkAction.CommandLine.Should().Contain(expectedModuleLibrary);
    }

    [Fact]
    public void IndependentModule_DependingOnAnotherIndependentModule_LinksAfterIt()
    {
        // Arrange: TestModuleC (BinaryType set) depends on TestModuleB (BinaryType set).
        // C's link action must depend on B's link action and reference B's produced library,
        // since B's objects were absorbed into B's own binary rather than folded into C's.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/ModuleB", "ModuleB.cpp");
        WriteSourceFile("Source/ModuleC", "ModuleC.cpp");
        var moduleB = new TestModuleB(context) { SourceDirectory = "Source/ModuleB", BinaryType = TargetType.SharedLibrary };
        var moduleC = new TestModuleC(context) { SourceDirectory = "Source/ModuleC", BinaryType = TargetType.SharedLibrary };
        moduleC.PublicDependencies.Add(moduleB.Name);
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [moduleB, moduleC]);

        // Assert
        var moduleBLinkAction = graph.Actions.Single(a => a.ModuleName == moduleB.Name && a.Type is ActionType.Link or ActionType.Archive);
        var moduleCLinkAction = graph.Actions.Single(a => a.ModuleName == moduleC.Name && a.Type is ActionType.Link or ActionType.Archive);

        moduleCLinkAction.Dependencies.Should().Contain(moduleBLinkAction);

        var expectedModuleBLibrary = Path.ChangeExtension(Path.Combine(context.OutputDirectory, moduleB.Name + ".dll"), ".lib");
        moduleCLinkAction.CommandLine.Should().Contain(expectedModuleBLibrary);
    }

    [Fact]
    public void ModuleWithUnsupportedBinaryType_ThrowsInvalidOperationException()
    {
        // Arrange: BinaryType only supports SharedLibrary/StaticLibrary; anything else (e.g. an
        // Executable module) has no defined archive/link behavior and must fail loudly rather
        // than silently falling back to a shared library.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Runtime", "Runtime.cpp");
        var runtimeModule = new TestModule(context) { SourceDirectory = "Source/Runtime", BinaryType = TargetType.Executable };
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var act = () => builder.Build(target, [runtimeModule]);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*TestModule*Executable*");
    }
}
