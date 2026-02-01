// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Runtime.InteropServices;
using Omen.Core.Configuration;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Unix;

/// <summary>
/// SDK detector for Linux systems.
/// </summary>
public sealed class LinuxSDK : IPlatformSDK
{
    public TargetPlatform Platform => TargetPlatform.Linux;
    public string Name => "Linux Clang/GCC";
    
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    
    public IReadOnlyList<TargetArchitecture> SupportedArchitectures =>
        [TargetArchitecture.X64, TargetArchitecture.ARM64, TargetArchitecture.ARMv7];
    
    public SDKInfo? Detect()
    {
        if (!IsAvailable) return null;
        
        // Try to find clang first, then gcc
        var (compilerPath, version) = FindCompiler();
        if (compilerPath == null) return null;
        
        return new SDKInfo
        {
            Version = version ?? "unknown",
            InstallPath = Path.GetDirectoryName(compilerPath) ?? "/usr/bin",
            AdditionalPaths = new Dictionary<string, string>
            {
                ["ClangPath"] = compilerPath,
                ["LldPath"] = FindLld() ?? compilerPath,
                ["ArPath"] = FindTool("ar") ?? "ar"
            }
        };
    }
    
    public IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo)
    {
        return new ClangToolchain(sdkInfo, TargetPlatform.Linux, architecture);
    }
    
    private static (string? Path, string? Version) FindCompiler()
    {
        // Prefer clang
        var clangVersions = new[] { "clang++-18", "clang++-17", "clang++-16", "clang++-15", "clang++" };
        foreach (var clang in clangVersions)
        {
            var path = FindTool(clang);
            if (path != null)
            {
                var version = GetCompilerVersion(path);
                return (path, version);
            }
        }
        
        // Fall back to g++
        var gccVersions = new[] { "g++-13", "g++-12", "g++-11", "g++" };
        foreach (var gcc in gccVersions)
        {
            var path = FindTool(gcc);
            if (path != null)
            {
                var version = GetCompilerVersion(path);
                return (path, version);
            }
        }
        
        return (null, null);
    }
    
    private static string? FindLld()
    {
        var lldVersions = new[] { "ld.lld-18", "ld.lld-17", "ld.lld-16", "ld.lld-15", "ld.lld" };
        foreach (var lld in lldVersions)
        {
            var path = FindTool(lld);
            if (path != null) return path;
        }
        return null;
    }
    
    private static string? FindTool(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/usr/local/bin")
            .Split(':');
        
        foreach (var dir in paths)
        {
            var fullPath = Path.Combine(dir, name);
            if (File.Exists(fullPath))
                return fullPath;
        }
        
        return null;
    }
    
    private static string? GetCompilerVersion(string compilerPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = compilerPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                var output = process.StandardOutput.ReadLine();
                process.WaitForExit();
                
                // Parse version from output like "clang version 17.0.0" or "g++ (GCC) 13.2.0"
                if (output != null)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(output, @"\d+\.\d+(\.\d+)?");
                    if (match.Success)
                        return match.Value;
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        
        return null;
    }
}

/// <summary>
/// SDK detector for FreeBSD systems.
/// </summary>
public sealed class FreeBsdSDK : IPlatformSDK
{
    public TargetPlatform Platform => TargetPlatform.FreeBSD;
    public string Name => "FreeBSD Clang";
    
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD);
    
    public IReadOnlyList<TargetArchitecture> SupportedArchitectures =>
        [TargetArchitecture.X64, TargetArchitecture.ARM64];
    
    public SDKInfo? Detect()
    {
        if (!IsAvailable) return null;
        
        // FreeBSD ships with clang by default
        var clangPath = "/usr/bin/clang++";
        if (!File.Exists(clangPath))
            clangPath = "/usr/local/bin/clang++";
        
        if (!File.Exists(clangPath)) return null;
        
        return new SDKInfo
        {
            Version = GetClangVersion(clangPath) ?? "unknown",
            InstallPath = Path.GetDirectoryName(clangPath) ?? "/usr/bin",
            AdditionalPaths = new Dictionary<string, string>
            {
                ["ClangPath"] = clangPath,
                ["LldPath"] = clangPath,
                ["ArPath"] = "/usr/bin/ar"
            }
        };
    }
    
    public IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo)
    {
        return new ClangToolchain(sdkInfo, TargetPlatform.FreeBSD, architecture);
    }
    
    private static string? GetClangVersion(string path)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                Arguments = "--version",
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
                    var match = System.Text.RegularExpressions.Regex.Match(output, @"\d+\.\d+(\.\d+)?");
                    if (match.Success)
                        return match.Value;
                }
            }
        }
        catch { }
        
        return null;
    }
}
