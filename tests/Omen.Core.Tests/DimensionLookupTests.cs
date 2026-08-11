// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Reflection;

namespace Omen.Core.Tests;

public class DimensionLookupTests
{
    [Theory]
    [InlineData("TargetPlatform")]
    [InlineData("targetplatform")]
    [InlineData("TARGETPLATFORM")]
    public void FindDimensionType_BuiltInDimension_IsCaseInsensitive(string name)
    {
        var result = DimensionLookup.FindDimensionType(typeof(DimensionLookupTests).Assembly, name);

        result.Should().Be(typeof(TargetPlatform));
    }

    [Fact]
    public void FindDimensionType_CustomDimensionInGivenAssembly_IsFound()
    {
        var result = DimensionLookup.FindDimensionType(typeof(DimensionLookupTests).Assembly, nameof(LookupOnlyDimension));

        result.Should().Be(typeof(LookupOnlyDimension));
    }

    [Fact]
    public void FindDimensionType_UnknownName_ReturnsNull()
    {
        DimensionLookup.FindDimensionType(typeof(DimensionLookupTests).Assembly, "NoSuchDimension").Should().BeNull();
    }

    [Fact]
    public void FindDimensionType_UnTaggedEnum_IsNotFound()
    {
        DimensionLookup.FindDimensionType(typeof(DimensionLookupTests).Assembly, nameof(UntaggedEnum)).Should().BeNull();
    }

    [Fact]
    public void TryParseValue_ValidValue_ReturnsTrue()
    {
        var success = DimensionLookup.TryParseValue(typeof(TargetPlatform), "Windows", out var value);

        success.Should().BeTrue();
        value.Should().Be(TargetPlatform.Windows);
    }

    [Fact]
    public void TryParseValue_ValidValue_IsCaseInsensitive()
    {
        var success = DimensionLookup.TryParseValue(typeof(TargetPlatform), "windows", out var value);

        success.Should().BeTrue();
        value.Should().Be(TargetPlatform.Windows);
    }

    [Fact]
    public void TryParseValue_InvalidValue_ReturnsFalse()
    {
        var success = DimensionLookup.TryParseValue(typeof(TargetPlatform), "NotAPlatform", out var value);

        success.Should().BeFalse();
        value.Should().BeNull();
    }

    [BuildDimension]
    public enum LookupOnlyDimension { DirectX12, Vulkan }

    public enum UntaggedEnum { A, B }
}
