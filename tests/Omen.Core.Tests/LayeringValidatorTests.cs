// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

public class LayeringValidatorTests
{
    private sealed class RuntimeModule : ModuleRules
    {
        public RuntimeModule(BuildContext context) : base(context) { }
    }

    private sealed class EditorModule : ModuleRules
    {
        public EditorModule(BuildContext context) : base(context) { }
    }

    private sealed class IntermediateModule : ModuleRules
    {
        public IntermediateModule(BuildContext context) : base(context) { }
    }

    private sealed class VendoredModule : ModuleRules
    {
        public VendoredModule(BuildContext context) : base(context) { Type = ModuleType.ThirdParty; }
    }

    private static BuildContext CreateContext() => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = "/test",
        OutputDirectory = "/test/bin",
        IntermediateDirectory = "/test/obj"
    };

    [Fact]
    public void Validate_NoForbiddenDependencies_DoesNotThrow()
    {
        var runtime = new RuntimeModule(CreateContext());
        var act = () => LayeringValidator.Validate([runtime]);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_DirectForbiddenDependency_Throws()
    {
        var context = CreateContext();
        var editor = new EditorModule(context);
        editor.PublicDependencies.Add("EditorModule");
        editor.ForbiddenDependencies.Add(("EditorModule", "The runtime is what ships."));

        var act = () => LayeringValidator.Validate([editor]);

        act.Should().Throw<LayeringViolationException>()
            .WithMessage("*forbidden dependency 'EditorModule'*")
            .WithMessage("*The runtime is what ships.*");
    }

    [Fact]
    public void Validate_ForbiddenDependencyThroughIntermediateModule_Throws()
    {
        var context = CreateContext();
        var runtime = new RuntimeModule(context);
        runtime.PublicDependencies.Add("IntermediateModule");
        runtime.ForbiddenDependencies.Add(("EditorModule", "no editor in runtime"));

        var intermediate = new IntermediateModule(context);
        intermediate.PublicDependencies.Add("EditorModule");

        var act = () => LayeringValidator.Validate([runtime, intermediate]);

        act.Should().Throw<LayeringViolationException>()
            .WithMessage("*RuntimeModule -> IntermediateModule -> EditorModule*");
    }

    [Fact]
    public void Validate_ForbiddenDependencyWithNoReason_Throws()
    {
        var context = CreateContext();
        var runtime = new RuntimeModule(context);
        runtime.ForbiddenDependencies.Add(("EditorModule", ""));

        var act = () => LayeringValidator.Validate([runtime]);

        act.Should().Throw<LayeringViolationException>().WithMessage("*reason*");
    }

    [Fact]
    public void Validate_ThirdPartyDependsOnFirstParty_ThrowsWithNoDeclarationRequired()
    {
        var context = CreateContext();
        var thirdParty = new VendoredModule(context);
        thirdParty.PublicDependencies.Add("RuntimeModule");
        var runtime = new RuntimeModule(context);

        var act = () => LayeringValidator.Validate([thirdParty, runtime]);

        act.Should().Throw<LayeringViolationException>().WithMessage("*third-party*VendoredModule*RuntimeModule*");
    }
}
