// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Build;
using Omen.Core.Reflect;

namespace Omen.Core.Tests;

/// <summary>
/// Tests for synthesizing reflection codegen steps from a scan of a module's own headers.
///
/// Same shape as QtCodeGenSynthesizerTests, for the same reason: which headers need generating is
/// a property of their contents, so it is discovered rather than declared.
/// </summary>
public class ReflectCodeGenSynthesizerTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourceDir;

    public ReflectCodeGenSynthesizerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OmenReflectGen_" + Guid.NewGuid().ToString("N"));
        _sourceDir = Path.Combine(_root, "Source", "ReflectModule");
        Directory.CreateDirectory(_sourceDir);

        // The synthesizer finds the engine by finding the generator, so the fixture is one.
        var toolDir = Path.Combine(_root, "Tools", "ReflectCodeGen");
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "ReflectCodeGen.py"), string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteHeader(string relativePath, string body)
    {
        var full = Path.Combine(_sourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    [Fact]
    public void CreateSteps_EmitsOneStepPerAnnotatedHeader()
    {
        WriteHeader("HotspotSheet.h", "NF_CLASS()\nstruct HotspotSheet {};");
        WriteHeader("Mover.h", "NF_CLASS(Bases = \"NF::Component\")\nstruct Mover {};");

        Synthesize().Should().HaveCount(2);
    }

    [Fact]
    public void CreateSteps_SkipsHeadersWithoutAnAnnotation()
    {
        WriteHeader("HotspotSheet.h", "NF_CLASS()\nstruct HotspotSheet {};");
        WriteHeader("PlainStruct.h", "struct PlainStruct { int x; };");

        Synthesize().Should().ContainSingle()
            .Which.Inputs.Should().Contain(i => i.EndsWith("HotspotSheet.h"));
    }

    [Fact]
    public void CreateSteps_SkipsTheHeaderThatDefinesTheMacros()
    {
        // NightFoxReflect.h contains "#define NF_CLASS(...)" and reflects nothing itself.
        WriteHeader("NightFoxReflect.h", "#define NF_CLASS(...)\n#define NF_PROPERTY(...)\n");

        Synthesize().Should().BeEmpty();
    }

    [Fact]
    public void CreateSteps_IgnoresTheEnginesOwnComponentMacro()
    {
        // Component.h defines NF_COMPONENT (ex-AZ_COMPONENT). 471 headers use it and reflect
        // by hand; scanning for it would claim every one of them.
        WriteHeader("Collider.h", "struct Collider { NF_COMPONENT(Collider, \"{B60C4D82}\"); };");

        Synthesize().Should().BeEmpty();
    }

    [Fact]
    public void CreateSteps_OutputsAGeneratedCppNamedAfterTheHeader()
    {
        WriteHeader("HotspotSheet.h", "NF_CLASS()\nstruct HotspotSheet {};");

        // A .cpp output is what makes CodeGenStep compile it into the module.
        Synthesize()[0].Outputs.Should().ContainSingle().Which.Should().Be("HotspotSheet.generated.cpp");
    }

    [Fact]
    public void CreateSteps_TracksTheHeaderAndTheGeneratorAsInputs()
    {
        WriteHeader("HotspotSheet.h", "NF_CLASS()\nstruct HotspotSheet {};");

        var step = Synthesize()[0];

        step.Inputs.Should().Contain(i => i.EndsWith("HotspotSheet.h"));
        step.Inputs.Should().Contain(i => i.EndsWith("ReflectCodeGen.py"));
    }

    [Fact]
    public void CreateSteps_IgnoresACommentedOutAnnotation()
    {
        // The generator errors on a header it was handed and found nothing in, so a step declared
        // for a header whose only NF_CLASS is commented out would fail the build.
        WriteHeader("Commented.h", "// NF_CLASS()\n// struct WasHere {};\n");

        Synthesize().Should().BeEmpty();
    }

    [Fact]
    public void CreateSteps_FindsTheGeneratorAboveAPluginRootedProject()
    {
        // ResolveProjectRoot deliberately roots a single-plugin pilot at the plugin, so the generator is
        // above ProjectRoot, not under it.
        var pluginRoot = Path.Combine(_root, "Plugins", "ReflectPlugin");
        var pluginSource = Path.Combine(pluginRoot, "Source", "ReflectModule");
        Directory.CreateDirectory(pluginSource);
        File.WriteAllText(Path.Combine(pluginSource, "Sheet.h"), "NF_CLASS()\nstruct Sheet {};");

        var context = CreateContext(pluginRoot);
        var steps = new ReflectCodeGenSynthesizer().CreateSteps(
            new ReflectModule(context), context, new CompileEnvironment([], []));

        steps.Should().ContainSingle().Which.Command.Should()
            .Contain(Path.Combine(_root, "Tools", "ReflectCodeGen", "ReflectCodeGen.py"));
    }

    [Fact]
    public void CreateSteps_SaysSoWhenTheGeneratorIsMissing()
    {
        File.Delete(Path.Combine(_root, "Tools", "ReflectCodeGen", "ReflectCodeGen.py"));
        WriteHeader("HotspotSheet.h", "NF_CLASS()\nstruct HotspotSheet {};");

        var act = () => Synthesize();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ReflectCodeGen.py*");
    }

    [Fact]
    public void CreateSteps_IsDeterministicallyOrdered()
    {
        WriteHeader("Zebra.h", "NF_CLASS()\nstruct Zebra {};");
        WriteHeader("Alpha.h", "NF_CLASS()\nstruct Alpha {};");

        Synthesize().Select(s => s.Outputs[0]).Should()
            .ContainInOrder("Alpha.generated.cpp", "Zebra.generated.cpp");
    }

    [Fact]
    public void CreateSteps_QualifiesCollidingBasenamesWithTheirDirectory()
    {
        WriteHeader("Hotspot/Sheet.h", "NF_CLASS()\nstruct A {};");
        WriteHeader("Document/Sheet.h", "NF_CLASS()\nstruct B {};");

        Synthesize().Select(s => s.Outputs[0]).Should()
            .BeEquivalentTo(["Document_Sheet.generated.cpp", "Hotspot_Sheet.generated.cpp"]);
    }

    [Fact]
    public void CreateSteps_PassesTheCompileEnvironmentThroughAFlagsFile()
    {
        WriteHeader("HotspotSheet.h", "NF_CLASS()\nstruct HotspotSheet {};");
        var env = new CompileEnvironment(["C:/inc/one"], ["WITH_EXTRA=1"], ["C:/inc/external"]);

        var step = Synthesize(env)[0];

        var flagsFile = step.Command.Split("--flags-file")[1].Split('"')[1];
        File.ReadAllLines(flagsFile).Should()
            .BeEquivalentTo(["-IC:/inc/one", "-IC:/inc/external", "-DWITH_EXTRA=1"]);
    }

    [Fact]
    public void CreateSteps_IsEmptyWhenTheModuleOptsOut()
    {
        WriteHeader("HotspotSheet.h", "NF_CLASS()\nstruct HotspotSheet {};");

        var module = new ReflectModule(CreateContext()) { EnableReflectCodeGen = false };

        new ReflectCodeGenSynthesizer().CreateSteps(module, CreateContext(), new CompileEnvironment([], []))
            .Should().BeEmpty();
    }

    private IReadOnlyList<CodeGenStep> Synthesize(CompileEnvironment? env = null) =>
        new ReflectCodeGenSynthesizer().CreateSteps(
            new ReflectModule(CreateContext()), CreateContext(), env ?? new CompileEnvironment([], []));

    private BuildContext CreateContext(string? projectRoot = null) => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = projectRoot ?? _root,
        OutputDirectory = Path.Combine(_root, "bin"),
        IntermediateDirectory = Path.Combine(_root, "obj")
    };

    /// <summary>
    /// ModuleRules.Name derives from the class name, so this resolves its sources under
    /// Source/ReflectModule - the directory the fixture creates.
    /// </summary>
    private sealed class ReflectModule(BuildContext context) : ModuleRules(context);
}

