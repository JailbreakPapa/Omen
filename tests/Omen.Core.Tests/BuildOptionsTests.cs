// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Options;

namespace Omen.Core.Tests;

public class BuildOptionsTests
{
    private static BuildContext CreateContext(IReadOnlyDictionary<string, string>? cached = null) => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Debug,
        ProjectRoot = "/test/project",
        OutputDirectory = "/test/project/bin",
        IntermediateDirectory = "/test/project/obj",
        CachedOptionValues = cached ?? new Dictionary<string, string>()
    };

    [Fact]
    public void Declare_Bool_NoCachedValue_ReturnsDefaultAndRecordsDeclaration()
    {
        var context = CreateContext();

        var result = BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable feature X", false);

        result.Should().BeFalse();
        context.DeclaredOptions.Should().ContainSingle(o =>
            o.Name == "ENABLE_FEATURE_X" &&
            o.Description == "Enable feature X" &&
            o.Type == BuildOptionType.Bool &&
            o.DefaultValue == "false" &&
            o.EffectiveValue == "false");
    }

    [Fact]
    public void Declare_Bool_WithCachedValue_ReturnsCachedOverride()
    {
        var context = CreateContext(new Dictionary<string, string> { ["ENABLE_FEATURE_X"] = "true" });

        var result = BuildOptions.Declare(context, "ENABLE_FEATURE_X", "Enable feature X", false);

        result.Should().BeTrue();
        context.DeclaredOptions.Single().EffectiveValue.Should().Be("true");
    }

    [Fact]
    public void Declare_String_WithCachedValue_ReturnsCachedOverride()
    {
        var context = CreateContext(new Dictionary<string, string> { ["INSTALL_PREFIX"] = "/opt/custom" });

        var result = BuildOptions.Declare(context, "INSTALL_PREFIX", "Install prefix", "/usr/local");

        result.Should().Be("/opt/custom");
    }

    [Fact]
    public void Declare_Int_WithCachedValue_ParsesAndReturnsCachedOverride()
    {
        var context = CreateContext(new Dictionary<string, string> { ["MAX_WORKERS"] = "16" });

        var result = BuildOptions.Declare(context, "MAX_WORKERS", "Max worker threads", 4);

        result.Should().Be(16);
    }

    [Fact]
    public void Declare_Int_WithUnparsableCachedValue_FallsBackToDefault()
    {
        var context = CreateContext(new Dictionary<string, string> { ["MAX_WORKERS"] = "not-a-number" });

        var result = BuildOptions.Declare(context, "MAX_WORKERS", "Max worker threads", 4);

        result.Should().Be(4);
    }

    [Fact]
    public void DeclarePath_RecordsPathType()
    {
        var context = CreateContext();

        var result = BuildOptions.DeclarePath(context, "SDK_ROOT", "Path to the SDK", "/default/sdk");

        result.Should().Be("/default/sdk");
        context.DeclaredOptions.Single().Type.Should().Be(BuildOptionType.Path);
    }

    [Fact]
    public void Declare_SameNameTwice_FirstDeclarationWins()
    {
        var context = CreateContext();

        var first = BuildOptions.Declare(context, "SHARED_OPTION", "First description", true);
        var second = BuildOptions.Declare(context, "SHARED_OPTION", "Second description", false);

        first.Should().BeTrue();
        second.Should().BeTrue();
        context.DeclaredOptions.Should().ContainSingle();
        context.DeclaredOptions.Single().Description.Should().Be("First description");
    }
}
