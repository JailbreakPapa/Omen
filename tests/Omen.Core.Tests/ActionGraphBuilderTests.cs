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

    [Fact]
    public void MonolithicTarget_FoldsSharedLibraryModulesAndGeneratesRegistration()
    {
        // Arrange
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Camera", "Camera.cpp");
        var cameraModule = new TestModule(context) { SourceDirectory = "Source/Camera", BinaryType = TargetType.SharedLibrary };
        var target = new TestTarget(context) { Type = TargetType.Executable, LinkType = LinkType.Monolithic };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [cameraModule]);

        // Assert: no independent link action for the module (folded into the target link)
        graph.Actions.Should().NotContain(a => (a.Type == ActionType.Link || a.Type == ActionType.Archive) && a.Description.Contains("Link TestModule"));

        // Assert: a registration source file was generated listing the folded module
        var registrationPath = Path.Combine(context.IntermediateDirectory, "StaticModuleRegistration.g.cpp");
        File.Exists(registrationPath).Should().BeTrue();
        File.ReadAllText(registrationPath).Should().Contain("TestModule");
    }

    [Fact]
    public void MonolithicTarget_IndependentModuleDependingOnFoldedModule_DoesNotReferenceFoldedModule()
    {
        // Arrange: TestModuleC (BinaryType = StaticLibrary, stays independent under Monolithic)
        // depends on TestModuleB (BinaryType = SharedLibrary, gets folded into the aggregate
        // link under Monolithic). C must not treat B as a library to link against or depend on,
        // since B no longer produces its own artifact.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/ModuleB", "ModuleB.cpp");
        WriteSourceFile("Source/ModuleC", "ModuleC.cpp");
        var moduleB = new TestModuleB(context) { SourceDirectory = "Source/ModuleB", BinaryType = TargetType.SharedLibrary };
        var moduleC = new TestModuleC(context) { SourceDirectory = "Source/ModuleC", BinaryType = TargetType.StaticLibrary };
        moduleC.PublicDependencies.Add(moduleB.Name);
        var target = new TestTarget(context) { Type = TargetType.Executable, LinkType = LinkType.Monolithic };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [moduleB, moduleC]);

        // Assert: B was folded, so it has no independent link/archive action.
        graph.Actions.Should().NotContain(a => (a.Type == ActionType.Link || a.Type == ActionType.Archive) && a.ModuleName == moduleB.Name);

        // Assert: C is still independently archived (its BinaryType is StaticLibrary, not SharedLibrary).
        var moduleCLinkAction = graph.Actions.Should()
            .ContainSingle(a => a.ModuleName == moduleC.Name && (a.Type == ActionType.Link || a.Type == ActionType.Archive))
            .Which;
        moduleCLinkAction.Type.Should().Be(ActionType.Archive);

        // Assert: C's link command line does not reference a library path for the folded module B.
        var wouldBeModuleBLibrary = Path.ChangeExtension(Path.Combine(context.OutputDirectory, moduleB.Name + ".dll"), ".lib");
        moduleCLinkAction.CommandLine.Should().NotContain(wouldBeModuleBLibrary);

        // Assert: C's action has no dependency edge onto a (nonexistent) independent action for B.
        moduleCLinkAction.Dependencies.Should().NotContain(a => a.ModuleName == moduleB.Name);
    }

    [Fact]
    public void MonolithicTarget_StaticLibraryModule_StaysIndependentAndSkipsRegistration()
    {
        // Arrange: a StaticLibrary-BinaryType module is unaffected by monolithic folding —
        // the fold condition is specifically BinaryType == SharedLibrary — so it should still
        // get its own archive action, and since nothing was actually folded, no registration
        // file should be generated.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Runtime", "Runtime.cpp");
        var runtimeModule = new TestModule(context) { SourceDirectory = "Source/Runtime", BinaryType = TargetType.StaticLibrary };
        var target = new TestTarget(context) { Type = TargetType.Executable, LinkType = LinkType.Monolithic };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [runtimeModule]);

        // Assert: one independent archive action for the module, plus the target's own link action.
        var linkActions = graph.Actions.Where(a => a.Type is ActionType.Link or ActionType.Archive).ToList();
        var moduleArchiveAction = linkActions.Should().ContainSingle(a => a.Description.Contains("TestModule")).Which;
        moduleArchiveAction.Type.Should().Be(ActionType.Archive);
        var targetLinkAction = linkActions.Single(a => a.ModuleName is null);
        targetLinkAction.Dependencies.Should().Contain(moduleArchiveAction);

        // Assert: no module was folded, so no registration file was generated.
        File.Exists(Path.Combine(context.IntermediateDirectory, "StaticModuleRegistration.g.cpp")).Should().BeFalse();
    }

    [Fact]
    public void IndependentModule_DependingOnFoldedModule_ThrowsInvalidOperationException()
    {
        // Arrange: TestModuleC is independently linked (BinaryType set) and depends on
        // TestModuleB, a real known module whose BinaryType is unset - i.e. B is folded into
        // some other aggregate link rather than producing its own artifact. Silently omitting
        // B from C's link would produce missing symbols at link time with no diagnostic, so
        // this must fail loudly instead. This is distinct from a dependency name that doesn't
        // resolve to any known module at all (not asserted here - that's the pre-existing,
        // intentional "handled elsewhere" case and must not throw).
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/ModuleB", "ModuleB.cpp");
        WriteSourceFile("Source/ModuleC", "ModuleC.cpp");
        var moduleB = new TestModuleB(context) { SourceDirectory = "Source/ModuleB" }; // BinaryType left unset - folded
        var moduleC = new TestModuleC(context) { SourceDirectory = "Source/ModuleC", BinaryType = TargetType.SharedLibrary };
        moduleC.PublicDependencies.Add(moduleB.Name);
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var act = () => builder.Build(target, [moduleB, moduleC]);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{moduleC.Name}*{moduleB.Name}*");
    }

    [Fact]
    public void ModularTarget_KeepsSharedLibraryModulesIndependent()
    {
        // Arrange
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Camera", "Camera.cpp");
        var cameraModule = new TestModule(context) { SourceDirectory = "Source/Camera", BinaryType = TargetType.SharedLibrary };
        var target = new TestTarget(context) { Type = TargetType.Executable, LinkType = LinkType.Modular };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [cameraModule]);

        // Assert
        graph.Actions.Should().Contain(a => a.Type == ActionType.Link && a.Description == "Link TestModule");
        File.Exists(Path.Combine(context.IntermediateDirectory, "StaticModuleRegistration.g.cpp")).Should().BeFalse();
    }
}
