// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Reflection;
using Omen.Core.Configuration;
using Omen.Core.Interfaces;
using Omen.Platforms.Android;
using Omen.Platforms.Apple;
using Omen.Platforms.Console;
using Omen.Platforms.Unix;
using Omen.Platforms.Windows;

namespace Omen.Platforms;

/// <summary>
/// Factory for creating platform SDKs and toolchains.
/// </summary>
public static class PlatformFactory
{
    private static readonly Lazy<IReadOnlyList<IPlatformSDK>> _allSdks = new(DiscoverAllSdks);
    
    /// <summary>
    /// Gets all available platform SDKs.
    /// </summary>
    public static IReadOnlyList<IPlatformSDK> AllSDKs => _allSdks.Value;
    
    /// <summary>
    /// Gets the SDK for a specific platform.
    /// </summary>
    public static IPlatformSDK? GetSDK(TargetPlatform platform)
    {
        return AllSDKs.FirstOrDefault(s => s.Platform == platform && s.IsAvailable);
    }
    
    /// <summary>
    /// Creates a toolchain for the specified platform and architecture.
    /// </summary>
    public static IToolchain? CreateToolchain(TargetPlatform platform, TargetArchitecture architecture)
    {
        var sdk = GetSDK(platform);
        if (sdk == null) return null;
        
        var sdkInfo = sdk.Detect();
        if (sdkInfo == null) return null;
        
        return sdk.CreateToolchain(architecture, sdkInfo);
    }
    
    /// <summary>
    /// Gets the default architecture for a platform.
    /// </summary>
    public static TargetArchitecture GetDefaultArchitecture(TargetPlatform platform)
    {
        return platform switch
        {
            TargetPlatform.Windows => TargetArchitecture.X64,
            TargetPlatform.Linux => TargetArchitecture.X64,
            TargetPlatform.FreeBSD => TargetArchitecture.X64,
            TargetPlatform.Android => TargetArchitecture.ARM64,
            TargetPlatform.iOS => TargetArchitecture.ARM64,
            TargetPlatform.Prospero => TargetArchitecture.X64,
            TargetPlatform.Xbox => TargetArchitecture.X64,
            _ => TargetArchitecture.X64
        };
    }
    
    /// <summary>
    /// Gets the current host platform.
    /// </summary>
    public static TargetPlatform GetHostPlatform()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return TargetPlatform.Windows;
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            return TargetPlatform.Linux;
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.FreeBSD))
            return TargetPlatform.FreeBSD;
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            return TargetPlatform.iOS; // macOS can build for iOS
        
        return TargetPlatform.Unknown;
    }
    
    /// <summary>
    /// Gets the current host architecture.
    /// </summary>
    public static TargetArchitecture GetHostArchitecture()
    {
        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
            System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
            System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.ARM64,
            System.Runtime.InteropServices.Architecture.Arm => TargetArchitecture.ARMv7,
            _ => TargetArchitecture.Unknown
        };
    }
    
    /// <summary>
    /// Lists all available platforms with their SDKs.
    /// </summary>
    public static IEnumerable<(TargetPlatform Platform, IPlatformSDK Sdk, SDKInfo? Info)> GetAvailablePlatforms()
    {
        foreach (var sdk in AllSDKs)
        {
            if (sdk.IsAvailable)
            {
                var info = sdk.Detect();
                yield return (sdk.Platform, sdk, info);
            }
        }
    }
    
    private static IReadOnlyList<IPlatformSDK> DiscoverAllSdks()
    {
        var sdks = new List<IPlatformSDK>
        {
            new WindowsSDK(),
            new LinuxSDK(),
            new FreeBsdSDK(),
            new AndroidNdkSDK(),
            new AppleSDK(),
            new ProsperoSDK(),
            new XboxSDK()
        };

        sdks.AddRange(DiscoverExternalSdks(Environment.GetEnvironmentVariable("OMEN_EXTRA_PLATFORMS_DIR")));
        return sdks;
    }

    /// <summary>
    /// Loads additional IPlatformSDK implementations from assemblies in a directory, so a
    /// new platform can be added without editing this factory. Isolated as its own method
    /// (not folded into DiscoverAllSdks) so it's testable without the surrounding Lazy&lt;&gt;
    /// cache.
    /// </summary>
    internal static IReadOnlyList<IPlatformSDK> DiscoverExternalSdks(string? extraPlatformsDirectory)
    {
        if (string.IsNullOrEmpty(extraPlatformsDirectory) || !Directory.Exists(extraPlatformsDirectory))
            return [];

        var discovered = new List<IPlatformSDK>();
        foreach (var dll in Directory.GetFiles(extraPlatformsDirectory, "*.dll"))
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(dll);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
            {
                continue;
            }

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IPlatformSDK).IsAssignableFrom(type))
                    continue;
                if (Activator.CreateInstance(type) is IPlatformSDK sdk)
                    discovered.Add(sdk);
            }
        }

        return discovered;
    }
}
