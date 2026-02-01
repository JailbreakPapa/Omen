// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Runtime.InteropServices;
using Microsoft.Win32;
using Omen.Core.Configuration;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Windows;

/// <summary>
/// Detects Visual Studio and Windows SDK installations.
/// </summary>
public sealed class WindowsSDK : IPlatformSDK
{
    public TargetPlatform Platform => TargetPlatform.Windows;
    public string Name => "Windows SDK + Visual Studio";
    
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    
    public IReadOnlyList<TargetArchitecture> SupportedArchitectures =>
        [TargetArchitecture.X64, TargetArchitecture.X86, TargetArchitecture.ARM64];
    
    public SDKInfo? Detect()
    {
        if (!IsAvailable) return null;
        
        // Try to find Visual Studio installation
        var vsInstallPath = FindVisualStudioInstallation();
        if (vsInstallPath == null) return null;
        
        // Find VC tools version
        var vcToolsVersionPath = Path.Combine(vsInstallPath, "VC", "Auxiliary", "Build", "Microsoft.VCToolsVersion.default.txt");
        if (!File.Exists(vcToolsVersionPath)) return null;
        
        var vcToolsVersion = File.ReadAllText(vcToolsVersionPath).Trim();
        var vcToolsPath = Path.Combine(vsInstallPath, "VC", "Tools", "MSVC", vcToolsVersion);
        
        if (!Directory.Exists(vcToolsPath)) return null;
        
        // Find Windows SDK
        var (windowsSdkDir, windowsSdkVersion) = FindWindowsSDK();
        if (windowsSdkDir == null) return null;
        
        return new SDKInfo
        {
            Version = vcToolsVersion,
            InstallPath = vsInstallPath,
            AdditionalPaths = new Dictionary<string, string>
            {
                ["VCToolsInstallDir"] = vcToolsPath,
                ["VCToolsVersion"] = vcToolsVersion,
                ["WindowsSdkDir"] = windowsSdkDir,
                ["WindowsSdkVersion"] = windowsSdkVersion ?? ""
            },
            Properties = new Dictionary<string, string>
            {
                ["VisualStudioVersion"] = GetVisualStudioVersion(vsInstallPath)
            }
        };
    }
    
    public IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo)
    {
        return new MsvcToolchain(sdkInfo, architecture);
    }
    
    private static string? FindVisualStudioInstallation()
    {
        // Common installation paths
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        
        var vsVersions = new[] { "2022", "2019" };
        var vsEditions = new[] { "Enterprise", "Professional", "Community", "BuildTools" };
        
        foreach (var version in vsVersions)
        {
            foreach (var edition in vsEditions)
            {
                var path = Path.Combine(programFiles, "Microsoft Visual Studio", version, edition);
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "VC", "Auxiliary", "Build", "vcvarsall.bat")))
                    return path;
                
                // Also check x86 program files
                path = Path.Combine(programFilesX86, "Microsoft Visual Studio", version, edition);
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "VC", "Auxiliary", "Build", "vcvarsall.bat")))
                    return path;
            }
        }
        
        // Try vswhere
        var vswherePath = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswherePath))
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vswherePath,
                    Arguments = "-latest -property installationPath",
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
                        return output;
                }
            }
            catch
            {
                // Ignore vswhere errors
            }
        }
        
        return null;
    }
    
    private static (string? Path, string? Version) FindWindowsSDK()
    {
        try
        {
            // Try registry
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
            if (key != null)
            {
                var kitsRoot = key.GetValue("KitsRoot10") as string;
                if (kitsRoot != null)
                {
                    // Find the latest SDK version
                    var includeDir = Path.Combine(kitsRoot, "Include");
                    if (Directory.Exists(includeDir))
                    {
                        var versions = Directory.GetDirectories(includeDir)
                            .Select(Path.GetFileName)
                            .Where(v => v != null && v.StartsWith("10."))
                            .OrderByDescending(v => v)
                            .FirstOrDefault();
                        
                        if (versions != null)
                            return (kitsRoot, versions);
                    }
                }
            }
        }
        catch
        {
            // Registry access failed
        }
        
        // Fallback: check common paths
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var sdkPath = Path.Combine(programFiles, "Windows Kits", "10");
        
        if (Directory.Exists(sdkPath))
        {
            var includeDir = Path.Combine(sdkPath, "Include");
            if (Directory.Exists(includeDir))
            {
                var version = Directory.GetDirectories(includeDir)
                    .Select(Path.GetFileName)
                    .Where(v => v != null && v.StartsWith("10."))
                    .OrderByDescending(v => v)
                    .FirstOrDefault();
                
                if (version != null)
                    return (sdkPath, version);
            }
        }
        
        return (null, null);
    }
    
    private static string GetVisualStudioVersion(string installPath)
    {
        if (installPath.Contains("2022")) return "17.0";
        if (installPath.Contains("2019")) return "16.0";
        if (installPath.Contains("2017")) return "15.0";
        return "Unknown";
    }
}
