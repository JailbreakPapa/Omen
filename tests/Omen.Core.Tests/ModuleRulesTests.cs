// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

/// <summary>
/// Tests for ModuleRules configuration.
/// </summary>
public class ModuleRulesTests
{
    [Fact]
    public void DefaultValues_AreSetCorrectly()
    {
        // Arrange & Act
        var module = new TestModule(CreateTestContext());

        // Assert
        module.Type.Should().Be(ModuleType.Runtime);
        module.Language.Should().Be(ModuleLanguage.Cpp);
        module.CppStandard.Should().Be(CppStandard.Cpp20);
        module.WarningLevel.Should().Be(WarningLevel.Level4);
        module.EnableRTTI.Should().BeTrue();
        module.EnableExceptions.Should().BeTrue();
        module.TreatWarningsAsErrors.Should().BeFalse();
    }

    [Fact]
    public void Name_DerivedFromClassName()
    {
        // Arrange & Act
        var module = new TestModule(CreateTestContext());

        // Assert
        module.Name.Should().Be("TestModule");
    }

    [Fact]
    public void IncludePaths_CombinesPublicAndPrivate()
    {
        // Arrange
        var module = new TestModule(CreateTestContext());
        module.PublicIncludePaths.Add("/public/include");
        module.PrivateIncludePaths.Add("/private/include");

        // Act
        var allPaths = module.IncludePaths;

        // Assert
        allPaths.Should().HaveCount(2);
        allPaths.Should().Contain("/public/include");
        allPaths.Should().Contain("/private/include");
    }

    [Fact]
    public void Dependencies_AreEmptyByDefault()
    {
        // Arrange & Act
        var module = new TestModule(CreateTestContext());

        // Assert
        module.PublicDependencies.Should().BeEmpty();
        module.PrivateDependencies.Should().BeEmpty();
    }

    [Fact]
    public void CSharpModule_HasCorrectDefaults()
    {
        // Arrange & Act
        var module = new TestCSharpModule(CreateTestContext());

        // Assert
        module.Language.Should().Be(ModuleLanguage.CSharp);
        module.IsCSharpProject.Should().BeTrue();
        module.CSharpVersion.Should().Be(CSharpVersion.CSharp12);
        module.TargetFramework.Should().Be(DotNetFramework.Net80);
        module.EnableNullable.Should().BeTrue();
        module.ImplicitUsings.Should().BeTrue();
    }

    [Fact]
    public void QtModule_HasCorrectDefaults()
    {
        // Arrange & Act
        var module = new TestQtModule(CreateTestContext());

        // Assert
        module.IsQtProject.Should().BeTrue();
        module.QtVersion.Should().Be(QtVersion.Qt6);
        module.QtModules.Should().Contain("Core");
        module.QtModules.Should().Contain("Widgets");
        module.EnableMoc.Should().BeTrue();
        module.EnableUic.Should().BeTrue();
        module.EnableRcc.Should().BeTrue();
    }

    [Fact]
    public void UseQt_SetsVersionAndModules()
    {
        // Arrange
        var module = new TestQtModuleWithHelper(CreateTestContext());

        // Assert
        module.QtVersion.Should().Be(QtVersion.Qt6);
        module.QtModules.Should().BeEquivalentTo(["Core", "Widgets", "Gui"]);
    }

    [Fact]
    public void PackageReferences_CanBeAdded()
    {
        // Arrange
        var module = new TestCSharpModule(CreateTestContext());

        // Assert
        module.PackageReferences.Should().HaveCount(2);
        module.PackageReferences.Should().Contain("Newtonsoft.Json/13.0.3");
        module.PackageReferences.Should().Contain("Serilog/3.1.1");
    }

    [Fact]
    public void Definitions_CanBeAddedToPublicAndPrivate()
    {
        // Arrange
        var module = new TestModule(CreateTestContext());
        module.PublicDefinitions.Add("PUBLIC_DEF=1");
        module.PrivateDefinitions.Add("PRIVATE_DEF=1");

        // Assert
        module.PublicDefinitions.Should().Contain("PUBLIC_DEF=1");
        module.PrivateDefinitions.Should().Contain("PRIVATE_DEF=1");
    }

    [Fact]
    public void Libraries_CanBeAddedToPublicAndPrivate()
    {
        // Arrange
        var module = new TestModule(CreateTestContext());
        module.PublicLibraries.Add("libpublic.lib");
        module.PrivateLibraries.Add("libprivate.lib");
        module.PublicSystemLibraries.Add("user32.lib");

        // Assert
        module.PublicLibraries.Should().Contain("libpublic.lib");
        module.PrivateLibraries.Should().Contain("libprivate.lib");
        module.PublicSystemLibraries.Should().Contain("user32.lib");
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

    // Test module classes
    private sealed class TestModule : ModuleRules
    {
        public TestModule(BuildContext context) : base(context) { }
    }

    private sealed class TestCSharpModule : ModuleRules
    {
        public TestCSharpModule(BuildContext context) : base(context)
        {
            Language = ModuleLanguage.CSharp;
            CSharpVersion = CSharpVersion.CSharp12;
            TargetFramework = DotNetFramework.Net80;
            PackageReferences.Add("Newtonsoft.Json/13.0.3");
            PackageReferences.Add("Serilog/3.1.1");
        }
    }

    private sealed class TestQtModule : ModuleRules
    {
        public TestQtModule(BuildContext context) : base(context)
        {
            QtVersion = QtVersion.Qt6;
            QtModules.AddRange(["Core", "Widgets"]);
        }
    }

    private sealed class TestQtModuleWithHelper : ModuleRules
    {
        public TestQtModuleWithHelper(BuildContext context) : base(context)
        {
            UseQt(QtVersion.Qt6, "Core", "Widgets", "Gui");
        }
    }
}
