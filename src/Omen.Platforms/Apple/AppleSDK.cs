// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Runtime.InteropServices;
using Omen.Core.Configuration;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Apple;

/// <summary>
/// Detects Xcode and iOS SDK installation.
/// </summary>
public sealed class AppleSDK : IPlatformSDK
{
    public TargetPlatform Platform => TargetPlatform.iOS;
    public string Name => "Xcode iOS SDK";
    
    // iOS development requires macOS with Xcode
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    
    public IReadOnlyList<TargetArchitecture> SupportedArchitectures =>
        [TargetArchitecture.ARM64, TargetArchitecture.X64]; // X64 for simulator
    
    public SDKInfo? Detect()
    {
        if (!IsAvailable) return null;

        var xcodePath = AppleXcodeLocator.GetXcodePath();
        if (xcodePath == null) return null;

        var deviceSdk = AppleXcodeLocator.GetSdkPath(xcodePath, "iPhoneOS.platform");
        if (deviceSdk == null) return null;

        var simulatorSdk = AppleXcodeLocator.GetSdkPath(xcodePath, "iPhoneSimulator.platform");
        var version = AppleXcodeLocator.GetXcodeVersion();

        return new SDKInfo
        {
            Version = version ?? "unknown",
            InstallPath = xcodePath,
            AdditionalPaths = new Dictionary<string, string>
            {
                ["ToolchainPath"] = Path.Combine(xcodePath, "Toolchains", "XcodeDefault.xctoolchain", "usr", "bin"),
                ["DeviceSdkPath"] = deviceSdk,
                ["SimulatorSdkPath"] = simulatorSdk ?? deviceSdk
            }
        };
    }

    public IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo)
    {
        // X64 is typically used for simulator
        var isSimulator = architecture == TargetArchitecture.X64;
        return new AppleToolchain(sdkInfo, architecture, TargetPlatform.iOS, isSimulator);
    }
}
