// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

/// <summary>
/// Tests for BuildEnums.
/// </summary>
public class BuildEnumsTests
{
    [Theory]
    [InlineData(TargetPlatform.Windows)]
    [InlineData(TargetPlatform.Linux)]
    [InlineData(TargetPlatform.FreeBSD)]
    [InlineData(TargetPlatform.Android)]
    [InlineData(TargetPlatform.iOS)]
    public void TargetPlatform_HasExpectedValues(TargetPlatform platform)
    {
        // Assert
        Enum.IsDefined(typeof(TargetPlatform), platform).Should().BeTrue();
    }

    [Theory]
    [InlineData(TargetArchitecture.X64)]
    [InlineData(TargetArchitecture.X86)]
    [InlineData(TargetArchitecture.ARM64)]
    [InlineData(TargetArchitecture.ARMv7)]
    public void TargetArchitecture_HasExpectedValues(TargetArchitecture arch)
    {
        // Assert
        Enum.IsDefined(typeof(TargetArchitecture), arch).Should().BeTrue();
    }

    [Theory]
    [InlineData(BuildConfiguration.Debug)]
    [InlineData(BuildConfiguration.Development)]
    [InlineData(BuildConfiguration.Shipping)]
    public void BuildConfiguration_HasExpectedValues(BuildConfiguration config)
    {
        // Assert
        Enum.IsDefined(typeof(BuildConfiguration), config).Should().BeTrue();
    }

    [Theory]
    [InlineData(CppStandard.Cpp14)]
    [InlineData(CppStandard.Cpp17)]
    [InlineData(CppStandard.Cpp20)]
    [InlineData(CppStandard.Cpp23)]
    [InlineData(CppStandard.Latest)]
    public void CppStandard_HasExpectedValues(CppStandard std)
    {
        // Assert
        Enum.IsDefined(typeof(CppStandard), std).Should().BeTrue();
    }

    [Theory]
    [InlineData(ModuleLanguage.Cpp)]
    [InlineData(ModuleLanguage.CSharp)]
    public void ModuleLanguage_HasExpectedValues(ModuleLanguage lang)
    {
        // Assert
        Enum.IsDefined(typeof(ModuleLanguage), lang).Should().BeTrue();
    }

    [Theory]
    [InlineData(CSharpVersion.CSharp10)]
    [InlineData(CSharpVersion.CSharp11)]
    [InlineData(CSharpVersion.CSharp12)]
    [InlineData(CSharpVersion.CSharp13)]
    [InlineData(CSharpVersion.Latest)]
    public void CSharpVersion_HasExpectedValues(CSharpVersion version)
    {
        // Assert
        Enum.IsDefined(typeof(CSharpVersion), version).Should().BeTrue();
    }

    [Theory]
    [InlineData(DotNetFramework.Net60)]
    [InlineData(DotNetFramework.Net70)]
    [InlineData(DotNetFramework.Net80)]
    [InlineData(DotNetFramework.Net90)]
    [InlineData(DotNetFramework.NetStandard20)]
    [InlineData(DotNetFramework.NetStandard21)]
    public void DotNetFramework_HasExpectedValues(DotNetFramework framework)
    {
        // Assert
        Enum.IsDefined(typeof(DotNetFramework), framework).Should().BeTrue();
    }

    [Theory]
    [InlineData(QtVersion.None)]
    [InlineData(QtVersion.Qt5)]
    [InlineData(QtVersion.Qt6)]
    public void QtVersion_HasExpectedValues(QtVersion version)
    {
        // Assert
        Enum.IsDefined(typeof(QtVersion), version).Should().BeTrue();
    }

    [Theory]
    [InlineData(TargetType.Executable)]
    [InlineData(TargetType.SharedLibrary)]
    [InlineData(TargetType.StaticLibrary)]
    public void TargetType_HasExpectedValues(TargetType type)
    {
        // Assert
        Enum.IsDefined(typeof(TargetType), type).Should().BeTrue();
    }

    [Theory]
    [InlineData(ModuleType.Runtime)]
    [InlineData(ModuleType.Editor)]
    [InlineData(ModuleType.ThirdParty)]
    [InlineData(ModuleType.Plugin)]
    public void ModuleType_HasExpectedValues(ModuleType type)
    {
        // Assert
        Enum.IsDefined(typeof(ModuleType), type).Should().BeTrue();
    }

    [Theory]
    [InlineData(WarningLevel.Off)]
    [InlineData(WarningLevel.Level1)]
    [InlineData(WarningLevel.Level2)]
    [InlineData(WarningLevel.Level3)]
    [InlineData(WarningLevel.Level4)]
    [InlineData(WarningLevel.EnableAll)]
    public void WarningLevel_HasExpectedValues(WarningLevel level)
    {
        // Assert
        Enum.IsDefined(typeof(WarningLevel), level).Should().BeTrue();
    }

    [Fact]
    public void TargetPlatform_Count()
    {
        // Assert - verify we have the expected number of platforms
        var count = Enum.GetValues<TargetPlatform>().Length;
        count.Should().BeGreaterOrEqualTo(5);
    }

    [Fact]
    public void BuildConfiguration_Count()
    {
        // Assert - verify we have the expected number of configurations
        var count = Enum.GetValues<BuildConfiguration>().Length;
        count.Should().Be(4); // Debug, Development, Release, Shipping
    }
}
