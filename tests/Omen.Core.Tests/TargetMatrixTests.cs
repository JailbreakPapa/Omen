// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

public class TargetMatrixTests
{
    [Fact]
    public void Resolve_UnconstrainedBuiltInDimension_DefaultsToEveryEnumValue()
    {
        var matrix = TargetMatrix.Empty.For(BuildConfiguration.Debug);
        // Platform and Architecture never constrained.

        var resolved = matrix.Resolve();

        resolved.Should().HaveCount(Enum.GetValues<TargetPlatform>().Length * Enum.GetValues<TargetArchitecture>().Length);
        resolved.Should().OnlyContain(r => r.Get<BuildConfiguration>() == BuildConfiguration.Debug);
    }

    [Fact]
    public void Resolve_ConstrainedDimensions_ProducesExactCartesianProduct()
    {
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows, TargetPlatform.Linux)
            .For(TargetArchitecture.X64)
            .For(BuildConfiguration.Debug, BuildConfiguration.Release);

        var resolved = matrix.Resolve();

        resolved.Should().HaveCount(4);
        resolved.Should().Contain(r => r.Get<TargetPlatform>() == TargetPlatform.Windows && r.Get<BuildConfiguration>() == BuildConfiguration.Debug);
        resolved.Should().Contain(r => r.Get<TargetPlatform>() == TargetPlatform.Linux && r.Get<BuildConfiguration>() == BuildConfiguration.Release);
        resolved.Should().OnlyContain(r => r.Get<TargetArchitecture>() == TargetArchitecture.X64);
    }

    [Fact]
    public void Resolve_CustomDimensionNeverConstrained_DoesNotAppearInResolvedTarget()
    {
        var matrix = TargetMatrix.Empty.For(TargetPlatform.Windows);

        var resolved = matrix.Resolve();

        resolved.Should().AllSatisfy(r => r.Values.ContainsKey(typeof(TestRenderApi)).Should().BeFalse());
    }

    [Fact]
    public void Resolve_CustomDimensionConstrained_AppearsInEveryResolvedTarget()
    {
        // All three built-ins pinned to a single value each, isolating "does a constrained
        // custom dimension appear correctly" from built-in auto-expansion math.
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows)
            .For(TargetArchitecture.X64)
            .For(BuildConfiguration.Debug)
            .For(TestRenderApi.DirectX12, TestRenderApi.Vulkan);

        var resolved = matrix.Resolve();

        resolved.Should().HaveCount(2);
        resolved.Should().Contain(r => r.Get<TestRenderApi>() == TestRenderApi.DirectX12);
        resolved.Should().Contain(r => r.Get<TestRenderApi>() == TestRenderApi.Vulkan);
    }

    [Fact]
    public void Exclude_DropsMatchingCombinationsOnly()
    {
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows, TargetPlatform.Linux)
            .For(TargetArchitecture.X64, TargetArchitecture.ARM64)
            .For(BuildConfiguration.Debug)
            .Exclude(t => t.Get<TargetPlatform>() == TargetPlatform.Windows && t.Get<TargetArchitecture>() == TargetArchitecture.ARM64);

        var resolved = matrix.Resolve();

        resolved.Should().HaveCount(3);
        resolved.Should().NotContain(r => r.Get<TargetPlatform>() == TargetPlatform.Windows && r.Get<TargetArchitecture>() == TargetArchitecture.ARM64);
    }

    [Fact]
    public void Exclude_MultiplePredicates_EachRemovesItsOwnMatches()
    {
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows, TargetPlatform.Linux)
            .For(BuildConfiguration.Debug, BuildConfiguration.Shipping)
            .Exclude(t => t.Get<TargetPlatform>() == TargetPlatform.Windows && t.Get<BuildConfiguration>() == BuildConfiguration.Shipping)
            .Exclude(t => t.Get<TargetPlatform>() == TargetPlatform.Linux && t.Get<BuildConfiguration>() == BuildConfiguration.Debug);

        var resolved = matrix.Resolve().Select(r => (r.Get<TargetPlatform>(), r.Get<TargetArchitecture>(), r.Get<BuildConfiguration>())).ToList();

        // 2 platforms x N architectures x 2 configs, minus the two excluded slices.
        var archCount = Enum.GetValues<TargetArchitecture>().Length;
        resolved.Should().HaveCount(2 * archCount);
        resolved.Where(r => r.Item1 == TargetPlatform.Windows).Should().OnlyContain(r => r.Item3 == BuildConfiguration.Debug);
        resolved.Where(r => r.Item1 == TargetPlatform.Linux).Should().OnlyContain(r => r.Item3 == BuildConfiguration.Shipping);
    }

    [Fact]
    public void Exclude_RemovesEveryCombination_ResolveReturnsEmpty()
    {
        var matrix = TargetMatrix.Empty
            .For(TargetPlatform.Windows)
            .For(BuildConfiguration.Debug)
            .Exclude(_ => true);

        matrix.Resolve().Should().BeEmpty();
    }

    [Fact]
    public void For_ValueOfUnTaggedEnum_Throws()
    {
        var act = () => TargetMatrix.Empty.For(UntaggedEnum.A);

        act.Should().Throw<InvalidOperationException>().WithMessage("*BuildDimension*");
    }

    [Fact]
    public void For_EmptyValueArray_Throws()
    {
        var act = () => TargetMatrix.Empty.For(Array.Empty<TargetPlatform>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResolvedTarget_TryGet_UnresolvedDimension_ReturnsFalse()
    {
        // All three built-ins pinned - .Single() is only valid once nothing auto-expands.
        var resolved = TargetMatrix.Empty
            .For(TargetPlatform.Windows)
            .For(TargetArchitecture.X64)
            .For(BuildConfiguration.Debug)
            .Resolve().Single();

        resolved.TryGet<TestRenderApi>(out var value).Should().BeFalse();
        value.Should().Be(default(TestRenderApi));
    }

    [Fact]
    public void ResolvedTarget_Get_UnresolvedDimension_Throws()
    {
        var resolved = TargetMatrix.Empty
            .For(TargetPlatform.Windows)
            .For(TargetArchitecture.X64)
            .For(BuildConfiguration.Debug)
            .Resolve().Single();

        var act = () => resolved.Get<TestRenderApi>();

        act.Should().Throw<InvalidOperationException>();
    }

    [BuildDimension]
    public enum TestRenderApi { DirectX12, Vulkan }

    public enum UntaggedEnum { A, B }
}
