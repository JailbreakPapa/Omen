// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.RegularExpressions;
using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Windows;

/// <summary>
/// Microsoft Visual C++ (MSVC) toolchain implementation.
/// </summary>
public sealed partial class MsvcToolchain : ToolchainBase
{
    private readonly SDKInfo _sdkInfo;
    private readonly TargetArchitecture _architecture;
    
    public override TargetPlatform Platform => TargetPlatform.Windows;
    public override TargetArchitecture Architecture => _architecture;
    public override string Name => "MSVC";
    public override string Version { get; }
    
    public override string CompilerPath { get; }
    public override string LinkerPath { get; }
    public override string ArchiverPath { get; }
    
    public override string ObjectFileExtension => ".obj";
    public override string StaticLibraryExtension => ".lib";
    public override string SharedLibraryExtension => ".dll";
    public override string ExecutableExtension => ".exe";
    
    private readonly Dictionary<string, string> _environment;

    public override IReadOnlyDictionary<string, string> Environment => _environment;
    
    public MsvcToolchain(SDKInfo sdkInfo, TargetArchitecture architecture)
    {
        _sdkInfo = sdkInfo;
        _architecture = architecture;
        Version = sdkInfo.Version;
        
        var vcToolsPath = sdkInfo.AdditionalPaths.GetValueOrDefault("VCToolsInstallDir", "");
        var hostArch = System.Environment.Is64BitOperatingSystem ? "x64" : "x86";
        var targetArch = architecture switch
        {
            TargetArchitecture.X64 => "x64",
            TargetArchitecture.X86 => "x86",
            TargetArchitecture.ARM64 => "arm64",
            _ => "x64"
        };
        
        var binPath = Path.Combine(vcToolsPath, "bin", $"Host{hostArch}", targetArch);
        CompilerPath = Path.Combine(binPath, "cl.exe");
        LinkerPath = Path.Combine(binPath, "link.exe");
        ArchiverPath = Path.Combine(binPath, "lib.exe");
        
        _environment = BuildEnvironment(sdkInfo, architecture);
    }
    
    private static Dictionary<string, string> BuildEnvironment(SDKInfo sdkInfo, TargetArchitecture arch)
    {
        var env = new Dictionary<string, string>(sdkInfo.EnvironmentVariables);
        
        var vcToolsPath = sdkInfo.AdditionalPaths.GetValueOrDefault("VCToolsInstallDir", "");
        var windowsSdkDir = sdkInfo.AdditionalPaths.GetValueOrDefault("WindowsSdkDir", "");
        var windowsSdkVersion = sdkInfo.AdditionalPaths.GetValueOrDefault("WindowsSdkVersion", "");
        
        var archStr = arch switch
        {
            TargetArchitecture.X64 => "x64",
            TargetArchitecture.X86 => "x86",
            TargetArchitecture.ARM64 => "arm64",
            _ => "x64"
        };
        
        var includePaths = new List<string>
        {
            Path.Combine(vcToolsPath, "include"),
            Path.Combine(windowsSdkDir, "Include", windowsSdkVersion, "ucrt"),
            Path.Combine(windowsSdkDir, "Include", windowsSdkVersion, "shared"),
            Path.Combine(windowsSdkDir, "Include", windowsSdkVersion, "um"),
            Path.Combine(windowsSdkDir, "Include", windowsSdkVersion, "winrt")
        };
        
        var libPaths = new List<string>
        {
            Path.Combine(vcToolsPath, "lib", archStr),
            Path.Combine(windowsSdkDir, "Lib", windowsSdkVersion, "ucrt", archStr),
            Path.Combine(windowsSdkDir, "Lib", windowsSdkVersion, "um", archStr)
        };
        
        env["INCLUDE"] = string.Join(";", includePaths);
        env["LIB"] = string.Join(";", libPaths);
        
        return env;
    }
    
