// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Unix;

/// <summary>
/// Clang toolchain implementation for Linux and FreeBSD.
/// </summary>
public sealed partial class ClangToolchain : ToolchainBase
{
    private readonly SDKInfo _sdkInfo;
    private readonly TargetPlatform _platform;
    private readonly TargetArchitecture _architecture;
    
    public override TargetPlatform Platform => _platform;
    public override TargetArchitecture Architecture => _architecture;
    public override string Name => "Clang";
    public override string Version { get; }
    
    public override string CompilerPath { get; }
    public override string LinkerPath { get; }
    public override string ArchiverPath { get; }
    public override string? SysrootPath { get; }
    
    public override string ObjectFileExtension => ".o";
    public override string StaticLibraryExtension => ".a";
    public override string SharedLibraryExtension => ".so";
    public override string ExecutableExtension => "";
    
    public ClangToolchain(SDKInfo sdkInfo, TargetPlatform platform, TargetArchitecture architecture)
    {
        _sdkInfo = sdkInfo;
        _platform = platform;
        _architecture = architecture;
        Version = sdkInfo.Version;
        
        CompilerPath = sdkInfo.AdditionalPaths.GetValueOrDefault("ClangPath", "clang++");
        LinkerPath = sdkInfo.AdditionalPaths.GetValueOrDefault("LldPath", CompilerPath);
        ArchiverPath = sdkInfo.AdditionalPaths.GetValueOrDefault("ArPath", "ar");
        SysrootPath = sdkInfo.AdditionalPaths.GetValueOrDefault("Sysroot");
    }
    
