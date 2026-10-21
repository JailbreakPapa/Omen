// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Reflection;
using Omen.Core.Configuration;
using Omen.Core.Interfaces;
using Omen.Platforms;
using Omen.Platforms.Apple;

namespace Omen.Core.Tests;

/// <summary>
/// Tests for the Mac target platform: it must resolve to the Apple/Darwin toolchain family
/// (not fall through to Windows/MSVC conventions), produce Darwin-shaped output names, and
/// report not-detected when no Xcode SDK is present (as is always the case on this Windows
/// test host).
/// </summary>
public class MacPlatformTests
{
    private static SDKInfo FakeSdkInfo() => new()
    {
        Version = "15.0",
        InstallPath = "/Applications/Xcode.app/Contents/Developer",
        AdditionalPaths = new Dictionary<string, string>
        {
            ["ToolchainPath"] = "/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin",
            ["DeviceSdkPath"] = "/Applications/Xcode.app/Contents/Developer/Platforms/MacOSX.platform/Developer/SDKs/MacOSX15.0.sdk"
        }
    };

    [Fact]
    public void TargetPlatform_Mac_IsDefined()
    {
        Enum.IsDefined(typeof(TargetPlatform), TargetPlatform.Mac).Should().BeTrue();
    }

    [Fact]
    public void TargetPlatform_Mac_AppendedWithoutRenumberingExistingPlatforms()
    {
        // Ordinals are load-bearing elsewhere (e.g. persisted option caches) - Mac must be
        // appended after Xbox, never inserted earlier.
        ((int)TargetPlatform.Windows).Should().Be(1);
        ((int)TargetPlatform.Linux).Should().Be(2);
        ((int)TargetPlatform.FreeBSD).Should().Be(3);
        ((int)TargetPlatform.Android).Should().Be(4);
        ((int)TargetPlatform.iOS).Should().Be(5);
        ((int)TargetPlatform.Prospero).Should().Be(6);
        ((int)TargetPlatform.Xbox).Should().Be(7);
        ((int)TargetPlatform.Mac).Should().Be(8);
    }

    [Fact]
    public void MacSDK_Platform_IsMac()
    {
        new MacSDK().Platform.Should().Be(TargetPlatform.Mac);
    }

    [Fact]
    public void MacSDK_SupportedArchitectures_AreAppleSiliconAndIntel()
    {
        new MacSDK().SupportedArchitectures.Should().Contain([TargetArchitecture.ARM64, TargetArchitecture.X64]);
    }

    [Fact]
    public void MacSDK_OnHostWithoutXcode_ReportsNotDetected()
    {
        // This test host is Windows, so there is no macOS SDK - a null Detect() result (not a
        // throw, and not a false-positive "available") is the correct, honest behaviour.
        var sdk = new MacSDK();

        sdk.IsAvailable.Should().BeFalse();
        sdk.Detect().Should().BeNull();
    }

    [Fact]
    public void PlatformFactory_GetSDK_Mac_IsNotDetectedOnThisHost()
    {
        PlatformFactory.GetSDK(TargetPlatform.Mac).Should().BeNull();
    }

    [Fact]
    public void PlatformFactory_CreateToolchain_Mac_ReturnsNullWithoutSdk()
    {
        PlatformFactory.CreateToolchain(TargetPlatform.Mac, TargetArchitecture.ARM64).Should().BeNull();
    }

    [Fact]
    public void PlatformFactory_GetDefaultArchitecture_Mac_IsArm64()
    {
        PlatformFactory.GetDefaultArchitecture(TargetPlatform.Mac).Should().Be(TargetArchitecture.ARM64);
    }

    [Fact]
    public void PlatformFactory_AllSDKs_IncludesMacSDK()
    {
        PlatformFactory.AllSDKs.Should().Contain(sdk => sdk.Platform == TargetPlatform.Mac);
    }

    [Fact]
    public void MacSDK_CreateToolchain_ResolvesToAppleToolchainFamily()
    {
        var toolchain = new MacSDK().CreateToolchain(TargetArchitecture.ARM64, FakeSdkInfo());

        toolchain.Should().BeOfType<AppleToolchain>();
        toolchain.Platform.Should().Be(TargetPlatform.Mac);
    }

