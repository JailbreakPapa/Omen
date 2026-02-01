// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Runtime.InteropServices;
using Omen.Core.Configuration;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Android;

/// <summary>
/// Detects Android NDK installation.
/// </summary>
public sealed class AndroidNdkSDK : IPlatformSDK
{
    public TargetPlatform Platform => TargetPlatform.Android;
    public string Name => "Android NDK";
    
    // Android development is supported from Windows, Linux, and macOS
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                               RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                               RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    
    public IReadOnlyList<TargetArchitecture> SupportedArchitectures =>
        [TargetArchitecture.ARM64, TargetArchitecture.ARMv7, TargetArchitecture.X64, TargetArchitecture.X86];
    
    public SDKInfo? Detect()
    {
        if (!IsAvailable) return null;
        
        var ndkPath = FindNdkPath();
        if (ndkPath == null) return null;
        
        var version = GetNdkVersion(ndkPath);
        if (version == null) return null;
        
        return new SDKInfo
        {
            Version = version,
            InstallPath = ndkPath,
            Properties = new Dictionary<string, string>
            {
                ["NdkPath"] = ndkPath
            }
        };
    }
    
    public IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo)
    {
        return new AndroidNdkToolchain(sdkInfo, architecture);
    }
    
    private static string? FindNdkPath()
    {
        // Check environment variables
        var ndkHome = Environment.GetEnvironmentVariable("ANDROID_NDK_HOME");
        if (!string.IsNullOrEmpty(ndkHome) && Directory.Exists(ndkHome))
            return ndkHome;
        
        var ndkRoot = Environment.GetEnvironmentVariable("ANDROID_NDK_ROOT");
        if (!string.IsNullOrEmpty(ndkRoot) && Directory.Exists(ndkRoot))
            return ndkRoot;
        
        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME") ??
                          Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        
        if (!string.IsNullOrEmpty(androidHome))
        {
            // Check for NDK inside Android SDK
            var ndkBundle = Path.Combine(androidHome, "ndk-bundle");
            if (Directory.Exists(ndkBundle))
                return ndkBundle;
            
            // Check for versioned NDK directories
            var ndkDir = Path.Combine(androidHome, "ndk");
            if (Directory.Exists(ndkDir))
            {
                var versions = Directory.GetDirectories(ndkDir)
                    .OrderByDescending(d => d)
                    .FirstOrDefault();
                if (versions != null)
                    return versions;
            }
        }
        
        // Check common installation paths
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var sdkPath = Path.Combine(localAppData, "Android", "Sdk");
            if (Directory.Exists(sdkPath))
            {
                var ndkDir = Path.Combine(sdkPath, "ndk");
                if (Directory.Exists(ndkDir))
                {
                    var version = Directory.GetDirectories(ndkDir).OrderByDescending(d => d).FirstOrDefault();
                    if (version != null) return version;
                }
            }
        }
        else
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "";
            var paths = new[]
            {
                Path.Combine(home, "Android", "Sdk", "ndk"),
                Path.Combine(home, "Library", "Android", "sdk", "ndk"), // macOS
                "/opt/android-ndk"
            };
            
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    var version = Directory.GetDirectories(path).OrderByDescending(d => d).FirstOrDefault();
                    if (version != null) return version;
                }
            }
        }
        
        return null;
    }
    
    private static string? GetNdkVersion(string ndkPath)
    {
        var sourcePropertiesPath = Path.Combine(ndkPath, "source.properties");
        if (File.Exists(sourcePropertiesPath))
        {
            var lines = File.ReadAllLines(sourcePropertiesPath);
            foreach (var line in lines)
            {
                if (line.StartsWith("Pkg.Revision"))
                {
                    var parts = line.Split('=');
                    if (parts.Length == 2)
                        return parts[1].Trim();
                }
            }
        }
        
        // Fallback: use directory name
        return Path.GetFileName(ndkPath);
    }
}
