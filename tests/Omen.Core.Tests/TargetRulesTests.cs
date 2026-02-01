// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

/// <summary>
/// Tests for TargetRules configuration.
/// </summary>
public class TargetRulesTests
{
    [Fact]
    public void DefaultValues_AreSetCorrectly()
    {
        // Arrange & Act
        var target = new TestTarget(CreateTestContext());

        // Assert
        target.Type.Should().Be(TargetType.Executable);
        target.UseUnityBuild.Should().BeTrue();
        target.UseAdaptiveUnityBuild.Should().BeTrue();
        target.UsePCHFiles.Should().BeTrue();
        target.UseIncrementalLinking.Should().BeTrue();
        target.DefaultCppStandard.Should().Be(CppStandard.Cpp20);
        target.DefaultWarningLevel.Should().Be(WarningLevel.Level4);
        target.EnableDistributedBuild.Should().BeTrue();
        target.GenerateDebugInfo.Should().BeTrue();
    }

    [Fact]
    public void Name_DerivedFromClassName_WithoutTargetSuffix()
    {
        // Arrange & Act
        var target = new TestTarget(CreateTestContext());

        // Assert - "TestTarget" -> "Test"
        target.Name.Should().Be("Test");
    }

    [Fact]
    public void SupportedPlatforms_HasDefaultPlatforms()
    {
        // Arrange & Act
        var target = new TestTarget(CreateTestContext());

        // Assert
        target.SupportedPlatforms.Should().Contain(TargetPlatform.Windows);
        target.SupportedPlatforms.Should().Contain(TargetPlatform.Linux);
        target.SupportedPlatforms.Should().Contain(TargetPlatform.Android);
        target.SupportedPlatforms.Should().Contain(TargetPlatform.iOS);
    }

    [Fact]
    public void GlobalDefinitions_StartsEmpty()
    {
        // Arrange & Act
        var target = new TestTarget(CreateTestContext());

        // Assert
        target.GlobalDefinitions.Should().BeEmpty();
    }

    [Fact]
    public void BuildSteps_StartEmpty()
    {
        // Arrange & Act
        var target = new TestTarget(CreateTestContext());

        // Assert
        target.PreBuildSteps.Should().BeEmpty();
        target.PostBuildSteps.Should().BeEmpty();
    }

    [Fact]
    public void ExtraModules_StartsEmpty()
    {
        // Arrange & Act
        var target = new TestTarget(CreateTestContext());

        // Assert
        target.ExtraModules.Should().BeEmpty();
    }

    [Fact]
    public void LaunchModuleName_DefaultsToNull()
    {
        // Arrange & Act
        var target = new TestTarget(CreateTestContext());

        // Assert
        target.LaunchModuleName.Should().BeNull();
    }

    [Fact]
    public void CanSetTargetType()
    {
        // Arrange
        var target = new TestTarget(CreateTestContext());

        // Act
        target.Type = TargetType.SharedLibrary;

        // Assert
        target.Type.Should().Be(TargetType.SharedLibrary);
    }

    [Fact]
    public void CanAddPreBuildStep()
    {
        // Arrange
        var target = new TestTarget(CreateTestContext());
        var step = new BuildStep
        {
            Description = "Generate code",
            Command = "codegen.exe"
        };

        // Act
        target.PreBuildSteps.Add(step);

        // Assert
        target.PreBuildSteps.Should().HaveCount(1);
        target.PreBuildSteps[0].Description.Should().Be("Generate code");
    }

    [Fact]
    public void CanAddPostBuildStep()
    {
        // Arrange
        var target = new TestTarget(CreateTestContext());
        var step = new BuildStep
        {
            Description = "Copy to output",
            Command = "copy /Y output\\*.dll deploy\\"
        };

        // Act
        target.PostBuildSteps.Add(step);

        // Assert
        target.PostBuildSteps.Should().HaveCount(1);
        target.PostBuildSteps[0].Command.Should().Contain("copy");
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

    private sealed class TestTarget : TargetRules
    {
        public TestTarget(BuildContext context) : base(context) { }
    }
}
