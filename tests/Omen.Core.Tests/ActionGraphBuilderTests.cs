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

    private static BuildContext CreateTestContext(
        string projectRoot, BuildConfiguration configuration = BuildConfiguration.Development) => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = configuration,
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

        /// <summary>For a test that needs two distinct module names without a second class.</summary>
        public TestModule(BuildContext context, string name) : base(context, explicitName: name) { }
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
    public void LinkOfManyObjects_SpillsToAResponseFile()
    {
        // Arrange - Windows caps a command line at 32,767 characters. Linking NightFoxCore is
        // 290 objects at roughly 90 characters of absolute path each, which clears the cap on
        // its own: the linker never sees the arguments and reports only "The command line is
        // too long."
        var context = CreateTestContext(_projectRoot);
        for (var i = 0; i < 120; i++)
        {
            WriteSourceFile("Source/Runtime", $"AVeryLongSourceFileNameToPushThePathOut{i}.cpp");
        }

        var module = new TestModule(context) { SourceDirectory = "Source/Runtime", BinaryType = TargetType.SharedLibrary };
        // One object per source: unity would fold these into a handful and never reach the cap.
        var target = new TestTarget(context) { Type = TargetType.Executable, UseUnityBuild = false };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [module]);

        // Assert
        var linkAction = graph.Actions.Single(a => a.Type is ActionType.Link && a.Description.Contains("TestModule"));
        // cmd.exe caps at 8,191 - actions run through the shell, not CreateProcess directly.
        linkAction.CommandLine.Length.Should().BeLessThan(8191);
        linkAction.CommandLine.Should().Contain("@");

        var responseFile = linkAction.CommandLine.Split('@')[1].Trim('"');
        File.Exists(responseFile).Should().BeTrue();
        File.ReadAllText(responseFile).Should().Contain("AVeryLongSourceFileNameToPushThePathOut0.obj");
    }

    [Fact]
    public void IndependentModule_BinaryDropsTheNamespaceQualifier()
    {
        // "NF::NightFoxCore" is how other rules refer to the module; the artifact is
        // NightFoxCore.dll, the way O3DE's AZ::AzCore has always produced AzCore.dll.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Runtime", "Runtime.cpp");
        var module = new TestModule(context, "NF::NightFoxCore")
        {
            SourceDirectory = "Source/Runtime",
            BinaryType = TargetType.SharedLibrary
        };
        var target = new TestTarget(context) { Type = TargetType.Executable };

        var graph = CreateBuilder(context).Build(target, [module]);

        var link = graph.Actions.Single(a => a.ModuleName == module.Name && a.Type is ActionType.Link);
        link.Outputs.Single().Path.Should().Be(Path.Combine(context.OutputDirectory, "NightFoxCore.dll"));
        // The PDB and every other file named after the output follow it. The intermediate
        // directory deliberately keeps the qualifier, so this checks the output directory only.
        link.CommandLine.Should().Contain(Path.Combine(context.OutputDirectory, "NightFoxCore.pdb"));
        link.CommandLine.Should().NotContain(Path.Combine(context.OutputDirectory, "NF__"));
    }

    [Fact]
    public void IndependentModule_GetsLibrariesFromAnIndirectDependency()
    {
        // A vendored package two levels down: NightFoxFramework -> NightFoxNetworking -> OpenSSL.
        // The framework's DLL is where libssl's symbols resolve, so a one-level walk leaves them
        // unresolved while naming a library that never reached the command line.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/App", "App.cpp");
        WriteSourceFile("Source/Middle", "Middle.cpp");

        var package = new TestModule(context, "3rdParty::Package")
        {
            SourceDirectory = "Source/Package",
            Type = ModuleType.ThirdParty,
            // What a vendored package is: compiles nothing, contributes an already-built .lib.
            BinaryType = TargetType.HeaderOnly
        };
        package.PublicLibraries.Add(Path.Combine(_projectRoot, "libpackage.lib"));
        package.PublicSystemLibraries.Add("ws2_32");

        var middle = new TestModuleB(context) { SourceDirectory = "Source/Middle", BinaryType = TargetType.StaticLibrary };
        middle.ThirdPartyDependencies.Add(package.Name);

        var app = new TestModuleC(context) { SourceDirectory = "Source/App", BinaryType = TargetType.SharedLibrary };
        app.PublicDependencies.Add(middle.Name);

        var target = new TestTarget(context) { Type = TargetType.Executable };
        var graph = CreateBuilder(context).Build(target, [package, middle, app]);

        var appLink = graph.Actions.Single(a => a.ModuleName == app.Name && a.Type is ActionType.Link);
        appLink.CommandLine.Should().ContainAll("libpackage.lib", "ws2_32.lib");
    }

    [Fact]
    public void WindowsLink_IncludesTheWin32StandardLibraries()
    {
        // CMake appends CMAKE_CXX_STANDARD_LIBRARIES to every MSVC link; invoking link.exe
        // directly does not, so a module calling RegOpenKeyExW or SHGetFolderPathW failed with
        // unresolved __imp_ symbols that named no missing source file.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Launch", "Launch.cpp");
        var module = new TestModule(context) { SourceDirectory = "Source/Launch" };
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        var graph = builder.Build(target, [module]);

        var linkAction = graph.Actions.Single(a => a.Type is ActionType.Link && a.ModuleName is null);
        linkAction.CommandLine.Should().ContainAll("advapi32.lib", "user32.lib", "shell32.lib", "kernel32.lib");
    }

    [Fact]
    public void ModuleWithBinaryType_GetsItsOwnLinkAction()
    {
        // Arrange
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Runtime", "Runtime.cpp");
        WriteSourceFile("Source/Launch", "Launch.cpp");
        var runtimeModule = new TestModule(context) { SourceDirectory = "Source/Runtime", BinaryType = TargetType.SharedLibrary };
        // The target needs objects of its own: an .exe built from an import library alone has no
        // entry point, and Omen now declines to emit a link action that could not succeed.
        var launchModule = new TestModuleB(context) { SourceDirectory = "Source/Launch" };
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [runtimeModule, launchModule]);

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

    [Theory]
    [InlineData(BuildConfiguration.Debug, "/Od")]
    [InlineData(BuildConfiguration.Development, "/O1")]
    [InlineData(BuildConfiguration.Release, "/O2")]
    public void CompileFlags_OptimizationFollowsTheConfiguration(BuildConfiguration configuration, string expected)
    {
        // Development is the optimized-with-debug-info configuration; it used to map to /Od,
        // so every Development build was unoptimized and collided with any project-supplied
        // /O2 ("D9025: overriding '/Od' with '/O2'").
        var context = CreateTestContext(_projectRoot, configuration);
        WriteSourceFile("Source/Thing", "Thing.cpp");
        var module = new TestModule(context) { SourceDirectory = "Source/Thing", BinaryType = TargetType.StaticLibrary };
        var graph = CreateBuilder(context).Build(new TestTarget(context) { Type = TargetType.Executable }, [module]);

        var compile = graph.Actions.First(a => a.Type == ActionType.Compile);
        compile.CommandLine.Should().Contain(expected);
    }

    [Fact]
    public void Definition_WithASpace_IsOneQuotedArgument()
    {
        // A path value (HOUDINI_ENGINE_BIN_DIR="C:/Program Files/...") split at the space into two arguments,
        // and cl reported C2001 "newline in string literal" for every file of the module.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Thing", "Thing.cpp");
        var module = new TestModule(context) { SourceDirectory = "Source/Thing", BinaryType = TargetType.StaticLibrary };
        module.PrivateDefinitions.Add("BIN_DIR=\"C:/Program Files/Tool/bin\"");
        module.PrivateDefinitions.Add("PLAIN=1");
        var graph = CreateBuilder(context).Build(new TestTarget(context) { Type = TargetType.StaticLibrary, UseUnityBuild = false }, [module]);

        var compile = graph.Actions.First(a => a.Type == ActionType.Compile);
        compile.CommandLine.Should().Contain("/D\"BIN_DIR=\\\"C:/Program Files/Tool/bin\\\"\"");
        compile.CommandLine.Should().Contain("/DPLAIN=1");
    }

    [Fact]
    public void CompileFlags_CSourceOmitsTheCppStandard()
    {
        // BuildCompileCommandLine used to add the C++ /std: value unconditionally, so a .c file in
        // a module that set its own /std:c17 via AdditionalCompilerFlags (the only way a C module
        // can request a standard) got both on one command line - cl rejects that combination with
        // "D8016: '/std:c++20' and '/std:c17' command-line options are incompatible", regardless of
        // which one comes last.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Thing", "Thing.c");
        var module = new TestModule(context) { SourceDirectory = "Source/Thing", BinaryType = TargetType.StaticLibrary };
        module.AdditionalCompilerFlags.Add("/std:c17");
        var graph = CreateBuilder(context).Build(new TestTarget(context) { Type = TargetType.Executable }, [module]);

        var compile = graph.Actions.First(a => a.Type == ActionType.Compile);
        compile.CommandLine.Should().NotContain("/std:c++");
        compile.CommandLine.Should().Contain("/std:c17");
    }

    [Fact]
    public void ExecutableModule_LinksToAnExecutable_AndIsNotOfferedToDependentsAsALibrary()
    {
        // An Executable module is a program in its own right - O3DE's `ly_add_target ...
        // EXECUTABLE` (AssetProcessorBatch, AzTestRunner, ProcessLaunchTest). It links like a
        // shared library minus /DLL, and must never appear on a dependent's linker line: an
        // .exe is not something you link against.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Tool", "Tool.cpp");
        WriteSourceFile("Source/Consumer", "Consumer.cpp");

        var toolModule = new TestModule(context, "ToolProgram")
        {
            SourceDirectory = "Source/Tool",
            BinaryType = TargetType.Executable
        };
        var consumerModule = new TestModule(context, "Consumer")
        {
            SourceDirectory = "Source/Consumer",
            BinaryType = TargetType.StaticLibrary
        };
        consumerModule.PrivateDependencies.Add("ToolProgram");

        var target = new TestTarget(context) { Type = TargetType.Executable };
        var graph = CreateBuilder(context).Build(target, [toolModule, consumerModule]);

        var toolLink = graph.Actions.Single(a => a.ModuleName == "ToolProgram" && a.Type == ActionType.Link);
        toolLink.Outputs.Single().Path.Should().EndWith("ToolProgram.exe");

        var consumerArchive = graph.Actions.Single(a => a.ModuleName == "Consumer" && a.Type == ActionType.Archive);
        consumerArchive.CommandLine.Should().NotContain("ToolProgram.exe");
    }

    [Fact]
    public void IndependentModule_LinkCarriesItsOwnLinkerFlags()
    {
        // A game launcher defines WinMain only, in a static library, so its own link needs
        // /SUBSYSTEM:WINDOWS; without the flag link.exe stops at LNK1561.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Launcher", "Launcher.cpp");

        var launcher = new TestModule(context) { SourceDirectory = "Source/Launcher", BinaryType = TargetType.Executable };
        launcher.AdditionalLinkerFlags.Add("/SUBSYSTEM:WINDOWS");

        var target = new TestTarget(context) { Type = TargetType.Executable };
        var graph = CreateBuilder(context).Build(target, [launcher]);

        var link = graph.Actions.Single(a => a.ModuleName == launcher.Name && a.Type == ActionType.Link);
        link.CommandLine.Should().Contain("/SUBSYSTEM:WINDOWS");
    }

    [Fact]
    public void IndependentModule_LinkCarriesGlobalLinkerOptionsBeforeItsOwn()
    {
        // O3DE's Development options (/opt:ref, /opt:icf) apply to every module DLL. With /DEBUG and without
        // them, link.exe keeps unreferenced code and never folds identical COMDATs.
        var context = CreateTestContext(_projectRoot);
        context.GlobalLinkerOptions = ["/opt:ref", "/opt:icf"];
        WriteSourceFile("Source/Plugin", "Plugin.cpp");

        var plugin = new TestModule(context) { SourceDirectory = "Source/Plugin", BinaryType = TargetType.SharedLibrary };
        plugin.AdditionalLinkerFlags.Add("/opt:noicf");

        var target = new TestTarget(context) { Type = TargetType.Executable };
        var graph = CreateBuilder(context).Build(target, [plugin]);

        var link = graph.Actions.Single(a => a.ModuleName == plugin.Name && a.Type == ActionType.Link);
        link.CommandLine.Should().Contain("/opt:ref");
        link.CommandLine.IndexOf("/opt:icf", StringComparison.Ordinal)
            .Should().BeLessThan(link.CommandLine.IndexOf("/opt:noicf", StringComparison.Ordinal));
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
        WriteSourceFile("Source/Launch", "Launch.cpp");
        var runtimeModule = new TestModule(context) { SourceDirectory = "Source/Runtime", BinaryType = TargetType.StaticLibrary };
        // Folded (no BinaryType), so it gives the target the objects its link action needs without
        // being a SharedLibrary and thus without triggering registration.
        var launchModule = new TestModuleB(context) { SourceDirectory = "Source/Launch" };
        var target = new TestTarget(context) { Type = TargetType.Executable, LinkType = LinkType.Monolithic };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [runtimeModule, launchModule]);

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
    public void IndependentModule_AbsorbsAnObjectLibraryDependencysObjects()
    {
        // TestModuleC is independently linked and depends on TestModuleB, whose BinaryType is
        // unset - it produces no artifact of its own. That is CMake's OBJECT library, which O3DE
        // writes as `ly_add_target(NAME <x>.Private.Object OBJECT)` to share one set of private
        // objects between a plugin module and its test module. B's objects belong in C's link.
        //
        // This once threw, because there was no way to get them there and omitting them silently
        // would have been missing symbols with no diagnostic. Absorbing them is the behaviour the
        // throw was standing in for.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/ModuleB", "ModuleB.cpp");
        WriteSourceFile("Source/ModuleC", "ModuleC.cpp");
        var moduleB = new TestModuleB(context) { SourceDirectory = "Source/ModuleB" }; // no BinaryType - object library
        var moduleC = new TestModuleC(context) { SourceDirectory = "Source/ModuleC", BinaryType = TargetType.SharedLibrary };
        moduleC.PublicDependencies.Add(moduleB.Name);
        var target = new TestTarget(context) { Type = TargetType.Executable };

        var graph = CreateBuilder(context).Build(target, [moduleB, moduleC]);

        var moduleCLink = graph.Actions.Single(a => a.ModuleName == moduleC.Name && a.Type is ActionType.Link);
        moduleCLink.CommandLine.Should().Contain("ModuleB.obj", "B's objects link into its consumer");

        // And not into the target as well, which would put the same code in two binaries.
        var targetLink = graph.Actions.SingleOrDefault(a => a.ModuleName is null && a.Type is ActionType.Link);
        targetLink.Should().BeNull("the target has no objects of its own once B belongs to C");
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

    [Fact]
    public void IndependentStaticLibraryModule_UsesArchiverNotLinker()
    {
        // Regression test: an independently-linked StaticLibrary module was tagged
        // ActionType.Archive correctly, but its actual command line was still built via
        // BuildLinkCommandLine (the linker), never a dedicated archiver command - so it
        // invoked link.exe with linker flags on a .lib output, which fails with
        // "LNK1561: entry point must be defined" since link.exe expects to produce an
        // executable or DLL, not bundle an archive. Caught by an actual end-to-end build.

        // Arrange
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Runtime", "Runtime.cpp");
        var module = new TestModule(context) { SourceDirectory = "Source/Runtime", BinaryType = TargetType.StaticLibrary };
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [module]);

        // Assert
        var archiveAction = graph.Actions.Single(a => a.Type == ActionType.Archive && a.ModuleName == "TestModule");
        archiveAction.CommandLine.Should().StartWith("\"lib.exe\"");
        archiveAction.CommandLine.Should().NotContain("link.exe");
        archiveAction.CommandLine.Should().NotContain("/DEBUG");
        archiveAction.CommandLine.Should().NotContain("/INCREMENTAL");
    }

    [Fact]
    public void ModuleWithExplicitSourceFiles_OnlyCompilesListedFiles()
    {
        // Regression coverage for the Camera-plugin gap: several O3DE targets can share one
        // Source/ directory but name different files via FILES_CMAKE (Camera's .Static,
        // runtime and .Editor targets all point at Source/). Globbing the directory for
        // every flavor would compile editor-only translation units into the runtime module.
        // An explicit, non-empty SourceFiles list must be authoritative.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Shared", "Included.cpp");
        WriteSourceFile("Source/Shared", "AlsoIncluded.cpp");
        WriteSourceFile("Source/Shared", "EditorOnly.cpp"); // present on disk, not listed
        var module = new TestModule(context) { SourceDirectory = "Source/Shared" };
        module.SourceFiles.Add("Included.cpp");
        module.SourceFiles.Add("AlsoIncluded.cpp");
        var target = new TestTarget(context) { Type = TargetType.StaticLibrary, UseUnityBuild = false };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [module]);

        // Assert
        var compileActions = graph.Actions.Where(a => a.Type == ActionType.Compile).ToList();
        compileActions.Should().HaveCount(2);
        compileActions.Should().Contain(a => a.Description.Contains("Included.cpp"));
        compileActions.Should().Contain(a => a.Description.Contains("AlsoIncluded.cpp"));
        compileActions.Should().NotContain(a => a.Description.Contains("EditorOnly.cpp"));
    }

    [Fact]
    public void StaticLibraryTarget_UsesArchiverNotLinker()
    {
        // Same bug, target-level path: a TargetType.StaticLibrary target's aggregate link
        // action was also tagged ActionType.Archive but built its command line via
        // BuildLinkCommandLine. This predates this session's work (the ActionType/CommandLine
        // split existed in the original codebase) - exercised here since a StaticLibrary
        // target was never actually built end-to-end before.

        // Arrange
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/TestModule", "TestModule.cpp");
        var module = new TestModule(context);
        var target = new TestTarget(context) { Type = TargetType.StaticLibrary };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [module]);

        // Assert
        var archiveAction = graph.Actions.Single(a => a.Type == ActionType.Archive && a.ModuleName == null);
        archiveAction.CommandLine.Should().StartWith("\"lib.exe\"");
        archiveAction.CommandLine.Should().NotContain("link.exe");
        archiveAction.CommandLine.Should().NotContain("/DEBUG");
        archiveAction.CommandLine.Should().NotContain("/INCREMENTAL");
    }

    // Regression coverage for the plugin "Api"/"EditorApi" flavors (O3DE's modern-layout
    // `${plugin_name}.API` INTERFACE targets): a HeaderOnly module must produce zero compile
    // actions and no independent link/archive action, while still contributing its
    // PublicIncludePaths to a dependent module's compile actions. Without the
    // ActionGraphBuilder fix, a HeaderOnly module's BinaryType.HasValue == true made
    // LinksIndependently return true, and BuildModuleBinaryAction has no case for
    // TargetType.HeaderOnly - it would throw InvalidOperationException instead of silently
    // (and correctly) doing nothing.

    [Fact]
    public void HeaderOnlyModule_ProducesNoCompileOrLinkActions()
    {
        // Arrange: an API-flavor-shaped module - headers only, no .cpp/.c in its directory.
        var context = CreateTestContext(_projectRoot);
        var headerDir = Path.Combine(_projectRoot, "Source/Api");
        Directory.CreateDirectory(headerDir);
        File.WriteAllText(Path.Combine(headerDir, "Public.h"), "#pragma once\n");
        var apiModule = new TestModule(context) { SourceDirectory = "Source/Api", BinaryType = TargetType.HeaderOnly };
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [apiModule]);

        // Assert: no compile action (nothing to compile) and no link/archive action (an
        // INTERFACE target produces no artifact of its own).
        graph.Actions.Should().NotContain(a => a.ModuleName == apiModule.Name);
    }

    [Fact]
    public void ModuleDependingOnHeaderOnlyModule_InheritsItsPublicIncludePaths()
    {
        // Arrange: TestModuleB stands in for a plugin's ".API" flavor (HeaderOnly, an explicit
        // PublicIncludePaths entry for its public headers); TestModuleC stands in for the
        // plugin's Runtime module, which PUBLIC-depends on ".API" in real O3DE.
        var context = CreateTestContext(_projectRoot);
        var apiDir = Path.Combine(_projectRoot, "Source/ModuleB");
        Directory.CreateDirectory(Path.Combine(apiDir, "Public"));
        WriteSourceFile("Source/ModuleC", "ModuleC.cpp");
        var apiModule = new TestModuleB(context) { SourceDirectory = "Source/ModuleB", BinaryType = TargetType.HeaderOnly };
        apiModule.PublicIncludePaths.Add("Public");
        var runtimeModule = new TestModuleC(context) { SourceDirectory = "Source/ModuleC" };
        runtimeModule.PublicDependencies.Add(apiModule.Name);
        var target = new TestTarget(context) { Type = TargetType.Executable };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [apiModule, runtimeModule]);

        // Assert: the dependent module's compile action includes the HeaderOnly dependency's
        // public include path (ActionGraphBuilder.BuildIncludePaths's existing dependency
        // walk - unchanged by this task, exercised here specifically for a HeaderOnly dep).
        var compileAction = graph.Actions.Single(a => a.ModuleName == runtimeModule.Name && a.Type == ActionType.Compile);
        compileAction.CommandLine.Should().Contain(Path.Combine(apiDir, "Public"));
    }

    [Fact]
    public void RuntimeDependency_ResolvesToKnownSharedLibraryModule_EmitsCopyActionToTargetOutputDirectory()
    {
        // Arrange: "Dependency" is a SharedLibrary module built (like every module) into
        // context.OutputDirectory; "Consumer" (folded into the executable's own link, no
        // BinaryType) declares Dependency as a RUNTIME_DEPENDENCIES-style runtime-only
        // dependency. The target's own output lands somewhere else entirely, so the
        // dependency's built .dll needs staging next to it for the exe to actually run.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Dependency", "Dependency.cpp");
        WriteSourceFile("Source/Consumer", "Consumer.cpp");
        var dependencyModule = new TestModule(context) { SourceDirectory = "Source/Dependency", BinaryType = TargetType.SharedLibrary };
        var consumerModule = new TestModuleB(context) { SourceDirectory = "Source/Consumer" };
        consumerModule.RuntimeDependencies.Add("Plugin::" + dependencyModule.Name);
        var target = new TestTarget(context) { Type = TargetType.Executable, OutputDirectory = Path.Combine(_projectRoot, "Staged") };
        var builder = CreateBuilder(context);

        // Act
        var graph = builder.Build(target, [dependencyModule, consumerModule]);

        // Assert
        var copyAction = graph.Actions.Should().ContainSingle(a => a.Type == ActionType.Copy).Subject;
        copyAction.Inputs.Should().ContainSingle(f => f.Path == Path.Combine(context.OutputDirectory, dependencyModule.Name + ".dll"));
        copyAction.Outputs.Should().ContainSingle(f => f.Path == Path.Combine(target.OutputDirectory, dependencyModule.Name + ".dll"));
        // Runs after the dependency's own link action.
        var dependencyLinkAction = graph.Actions.Single(a => a.ModuleName == dependencyModule.Name && a.Type == ActionType.Link);
        copyAction.Dependencies.Should().Contain(dependencyLinkAction);
    }

    [Fact]
    public void CompileAction_ListsTransitivelyIncludedHeadersAsInputs()
    {
        // The up-to-date check compares input timestamps; with only the .cpp (and PCH) as
        // inputs, editing a header invalidated nothing. Headers a TU reaches - directly or
        // through another header - must be inputs.
        var context = CreateTestContext(_projectRoot);
        var srcDir = Path.Combine(_projectRoot, "Source/Thing");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Inner.h"), "#pragma once\n");
        File.WriteAllText(Path.Combine(srcDir, "Outer.h"), "#pragma once\n#include \"Inner.h\"\n");
        File.WriteAllText(Path.Combine(srcDir, "Thing.cpp"), "#include \"Outer.h\"\n");
        var module = new TestModule(context) { SourceDirectory = "Source/Thing" };
        var graph = CreateBuilder(context).Build(new TestTarget(context) { Type = TargetType.StaticLibrary }, [module]);

        var compile = graph.Actions.Single(a => a.Type == ActionType.Compile);
        var inputPaths = compile.Inputs.Select(i => i.Path).ToList();
        inputPaths.Should().Contain(p => p.EndsWith("Outer.h"));
        inputPaths.Should().Contain(p => p.EndsWith("Inner.h"));
    }

    [Fact]
    public void UnityBuild_CombinesSourcesAndStillCompilesExclusionsIndividually()
    {
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Big", "A.cpp");
        WriteSourceFile("Source/Big", "B.cpp");
        WriteSourceFile("Source/Big", "Standalone.cpp");
        var module = new TestModule(context) { SourceDirectory = "Source/Big", UseUnityBuild = true };
        module.UnityBuildExclusions.Add("Standalone.cpp");
        var target = new TestTarget(context) { Type = TargetType.StaticLibrary };

        var graph = CreateBuilder(context).Build(target, [module]);

        var compiles = graph.Actions.Where(a => a.Type == ActionType.Compile).ToList();
        compiles.Should().HaveCount(2);
        var unityCompile = compiles.Should().ContainSingle(a => a.Description.Contains("Unity")).Which;
        compiles.Should().ContainSingle(a => a.Description.Contains("Standalone.cpp"));

        // The unity file was written, includes the absorbed sources, and those sources are
        // inputs of the unity compile (via the include scan), so editing A.cpp rebuilds it.
        var unitySource = unityCompile.Inputs[0].Path;
        File.ReadAllText(unitySource).Should().Contain("A.cpp").And.Contain("B.cpp").And.NotContain("Standalone.cpp");
        unityCompile.Inputs.Select(i => i.Path).Should().Contain(p => p.EndsWith("A.cpp"));
    }

    [Fact]
    public void UnityBuild_MixedCAndCpp_CompileToDistinctObjects()
    {
        // TinyExr (tinyexr.cpp + miniz.c): the C and C++ unity files shared a stem, so both compiled to
        // Unity0.obj and the link lost one of them (LNK2019 mz_compress).
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Mixed", "A.cpp");
        WriteSourceFile("Source/Mixed", "B.cpp");
        WriteSourceFile("Source/Mixed", "C1.c");
        WriteSourceFile("Source/Mixed", "C2.c");
        var module = new TestModule(context) { SourceDirectory = "Source/Mixed", UseUnityBuild = true };
        var graph = CreateBuilder(context).Build(new TestTarget(context) { Type = TargetType.StaticLibrary }, [module]);

        var objects = graph.Actions.Where(a => a.Type == ActionType.Compile).Select(a => a.Outputs[0].Path).ToList();
        objects.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void UnityBuild_OnByDefault_CombinesSources()
    {
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Plain", "A.cpp");
        WriteSourceFile("Source/Plain", "B.cpp");
        var module = new TestModule(context) { SourceDirectory = "Source/Plain" };
        var graph = CreateBuilder(context).Build(new TestTarget(context) { Type = TargetType.StaticLibrary }, [module]);

        graph.Actions.Where(a => a.Type == ActionType.Compile).Should().ContainSingle(a => a.Description.Contains("Unity"));
    }

    [Fact]
    public void UnityBuild_TargetOptOut_CompilesEachSourceIndividually()
    {
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Plain", "A.cpp");
        WriteSourceFile("Source/Plain", "B.cpp");
        var module = new TestModule(context) { SourceDirectory = "Source/Plain" };
        var target = new TestTarget(context) { Type = TargetType.StaticLibrary, UseUnityBuild = false };
        var graph = CreateBuilder(context).Build(target, [module]);

        graph.Actions.Where(a => a.Type == ActionType.Compile).Should().HaveCount(2);
        graph.Actions.Should().NotContain(a => a.Description.Contains("Unity"));
    }

    [Fact]
    public void UnityBuild_SanitizesNamespacedModuleNameInTheUnityFilePath()
    {
        // A namespaced module ("NF::Widgets") carries a colon, which is illegal in a Windows
        // path - the same failure ModulePathName's own doc comment describes for intermediate
        // directories. The unity file path is built from the module name too.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Widgets", "A.cpp");
        WriteSourceFile("Source/Widgets", "B.cpp");
        var module = new TestModule(context, "NF::Widgets") { SourceDirectory = "Source/Widgets", UseUnityBuild = true };
        var target = new TestTarget(context) { Type = TargetType.StaticLibrary };

        var graph = CreateBuilder(context).Build(target, [module]);

        var unityCompile = graph.Actions.Should().ContainSingle(a => a.Type == ActionType.Compile && a.Description.Contains("Unity")).Which;
        var unityPath = unityCompile.Inputs[0].Path;
        Path.GetFileName(unityPath).IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
        File.Exists(unityPath).Should().BeTrue();
    }

    [Fact]
    public void CodeGenStep_GeneratesActionsAndCompilesGeneratedSources()
    {
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Gen", "Handwritten.cpp");
        var module = new TestModule(context) { SourceDirectory = "Source/Gen" };
        module.CodeGenSteps.Add(new CodeGenStep
        {
            Description = "Generate build info",
            Command = "echo generated > \"$(IntermediateDir)/BuildInfo.h\"",
            Inputs = { "Handwritten.cpp" },
            Outputs = { "BuildInfo.h", "BuildInfo.cpp" }
        });
        var target = new TestTarget(context) { Type = TargetType.StaticLibrary };

        var graph = CreateBuilder(context).Build(target, [module]);

        var moduleIntermediate = Path.Combine(context.IntermediateDirectory, module.Name);
        var codeGen = graph.Actions.Single(a => a.Type == ActionType.Custom);
        codeGen.CommandLine.Should().Contain(moduleIntermediate, "the $(IntermediateDir) token expands");
        codeGen.Outputs.Select(o => o.Path).Should().BeEquivalentTo(
            Path.Combine(moduleIntermediate, "BuildInfo.h"),
            Path.Combine(moduleIntermediate, "BuildInfo.cpp"));
        codeGen.Inputs.Should().ContainSingle().Which.Path.Should().EndWith("Handwritten.cpp");

        // The generated .cpp compiles, and every compile in the module orders after the step.
        var compiles = graph.Actions.Where(a => a.Type == ActionType.Compile).ToList();
        compiles.Should().HaveCount(2);
        compiles.Should().OnlyContain(c => c.Dependencies.Contains(codeGen));
        var generatedCompile = compiles.Single(c => c.Description.Contains("BuildInfo.cpp"));
        generatedCompile.Inputs[0].Path.Should().Be(Path.Combine(moduleIntermediate, "BuildInfo.cpp"));

        // Generated headers resolve: the module's intermediate dir is on its include path.
        compiles.Should().OnlyContain(c => c.CommandLine.Contains(moduleIntermediate));
    }

    [Fact]
    public void CodeGenStep_OrdersDependentModulesCompilesAfterIt()
    {
        // AddAutoGen publishes its output directory as a public include path, so a module two
        // dependency links away can include a generated header and must not race the generator.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Gen", "Gen.cpp");
        WriteSourceFile("Source/Middle", "Middle.cpp");
        WriteSourceFile("Source/Consumer", "Consumer.cpp");
        var gen = new TestModule(context, "Gen") { SourceDirectory = "Source/Gen", BinaryType = TargetType.StaticLibrary };
        gen.CodeGenSteps.Add(new CodeGenStep
        {
            Description = "Generate header",
            Command = "echo x > \"$(IntermediateDir)/Gen.generated.h\"",
            Outputs = { "Gen.generated.h" }
        });
        var middle = new TestModule(context, "Middle") { SourceDirectory = "Source/Middle", BinaryType = TargetType.StaticLibrary };
        middle.PublicDependencies.Add(gen.Name);
        var consumer = new TestModule(context, "Consumer") { SourceDirectory = "Source/Consumer" };
        consumer.PrivateDependencies.Add(middle.Name);

        var graph = CreateBuilder(context).Build(new TestTarget(context) { Type = TargetType.StaticLibrary }, [consumer, middle, gen]);

        var codeGen = graph.Actions.Single(a => a.Type == ActionType.Custom);
        var compiles = graph.Actions.Where(a => a.Type == ActionType.Compile).ToList();
        compiles.Should().HaveCount(3);
        compiles.Should().OnlyContain(c => c.Dependencies.Contains(codeGen));
    }

    [Fact]
    public void CodeGenStep_WithoutOutputs_Throws()
    {
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Gen", "A.cpp");
        var module = new TestModule(context) { SourceDirectory = "Source/Gen" };
        module.CodeGenSteps.Add(new CodeGenStep { Description = "bad", Command = "echo x" });

        var act = () => CreateBuilder(context).Build(new TestTarget(context), [module]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*declares no Outputs*");
    }

    [Fact]
    public void CodeGenOnlyModule_WithNoDiskSources_StillBuilds()
    {
        // A module whose every translation unit is generated (the .setreg/AzAutoGen shape).
        var context = CreateTestContext(_projectRoot);
        var module = new TestModule(context) { SourceDirectory = "Source/PureGen" };
        module.CodeGenSteps.Add(new CodeGenStep
        {
            Description = "Generate everything",
            Command = "echo x > \"$(IntermediateDir)/All.cpp\"",
            Outputs = { "All.cpp" }
        });

        var graph = CreateBuilder(context).Build(new TestTarget(context) { Type = TargetType.StaticLibrary }, [module]);

        graph.Actions.Should().Contain(a => a.Type == ActionType.Custom);
        graph.Actions.Should().Contain(a => a.Type == ActionType.Compile && a.Description.Contains("All.cpp"));
    }

    [Fact]
    public void RuntimeDependency_UnresolvableExternalName_ProducesNoCopyActionButIsStillRecordedOnModule()
    {
        // "Legacy::EditorCommon"-style entries never resolve to a module Omen itself
        // compiled in this Build() call - recorded on ModuleRules.RuntimeDependencies for
        // inspection, but no action is (or safely could be) synthesized for it.
        var context = CreateTestContext(_projectRoot);
        WriteSourceFile("Source/Consumer", "Consumer.cpp");
        var consumerModule = new TestModule(context) { SourceDirectory = "Source/Consumer" };
        consumerModule.RuntimeDependencies.Add("Legacy::EditorCommon");
        var target = new TestTarget(context) { Type = TargetType.Executable, OutputDirectory = Path.Combine(_projectRoot, "Staged") };
        var builder = CreateBuilder(context);

        var graph = builder.Build(target, [consumerModule]);

        graph.Actions.Should().NotContain(a => a.Type == ActionType.Copy);
        consumerModule.RuntimeDependencies.Should().Contain("Legacy::EditorCommon");
    }
}
