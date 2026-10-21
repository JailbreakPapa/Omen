// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Runtime.InteropServices;
using Omen.Core.Configuration;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Apple;

/// <summary>
/// Detects Xcode and the macOS SDK. Shares Xcode discovery with <see cref="AppleSDK"/>
/// (see <see cref="AppleXcodeLocator"/>) - same toolchain install, different platform SDK
/// and target triple (macosx vs iphoneos), and no simulator concept for Mac.
/// </summary>
public sealed class MacSDK : IPlatformSDK
{
    public TargetPlatform Platform => TargetPlatform.Mac;
    public string Name => "Xcode macOS SDK";

    // Building for macOS requires a macOS host with Xcode/Command Line Tools installed.
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public IReadOnlyList<TargetArchitecture> SupportedArchitectures =>
        [TargetArchitecture.ARM64, TargetArchitecture.X64]; // Apple Silicon and Intel, both native

    public SDKInfo? Detect()
    {
        if (!IsAvailable) return null;

        var xcodePath = AppleXcodeLocator.GetXcodePath();
        if (xcodePath == null) return null;

        var macSdk = AppleXcodeLocator.GetSdkPath(xcodePath, "MacOSX.platform");
        if (macSdk == null) return null;

        var version = AppleXcodeLocator.GetXcodeVersion();

        return new SDKInfo
        {
            Version = version ?? "unknown",
            InstallPath = xcodePath,
            AdditionalPaths = new Dictionary<string, string>
            {
                ["ToolchainPath"] = Path.Combine(xcodePath, "Toolchains", "XcodeDefault.xctoolchain", "usr", "bin"),
                ["DeviceSdkPath"] = macSdk
            }
        };
    }

    public IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo) =>
        new AppleToolchain(sdkInfo, architecture, TargetPlatform.Mac);
}