    public override async Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "/nologo",
            "/c",
            $"/Fo\"{request.OutputFile}\"",
            $"\"{request.SourceFile}\""
        };
        
        // C++ standard
        args.Add(request.CppStandard switch
        {
            CppStandard.Cpp14 => "/std:c++14",
            CppStandard.Cpp17 => "/std:c++17",
            CppStandard.Cpp20 => "/std:c++20",
            CppStandard.Cpp23 => "/std:c++latest",
            CppStandard.Latest => "/std:c++latest",
            _ => "/std:c++20"
        });
        
        // Optimization
        args.Add(request.Optimization switch
        {
            OptimizationLevel.Disabled => "/Od",
            OptimizationLevel.Debug => "/Od",
            OptimizationLevel.Development => "/O1",
            OptimizationLevel.Shipping => "/O2",
            OptimizationLevel.Size => "/Os",
            OptimizationLevel.SizeAndSpeed => "/Ox",
            _ => "/Od"
        });
        
        // Debug info
        if (request.GenerateDebugInfo)
        {
            args.Add("/Zi");
            args.Add($"/Fd\"{Path.ChangeExtension(request.OutputFile, ".pdb")}\"");
        }
        
        // Warning level
        args.Add(request.WarningLevel switch
        {
            WarningLevel.Off => "/W0",
            WarningLevel.Level1 => "/W1",
            WarningLevel.Level2 => "/W2",
            WarningLevel.Level3 => "/W3",
            WarningLevel.Level4 => "/W4",
            WarningLevel.EnableAll => "/Wall",
            _ => "/W4"
        });
        
        if (request.TreatWarningsAsErrors)
            args.Add("/WX");
        
        // RTTI
        args.Add(request.EnableRTTI ? "/GR" : "/GR-");
        
        // Exceptions
        if (request.EnableExceptions)
            args.Add("/EHsc");
        
        // Include paths
        foreach (var include in request.IncludePaths)
        {
            args.Add($"/I\"{include}\"");
        }
        
        // Definitions
        foreach (var def in request.Definitions)
        {
            args.Add($"/D{def}");
        }
        
        // PCH
        if (request.CreatePrecompiledHeader)
        {
            args.Add($"/Yc\"{Path.GetFileName(request.SourceFile)}\"");
            args.Add($"/Fp\"{request.OutputFile}\"");
        }
        else if (request.PrecompiledHeader != null)
        {
            args.Add($"/Yu\"{Path.GetFileName(request.PrecompiledHeader)}\"");
            args.Add($"/Fp\"{request.PrecompiledHeader}\"");
        }
        
        // Additional flags
        args.AddRange(request.AdditionalFlags);
        
        // Ensure output directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputFile)!);
        
        var result = await RunProcessAsync(
            CompilerPath, 
            string.Join(" ", args),
            Path.GetDirectoryName(request.SourceFile)!,
            _environment,
            ct);
        
        return new CompileResult
        {
            Success = result.ExitCode == 0,
            OutputFile = request.OutputFile,
            Duration = result.Duration,
            Output = result.StandardOutput,
            ErrorOutput = result.StandardError,
            ExitCode = result.ExitCode,
            Diagnostics = ParseDiagnostics(result.StandardOutput + result.StandardError)
        };
    }
    
    public override async Task<LinkResult> LinkAsync(LinkRequest request, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "/nologo",
            $"/OUT:\"{request.OutputFile}\""
        };
        
        // Output type
        if (request.OutputType == TargetType.SharedLibrary)
        {
            args.Add("/DLL");
        }
        
        // Debug info
        if (request.GenerateDebugInfo)
        {
            args.Add("/DEBUG");
            args.Add($"/PDB:\"{Path.ChangeExtension(request.OutputFile, ".pdb")}\"");
        }
        
        // Incremental linking
        args.Add(request.IncrementalLinking ? "/INCREMENTAL" : "/INCREMENTAL:NO");
        
        // LTO
        if (request.EnableLTO)
        {
            args.Add("/LTCG");
        }
        
        // Library paths
        foreach (var libPath in request.LibraryPaths)
        {
            args.Add($"/LIBPATH:\"{libPath}\"");
        }
        
        // Object files
        foreach (var obj in request.ObjectFiles)
        {
            args.Add($"\"{obj}\"");
        }
        
        // Libraries
        foreach (var lib in request.Libraries)
        {
            args.Add(lib.EndsWith(".lib") ? $"\"{lib}\"" : $"{lib}.lib");
        }
        
        // System libraries
        foreach (var lib in request.SystemLibraries)
        {
            args.Add($"{lib}.lib");
        }
        
        // Additional flags
        args.AddRange(request.AdditionalFlags);
        
        // Ensure output directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputFile)!);
        
        var result = await RunProcessAsync(
            LinkerPath,
            string.Join(" ", args),
            Path.GetDirectoryName(request.OutputFile)!,
            _environment,
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
        var args = new List<string>
        {
            "/nologo",
            $"/OUT:\"{request.OutputFile}\""
        };
        
        foreach (var obj in request.ObjectFiles)
        {
            args.Add($"\"{obj}\"");
        }
        
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputFile)!);
        
        var result = await RunProcessAsync(
            ArchiverPath,
            string.Join(" ", args),
            Path.GetDirectoryName(request.OutputFile)!,
            _environment,
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
            "/nologo",
            "/MP", // Multi-process compilation
            "/GS", // Buffer security check
            "/Gd", // __cdecl calling convention
            "/fp:precise",
            "/Zc:wchar_t",
            "/Zc:forScope",
            "/Zc:inline"
        };
        
        if (configuration is BuildConfiguration.Debug or BuildConfiguration.Development)
        {
            flags.Add("/MDd"); // Debug runtime
            flags.Add("/RTC1"); // Runtime checks
        }
        else
        {
            flags.Add("/MD"); // Release runtime
            flags.Add("/GL"); // Whole program optimization
        }
        
        return flags;
    }
    
    public override IReadOnlyList<string> GetDefaultLinkerFlags(BuildConfiguration configuration)
    {
        var flags = new List<string>
        {
            "/nologo",
            "/DYNAMICBASE",
            "/NXCOMPAT"
        };
        
        if (_architecture == TargetArchitecture.X64)
        {
            flags.Add("/MACHINE:X64");
            flags.Add("/HIGHENTROPYVA");
        }
        else if (_architecture == TargetArchitecture.X86)
        {
            flags.Add("/MACHINE:X86");
            flags.Add("/SAFESEH");
        }
        else if (_architecture == TargetArchitecture.ARM64)
        {
            flags.Add("/MACHINE:ARM64");
        }
        
        if (configuration is BuildConfiguration.Release or BuildConfiguration.Shipping)
        {
            flags.Add("/OPT:REF");
            flags.Add("/OPT:ICF");
        }
        
        return flags;
    }
    
    protected override IReadOnlyList<CompileDiagnostic> ParseDiagnostics(string output)
    {
        var diagnostics = new List<CompileDiagnostic>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var match = MsvcDiagnosticRegex().Match(line);
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
                    Code = match.Groups[4].Value,
                    Message = match.Groups[5].Value.Trim()
                });
            }
        }
        
        return diagnostics;
    }
    
    [GeneratedRegex(@"^(.+?)\((\d+)\):\s*(error|warning|note)\s+(\w+):\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MsvcDiagnosticRegex();
}

