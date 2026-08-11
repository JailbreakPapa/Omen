// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

public class CompiledRulesSupportedTargetsTests : IDisposable
{
    private readonly string _projectRoot;

    public CompiledRulesSupportedTargetsTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(CompiledRulesSupportedTargetsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }

    [Fact]
    public async Task GetSupportedTargets_TargetDeclaresIt_ReturnsTheMatrix()
    {
        var targetFile = Path.Combine(_projectRoot, "WithMatrix.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class WithMatrixTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows, TargetPlatform.Linux)
                    .For(TargetArchitecture.X64)
                    .For(BuildConfiguration.Debug, BuildConfiguration.Release);

                public WithMatrixTarget(BuildContext context) : base(context) { }
            }
            """);
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "Intermediate", "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var targetType = compiledRules.FindTargetType();
        targetType.Should().NotBeNull();
        var matrix = compiledRules.GetSupportedTargets(targetType!);

        matrix.Should().NotBeNull();
        matrix!.Resolve().Should().HaveCount(4);
    }

    [Fact]
    public async Task GetSupportedTargets_TargetDoesNotDeclareIt_ReturnsNull()
    {
        var targetFile = Path.Combine(_projectRoot, "NoMatrix.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class NoMatrixTarget : TargetRules
            {
                public NoMatrixTarget(BuildContext context) : base(context) { }
            }
            """);
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "Intermediate", "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var targetType = compiledRules.FindTargetType();
        targetType.Should().NotBeNull();

        compiledRules.GetSupportedTargets(targetType!).Should().BeNull();
    }

    [Fact]
    public async Task FindTargetType_DoesNotRequireAnyBuildContext()
    {
        // Regression guard: FindTargetType must work purely via reflection over the
        // compiled assembly's types - no BuildContext, no instance construction. A target
        // whose constructor would throw for an unconfigured context proves this.
        var targetFile = Path.Combine(_projectRoot, "Throwing.target.cs");
        File.WriteAllText(targetFile, """
            using System;
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class ThrowingTarget : TargetRules
            {
                public ThrowingTarget(BuildContext context) : base(context)
                {
                    throw new InvalidOperationException("must never run during discovery");
                }
            }
            """);
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "Intermediate", "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var targetType = compiledRules.FindTargetType();

        targetType.Should().NotBeNull();
        targetType!.Name.Should().Be("ThrowingTarget");
    }

    [Fact]
    public async Task GetSupportedTargets_ModuleDeclaresIt_ReturnsTheMatrix()
    {
        var moduleFile = Path.Combine(_projectRoot, "WithMatrix.module.cs");
        File.WriteAllText(moduleFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class WithMatrixModule : ModuleRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(TargetPlatform.Windows)
                    .For(TargetArchitecture.X64)
                    .For(BuildConfiguration.Debug);

                public WithMatrixModule(BuildContext context) : base(context) { }
            }
            """);
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "Intermediate", "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var moduleType = compiledRules.CreateModuleRules(CreateContext()).Single(m => m.Name == "WithMatrixModule").GetType();
        var matrix = compiledRules.GetSupportedTargets(moduleType);

        matrix.Should().NotBeNull();
        // All three built-ins pinned, so Resolve() yields exactly one combination -
        // isolates "does a module-level matrix resolve" from built-in auto-expansion math.
        var resolved = matrix!.Resolve().Should().ContainSingle().Subject;
        resolved.Get<TargetPlatform>().Should().Be(TargetPlatform.Windows);
    }

    [Fact]
    public async Task GetSupportedTargets_GetterThrows_PropagatesOriginalException()
    {
        // Reflection's PropertyInfo.GetValue wraps getter exceptions in a
        // TargetInvocationException whose own .Message is a useless generic string. Callers
        // (and ultimately the CLI's top-level error handler) need the real exception - e.g.
        // TargetMatrix.For<T>()'s specific "not a [BuildDimension] enum" message - not the
        // wrapper's.
        var targetFile = Path.Combine(_projectRoot, "Throws.target.cs");
        File.WriteAllText(targetFile, """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public enum UntaggedEnum { A }

            public class ThrowsTarget : TargetRules
            {
                public static TargetMatrix SupportedTargets => TargetMatrix.Empty
                    .For(UntaggedEnum.A);

                public ThrowsTarget(BuildContext context) : base(context) { }
            }
            """);
        var compiler = new RuleCompiler(Path.Combine(_projectRoot, "Intermediate", "RuleCache"));
        var compiledRules = await compiler.CompileRulesAsync(_projectRoot);

        var targetType = compiledRules.FindTargetType();
        targetType.Should().NotBeNull();

        var act = () => compiledRules.GetSupportedTargets(targetType!);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a [BuildDimension] enum*");
    }

    private BuildContext CreateContext() => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = _projectRoot,
        OutputDirectory = Path.Combine(_projectRoot, "Binaries"),
        IntermediateDirectory = Path.Combine(_projectRoot, "Intermediate")
    };
}