    public override async Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-c",
            "-o", $"\"{request.OutputFile}\"",
            $"\"{request.SourceFile}\""
        };
        
        // Target triple for cross-compilation
        args.Add($"--target={GetTargetTriple()}");
        
        // Sysroot
        if (SysrootPath != null)
        {
            args.Add($"--sysroot=\"{SysrootPath}\"");
        }
        
        // C++ standard
        args.Add(request.CppStandard switch
        {
            CppStandard.Cpp14 => "-std=c++14",
            CppStandard.Cpp17 => "-std=c++17",
            CppStandard.Cpp20 => "-std=c++20",
            CppStandard.Cpp23 => "-std=c++23",
            CppStandard.Latest => "-std=c++2c",
            _ => "-std=c++20"
        });
        
        // Optimization
        args.Add(request.Optimization switch
        {
            OptimizationLevel.Disabled => "-O0",
            OptimizationLevel.Debug => "-O0",
            OptimizationLevel.Development => "-O1",
            OptimizationLevel.Shipping => "-O3",
            OptimizationLevel.Size => "-Os",
            OptimizationLevel.SizeAndSpeed => "-O2",
            _ => "-O0"
        });
        
        // Debug info
        if (request.GenerateDebugInfo)
        {
            args.Add("-g");
        }
        
        // Warning level
        switch (request.WarningLevel)
        {
            case WarningLevel.Off:
                args.Add("-w");
                break;
            case WarningLevel.Level1:
                args.Add("-Wall");
                break;
            case WarningLevel.Level2:
                args.Add("-Wall");
                break;
            case WarningLevel.Level3:
                args.Add("-Wall");
                args.Add("-Wextra");
                break;
            case WarningLevel.Level4:
            case WarningLevel.EnableAll:
                args.Add("-Wall");
                args.Add("-Wextra");
                args.Add("-Wpedantic");
                break;
        }
        
        if (request.TreatWarningsAsErrors)
            args.Add("-Werror");
        
        // RTTI
        if (!request.EnableRTTI)
            args.Add("-fno-rtti");
        
        // Exceptions
        if (!request.EnableExceptions)
            args.Add("-fno-exceptions");
        
        // PIC for shared libraries
        args.Add("-fPIC");
        
        // Include paths
        foreach (var include in request.IncludePaths)
        {
            args.Add($"-I\"{include}\"");
        }
        
        // Definitions
        foreach (var def in request.Definitions)
        {
            args.Add($"-D{def}");
        }
        
        // PCH
        if (request.CreatePrecompiledHeader)
        {
            args.Add("-x");
            args.Add("c++-header");
        }
        else if (request.PrecompiledHeader != null)
        {
            args.Add($"-include-pch");
            args.Add($"\"{request.PrecompiledHeader}\"");
        }
        
        // Additional flags
        args.AddRange(request.AdditionalFlags);
        
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputFile)!);
        
        var result = await RunProcessAsync(
            CompilerPath,
            string.Join(" ", args),
            Path.GetDirectoryName(request.SourceFile)!,
            null,
            ct);
        
        return new CompileResult
        {
            Success = result.ExitCode == 0,
            OutputFile = request.OutputFile,
            Duration = result.Duration,
            Output = result.StandardOutput,
            ErrorOutput = result.StandardError,
            ExitCode = result.ExitCode,
            Diagnostics = ParseDiagnostics(result.StandardError)
        };
    }
    
    public override async Task<LinkResult> LinkAsync(LinkRequest request, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-o", $"\"{request.OutputFile}\""
        };
        
        args.Add($"--target={GetTargetTriple()}");
        
        if (SysrootPath != null)
        {
            args.Add($"--sysroot=\"{SysrootPath}\"");
        }
        
        // Output type
        if (request.OutputType == TargetType.SharedLibrary)
        {
            args.Add("-shared");
        }
        
        // Use LLD linker
        args.Add("-fuse-ld=lld");
        
        // Debug info
        if (request.GenerateDebugInfo)
        {
            args.Add("-g");
        }
        
        // LTO
        if (request.EnableLTO)
        {
            args.Add("-flto");
        }
        
        // Library paths
        foreach (var libPath in request.LibraryPaths)
        {
            args.Add($"-L\"{libPath}\"");
        }
        
        // Object files
        foreach (var obj in request.ObjectFiles)
        {
            args.Add($"\"{obj}\"");
        }
        
        // Libraries
        foreach (var lib in request.Libraries)
        {
            if (Path.IsPathRooted(lib))
                args.Add($"\"{lib}\"");
            else
                args.Add($"-l{lib}");
        }
        
        // System libraries
        foreach (var lib in request.SystemLibraries)
        {
            args.Add($"-l{lib}");
        }
        
        // Additional flags
        args.AddRange(request.AdditionalFlags);
        
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputFile)!);
        
        var result = await RunProcessAsync(
            LinkerPath,
            string.Join(" ", args),
            Path.GetDirectoryName(request.OutputFile)!,
            null,
            ct);
        
        return new LinkResult
        {
            Success = result.ExitCode == 0,
            OutputFile = request.OutputFile,
            Duration = result.Duration,
            Output = result.StandardOutput,
            ErrorOutput = result.StandardError,
            ExitCode = result.ExitCode
        };
    }
    
    public override async Task<ArchiveResult> ArchiveAsync(ArchiveRequest request, CancellationToken ct = default)
    {
        var args = new List<string> { "rcs", $"\"{request.OutputFile}\"" };
        
        foreach (var obj in request.ObjectFiles)
        {
            args.Add($"\"{obj}\"");
        }
        
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputFile)!);
        
        var result = await RunProcessAsync(
            ArchiverPath,
            string.Join(" ", args),
            Path.GetDirectoryName(request.OutputFile)!,
            null,
            ct);
        
        return new ArchiveResult
        {
            Success = result.ExitCode == 0,
            OutputFile = request.OutputFile,
            Duration = result.Duration,
            Output = result.StandardOutput,
            ErrorOutput = result.StandardError,
            ExitCode = result.ExitCode
        };
    }
    
    public override IReadOnlyList<string> GetDefaultCompilerFlags(BuildConfiguration configuration)
    {
        var flags = new List<string>
        {
            "-fPIC",
            "-fvisibility=hidden",
            "-fvisibility-inlines-hidden"
        };
        
        if (configuration is BuildConfiguration.Debug or BuildConfiguration.Development)
        {
            flags.Add("-fno-omit-frame-pointer");
        }
        
        return flags;
    }
    
    public override IReadOnlyList<string> GetDefaultLinkerFlags(BuildConfiguration configuration)
    {
        var flags = new List<string>
        {
            "-fuse-ld=lld"
        };
        
        if (configuration is BuildConfiguration.Release or BuildConfiguration.Shipping)
        {
            flags.Add("-s"); // Strip symbols
        }
        
        return flags;
    }
    
    private string GetTargetTriple()
    {
        var arch = _architecture switch
        {
            TargetArchitecture.X64 => "x86_64",
            TargetArchitecture.X86 => "i686",
            TargetArchitecture.ARM64 => "aarch64",
            TargetArchitecture.ARMv7 => "armv7",
            _ => "x86_64"
        };
        
        var os = _platform switch
        {
            TargetPlatform.Linux => "linux-gnu",
            TargetPlatform.FreeBSD => "freebsd",
            _ => "linux-gnu"
        };
        
        return $"{arch}-unknown-{os}";
    }
    
    protected override IReadOnlyList<CompileDiagnostic> ParseDiagnostics(string output)
    {
        var diagnostics = new List<CompileDiagnostic>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var match = ClangDiagnosticRegex().Match(line);
            if (match.Success)
            {
                var severity = match.Groups[3].Value.ToLowerInvariant() switch
                {
                    "error" => DiagnosticSeverity.Error,
                    "warning" => DiagnosticSeverity.Warning,
                    _ => DiagnosticSeverity.Note
                };
                
                diagnostics.Add(new CompileDiagnostic
                {
                    Severity = severity,
                    File = match.Groups[1].Value,
                    Line = int.TryParse(match.Groups[2].Value, out var line_) ? line_ : null,
                    Message = match.Groups[4].Value.Trim()
                });
            }
        }
        
        return diagnostics;
    }
    
    [GeneratedRegex(@"^(.+?):(\d+):\d+:\s*(error|warning|note):\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ClangDiagnosticRegex();
}
