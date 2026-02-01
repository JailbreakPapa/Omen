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
        
        var xcodePath = GetXcodePath();
        if (xcodePath == null) return null;
        
        var (deviceSdk, simulatorSdk) = GetSdkPaths(xcodePath);
        if (deviceSdk == null) return null;
        
        var version = GetXcodeVersion();
        
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
        return new AppleToolchain(sdkInfo, architecture, isSimulator);
    }
    
    private static string? GetXcodePath()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xcode-select",
                Arguments = "-p",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    // xcode-select returns /Applications/Xcode.app/Contents/Developer
                    return output;
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        
        // Fallback to common path
        var defaultPath = "/Applications/Xcode.app/Contents/Developer";
        if (Directory.Exists(defaultPath))
            return defaultPath;
        
        return null;
    }
    
    private static (string? Device, string? Simulator) GetSdkPaths(string xcodePath)
    {
        var platformsPath = Path.Combine(xcodePath, "Platforms");
        
        string? deviceSdk = null;
        string? simulatorSdk = null;
        
        // iOS device SDK
        var iphoneOsPath = Path.Combine(platformsPath, "iPhoneOS.platform", "Developer", "SDKs");
        if (Directory.Exists(iphoneOsPath))
        {
            var sdk = Directory.GetDirectories(iphoneOsPath)
                .Where(d => d.EndsWith(".sdk"))
                .OrderByDescending(d => d)
                .FirstOrDefault();
            deviceSdk = sdk;
        }
        
        // iOS simulator SDK
        var simPath = Path.Combine(platformsPath, "iPhoneSimulator.platform", "Developer", "SDKs");
        if (Directory.Exists(simPath))
        {
            var sdk = Directory.GetDirectories(simPath)
                .Where(d => d.EndsWith(".sdk"))
                .OrderByDescending(d => d)
                .FirstOrDefault();
            simulatorSdk = sdk;
        }
        
        return (deviceSdk, simulatorSdk);
    }
    
    private static string? GetXcodeVersion()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xcodebuild",
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                var output = process.StandardOutput.ReadLine();
                process.WaitForExit();
                
                if (output != null)
                {
                    // Parse "Xcode 15.0" -> "15.0"
                    var match = System.Text.RegularExpressions.Regex.Match(output, @"Xcode\s+(\d+\.\d+)");
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
        }
        catch { }
        
        return null;
    }
}
