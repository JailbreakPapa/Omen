// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

/// <summary>
/// Tests for BuildContext configuration.
/// </summary>
public class BuildContextTests
{
    [Fact]
    public void GetContextId_ReturnsCorrectFormat()
    {
        // Arrange
        var context = CreateTestContext();

        // Act
        var id = context.GetContextId();

        // Assert
        id.Should().Be("Windows-X64-Debug");
    }

    [Theory]
    [InlineData(TargetPlatform.Windows, TargetArchitecture.X64, BuildConfiguration.Debug, "Windows-X64-Debug")]
    [InlineData(TargetPlatform.Linux, TargetArchitecture.ARM64, BuildConfiguration.Shipping, "Linux-ARM64-Shipping")]
    [InlineData(TargetPlatform.Android, TargetArchitecture.ARM64, BuildConfiguration.Development, "Android-ARM64-Development")]
    public void GetContextId_ReturnsCorrectFormat_ForVariousPlatforms(
        TargetPlatform platform, 
        TargetArchitecture arch, 
        BuildConfiguration config, 
        string expected)
    {
        // Arrange
        var context = new BuildContext
        {
            Platform = platform,
            Architecture = arch,
            Configuration = config,
            ProjectRoot = "/test",
            OutputDirectory = "/test/bin",
            IntermediateDirectory = "/test/obj"
        };

        // Act
        var id = context.GetContextId();

        // Assert
        id.Should().Be(expected);
    }

    [Fact]
    public void ParallelJobs_DefaultsToProcessorCount()
    {
        // Arrange
        var context = CreateTestContext();

        // Assert
        context.ParallelJobs.Should().Be(Environment.ProcessorCount);
    }

    [Fact]
    public void GlobalDefinitions_InitializesAsEmptyList()
    {
        // Arrange
        var context = CreateTestContext();

        // Assert
        context.GlobalDefinitions.Should().NotBeNull();
        context.GlobalDefinitions.Should().BeEmpty();
    }

    [Fact]
    public void UseUnityBuild_DefaultsToTrue()
    {
        // Arrange
        var context = CreateTestContext();

        // Assert
        context.UseUnityBuild.Should().BeTrue();
    }

    [Fact]
    public void UseDistributedBuild_DefaultsToFalse()
    {
        // Arrange
        var context = CreateTestContext();

        // Assert
        context.UseDistributedBuild.Should().BeFalse();
    }

    private static BuildContext CreateTestContext() => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = "/test/project",
        OutputDirectory = "/test/project/bin",
        IntermediateDirectory = "/test/project/obj"
    };

    [Fact]
    public void GetDimension_BuiltInPlatform_ReturnsPlatformProperty()
    {
        var context = CreateTestContext();

        context.GetDimension<TargetPlatform>().Should().Be(context.Platform);
    }

    [Fact]
    public void GetDimension_BuiltInArchitecture_ReturnsArchitectureProperty()
    {
        var context = CreateTestContext();

        context.GetDimension<TargetArchitecture>().Should().Be(context.Architecture);
    }

    [Fact]
    public void GetDimension_BuiltInConfiguration_ReturnsConfigurationProperty()
    {
        var context = CreateTestContext();

        context.GetDimension<BuildConfiguration>().Should().Be(context.Configuration);
    }

    [Fact]
    public void GetDimension_CustomDimensionPresent_ReturnsItFromAdditionalDimensions()
    {
        var context = new BuildContext
        {
            Platform = TargetPlatform.Windows,
            Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Debug,
            ProjectRoot = "/test",
            OutputDirectory = "/test/bin",
            IntermediateDirectory = "/test/obj",
            AdditionalDimensions = new Dictionary<Type, Enum> { [typeof(TestRenderApi)] = TestRenderApi.Vulkan }
        };

        context.GetDimension<TestRenderApi>().Should().Be(TestRenderApi.Vulkan);
    }

    [Fact]
    public void GetDimension_DimensionNotPresent_Throws()
    {
        var context = CreateTestContext();

        var act = () => context.GetDimension<TestRenderApi>();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AdditionalDimensions_DefaultsToEmpty()
    {
        var context = CreateTestContext();

        context.AdditionalDimensions.Should().NotBeNull();
        context.AdditionalDimensions.Should().BeEmpty();
    }

    [BuildDimension]
    public enum TestRenderApi { DirectX12, Vulkan }
}