    [Theory]
    [InlineData(TargetArchitecture.ARM64)]
    [InlineData(TargetArchitecture.X64)]
    public void AppleToolchain_ForMac_ProducesDarwinShapedOutputNames_NotWindows(TargetArchitecture arch)
    {
        IToolchain toolchain = new AppleToolchain(FakeSdkInfo(), arch, TargetPlatform.Mac);

        toolchain.ObjectFileExtension.Should().Be(".o");
        toolchain.StaticLibraryExtension.Should().Be(".a");
        toolchain.SharedLibraryExtension.Should().Be(".dylib");
        toolchain.ExecutableExtension.Should().Be("");

        // The exact failure mode this must avoid: silently falling back to MSVC/Windows shapes.
        toolchain.StaticLibraryExtension.Should().NotBe(".lib");
        toolchain.SharedLibraryExtension.Should().NotBe(".dll");
        toolchain.ExecutableExtension.Should().NotBe(".exe");
    }

    [Fact]
    public void AppleToolchain_ForMac_UsesMacosxTargetTripleAndVersionFlag_NotIos()
    {
        var toolchain = new AppleToolchain(FakeSdkInfo(), TargetArchitecture.ARM64, TargetPlatform.Mac);

        var triple = InvokePrivate<string>(toolchain, "GetTargetTriple");
        var versionFlag = InvokePrivate<string>(toolchain, "GetVersionMinFlag");

        triple.Should().Contain("macosx");
        triple.Should().NotContain("ios");
        versionFlag.Should().StartWith("-mmacosx-version-min=");
    }

    [Fact]
    public void AppleToolchain_ForIOS_StillUsesIosTargetTripleAndVersionFlag()
    {
        // Regression guard: generalizing AppleToolchain to also serve Mac must not change
        // iOS's existing behaviour.
        var toolchain = new AppleToolchain(FakeSdkInfo(), TargetArchitecture.ARM64, TargetPlatform.iOS);

        var triple = InvokePrivate<string>(toolchain, "GetTargetTriple");
        var versionFlag = InvokePrivate<string>(toolchain, "GetVersionMinFlag");

        triple.Should().Contain("ios");
        versionFlag.Should().StartWith("-mios-version-min=");
    }

    [Fact]
    public void AppleToolchain_DefaultCompilerFlags_MacOmitsBitcode_IosIncludesIt()
    {
        var mac = new AppleToolchain(FakeSdkInfo(), TargetArchitecture.ARM64, TargetPlatform.Mac);
        var ios = new AppleToolchain(FakeSdkInfo(), TargetArchitecture.ARM64, TargetPlatform.iOS);

        mac.GetDefaultCompilerFlags(BuildConfiguration.Development).Should().NotContain("-fembed-bitcode");
        ios.GetDefaultCompilerFlags(BuildConfiguration.Development).Should().Contain("-fembed-bitcode");
    }

    [Fact]
    public void AppleToolchain_ForMac_NeverUsesSimulator()
    {
        // Mac has no simulator concept - even if isSimulator were mistakenly passed true,
        // Mac must not resolve to the (iOS-only) simulator SDK/sysroot.
        var toolchain = new AppleToolchain(FakeSdkInfo(), TargetArchitecture.X64, TargetPlatform.Mac, isSimulator: true);

        toolchain.Name.Should().Be("macOS");
        toolchain.SysrootPath.Should().Be(FakeSdkInfo().AdditionalPaths["DeviceSdkPath"]);
    }

    [Fact]
    public void TargetRules_DefaultSupportedPlatforms_IncludesMac()
    {
        var context = new BuildContext
        {
            Platform = TargetPlatform.Mac,
            Architecture = TargetArchitecture.ARM64,
            Configuration = BuildConfiguration.Development,
            ProjectRoot = Path.GetTempPath(),
            OutputDirectory = Path.GetTempPath(),
            IntermediateDirectory = Path.GetTempPath()
        };
        var target = new TestTargetRules(context);

        target.SupportedPlatforms.Should().Contain(TargetPlatform.Mac);
    }

    private sealed class TestTargetRules(BuildContext context) : Omen.Core.Rules.TargetRules(context);

    private static T InvokePrivate<T>(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().Name, methodName);
        return (T)method.Invoke(instance, null)!;
    }
}
