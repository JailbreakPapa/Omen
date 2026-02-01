// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Android;

/// <summary>
/// Android NDK toolchain implementation.
/// </summary>
public sealed partial class AndroidNdkToolchain : ToolchainBase
{
    private readonly SDKInfo _sdkInfo;
    private readonly TargetArchitecture _architecture;
    private readonly int _apiLevel;
    
    public override TargetPlatform Platform => TargetPlatform.Android;
    public override TargetArchitecture Architecture => _architecture;
    public override string Name => "Android NDK";
    public override string Version { get; }
    
    public override string CompilerPath { get; }
    public override string LinkerPath { get; }
    public override string ArchiverPath { get; }
    public override string? SysrootPath { get; }
    
    public override string ObjectFileExtension => ".o";
    public override string StaticLibraryExtension => ".a";
    public override string SharedLibraryExtension => ".so";
    public override string ExecutableExtension => "";
    
    public AndroidNdkToolchain(SDKInfo sdkInfo, TargetArchitecture architecture, int apiLevel = 21)
    {
        _sdkInfo = sdkInfo;
        _architecture = architecture;
        _apiLevel = apiLevel;
        Version = sdkInfo.Version;
        
        var ndkPath = sdkInfo.InstallPath;
        var hostTag = GetHostTag();
        var toolchainPath = Path.Combine(ndkPath, "toolchains", "llvm", "prebuilt", hostTag);
        
        CompilerPath = Path.Combine(toolchainPath, "bin", "clang++");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            CompilerPath += ".cmd";
        
        LinkerPath = CompilerPath;
        ArchiverPath = Path.Combine(toolchainPath, "bin", "llvm-ar");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            ArchiverPath += ".exe";
        
        SysrootPath = Path.Combine(toolchainPath, "sysroot");
    }
    
    private static string GetHostTag()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows-x86_64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "darwin-x86_64";
        return "linux-x86_64";
    }
    
    private string GetTargetTriple()
    {
        return _architecture switch
        {
            TargetArchitecture.ARM64 => $"aarch64-linux-android{_apiLevel}",
            TargetArchitecture.ARMv7 => $"armv7a-linux-androideabi{_apiLevel}",
            TargetArchitecture.X64 => $"x86_64-linux-android{_apiLevel}",
            TargetArchitecture.X86 => $"i686-linux-android{_apiLevel}",
            _ => $"aarch64-linux-android{_apiLevel}"
        };
    }
    
    private string GetAbi()
    {
        return _architecture switch
        {
            TargetArchitecture.ARM64 => "arm64-v8a",
            TargetArchitecture.ARMv7 => "armeabi-v7a",
            TargetArchitecture.X64 => "x86_64",
            TargetArchitecture.X86 => "x86",
            _ => "arm64-v8a"
        };
    }
    
    public override async Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-c",
            "-o", $"\"{request.OutputFile}\"",
            $"\"{request.SourceFile}\"",
            $"--target={GetTargetTriple()}",
            $"--sysroot=\"{SysrootPath}\""
        };
        
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
        
        if (request.GenerateDebugInfo)
            args.Add("-g");
        
        // Warning flags
        switch (request.WarningLevel)
        {
            case WarningLevel.Off:
                args.Add("-w");
                break;
            case WarningLevel.Level3:
            case WarningLevel.Level4:
            case WarningLevel.EnableAll:
                args.Add("-Wall");
                args.Add("-Wextra");
                break;
            default:
                args.Add("-Wall");
                break;
        }
        
        if (request.TreatWarningsAsErrors)
            args.Add("-Werror");
        
        if (!request.EnableRTTI)
            args.Add("-fno-rtti");
        
        if (!request.EnableExceptions)
            args.Add("-fno-exceptions");
        
        args.Add("-fPIC");
        args.Add("-DANDROID");
        args.Add($"-DANDROID_API={_apiLevel}");
        
        foreach (var include in request.IncludePaths)
            args.Add($"-I\"{include}\"");
        
        foreach (var def in request.Definitions)
            args.Add($"-D{def}");
        
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
            "-o", $"\"{request.OutputFile}\"",
            $"--target={GetTargetTriple()}",
            $"--sysroot=\"{SysrootPath}\""
        };
        
        if (request.OutputType == TargetType.SharedLibrary)
            args.Add("-shared");
        
        if (request.GenerateDebugInfo)
            args.Add("-g");
        
        if (request.EnableLTO)
            args.Add("-flto");
        
        // Standard library
        args.Add("-static-libstdc++");
        
        foreach (var libPath in request.LibraryPaths)
            args.Add($"-L\"{libPath}\"");
        
        foreach (var obj in request.ObjectFiles)
            args.Add($"\"{obj}\"");
        
        foreach (var lib in request.Libraries)
        {
            if (Path.IsPathRooted(lib))
                args.Add($"\"{lib}\"");
            else
                args.Add($"-l{lib}");
        }
        
        foreach (var lib in request.SystemLibraries)
            args.Add($"-l{lib}");
        
        // Android system libraries
        args.Add("-llog");
        args.Add("-landroid");
        
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
            args.Add($"\"{obj}\"");
        
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
            "-DANDROID",
            "-fdata-sections",
            "-ffunction-sections"
        };
        
        if (configuration is BuildConfiguration.Debug or BuildConfiguration.Development)
        {
            flags.Add("-fno-omit-frame-pointer");
            flags.Add("-funwind-tables");
        }
        
        return flags;
    }
    
    public override IReadOnlyList<string> GetDefaultLinkerFlags(BuildConfiguration configuration)
    {
        var flags = new List<string>
        {
            "-Wl,--gc-sections",
            "-static-libstdc++"
        };
        
        if (configuration is BuildConfiguration.Release or BuildConfiguration.Shipping)
        {
            flags.Add("-Wl,--strip-all");
        }
        
        return flags;
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
