// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Graph;

namespace Omen.Core.Interfaces;

/// <summary>
/// Interface for compiling and linking source code.
/// Each platform implements its own toolchain.
/// </summary>
public interface IToolchain
{
    /// <summary>
    /// Platform this toolchain targets.
    /// </summary>
    TargetPlatform Platform { get; }
    
    /// <summary>
    /// Architecture this toolchain targets.
    /// </summary>
    TargetArchitecture Architecture { get; }
    
    /// <summary>
    /// Display name of the toolchain.
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Version of the toolchain.
    /// </summary>
    string Version { get; }
    
    /// <summary>
    /// Path to the compiler executable.
    /// </summary>
    string CompilerPath { get; }
    
    /// <summary>
    /// Path to the linker executable.
    /// </summary>
    string LinkerPath { get; }
    
    /// <summary>
    /// Path to the archiver (static library creator) executable.
    /// </summary>
    string ArchiverPath { get; }
    
    /// <summary>
    /// Optional sysroot path for cross-compilation.
    /// </summary>
    string? SysrootPath { get; }
    
    /// <summary>
    /// Compiles a source file.
    /// </summary>
    Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default);
    
    /// <summary>
    /// Links object files into an executable or library.
    /// </summary>
    Task<LinkResult> LinkAsync(LinkRequest request, CancellationToken ct = default);
    
    /// <summary>
    /// Creates a static library from object files.
    /// </summary>
    Task<ArchiveResult> ArchiveAsync(ArchiveRequest request, CancellationToken ct = default);
    
    /// <summary>
    /// Gets default compiler flags for the given configuration.
    /// </summary>
    IReadOnlyList<string> GetDefaultCompilerFlags(BuildConfiguration configuration);
    
    /// <summary>
    /// Gets default linker flags for the given configuration.
    /// </summary>
    IReadOnlyList<string> GetDefaultLinkerFlags(BuildConfiguration configuration);
    
    /// <summary>
    /// Gets the object file extension for this toolchain.
    /// </summary>
    string ObjectFileExtension { get; }
    
    /// <summary>
    /// Gets the static library extension for this toolchain.
    /// </summary>
    string StaticLibraryExtension { get; }
    
    /// <summary>
    /// Gets the shared library extension for this toolchain.
    /// </summary>
    string SharedLibraryExtension { get; }
    
    /// <summary>
    /// Gets the executable extension for this toolchain.
    /// </summary>
    string ExecutableExtension { get; }

    /// <summary>
    /// Gets environment variables required for this toolchain (e.g., INCLUDE, LIB paths for MSVC).
    /// </summary>
    IReadOnlyDictionary<string, string> Environment { get; }
}

/// <summary>
/// Request to compile a source file.
/// </summary>
public sealed class CompileRequest
{
    public required string SourceFile { get; init; }
    public required string OutputFile { get; init; }
    public required BuildConfiguration Configuration { get; init; }
    public IReadOnlyList<string> IncludePaths { get; init; } = [];
    public IReadOnlyList<string> Definitions { get; init; } = [];
    public IReadOnlyList<string> AdditionalFlags { get; init; } = [];
    public CppStandard CppStandard { get; init; } = CppStandard.Cpp20;
    public OptimizationLevel Optimization { get; init; } = OptimizationLevel.Debug;
    public WarningLevel WarningLevel { get; init; } = WarningLevel.Level4;
    public bool TreatWarningsAsErrors { get; init; } = false;
    public bool EnableRTTI { get; init; } = true;
    public bool EnableExceptions { get; init; } = true;
    public bool GenerateDebugInfo { get; init; } = true;
    public string? PrecompiledHeader { get; init; }
    public bool CreatePrecompiledHeader { get; init; } = false;
}

/// <summary>
/// Result of a compilation.
/// </summary>
public sealed class CompileResult
{
    public required bool Success { get; init; }
    public required string OutputFile { get; init; }
    public required TimeSpan Duration { get; init; }
    public string Output { get; init; } = "";
    public string ErrorOutput { get; init; } = "";
    public int ExitCode { get; init; }
    public IReadOnlyList<CompileDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// A diagnostic message from compilation.
/// </summary>
public sealed class CompileDiagnostic
{
    public required DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? Code { get; init; }
}

public enum DiagnosticSeverity
{
    Note,
    Warning,
    Error
}

/// <summary>
/// Request to link object files.
/// </summary>
public sealed class LinkRequest
{
    public required IReadOnlyList<string> ObjectFiles { get; init; }
    public required string OutputFile { get; init; }
    public required TargetType OutputType { get; init; }
    public required BuildConfiguration Configuration { get; init; }
    public IReadOnlyList<string> LibraryPaths { get; init; } = [];
    public IReadOnlyList<string> Libraries { get; init; } = [];
    public IReadOnlyList<string> SystemLibraries { get; init; } = [];
    public IReadOnlyList<string> Frameworks { get; init; } = [];
    public IReadOnlyList<string> AdditionalFlags { get; init; } = [];
    public bool GenerateDebugInfo { get; init; } = true;
    public bool IncrementalLinking { get; init; } = true;
    public bool EnableLTO { get; init; } = false;
}

/// <summary>
/// Result of linking.
/// </summary>
public sealed class LinkResult
{
    public required bool Success { get; init; }
    public required string OutputFile { get; init; }
    public required TimeSpan Duration { get; init; }
    public string Output { get; init; } = "";
    public string ErrorOutput { get; init; } = "";
    public int ExitCode { get; init; }
}

/// <summary>
/// Request to create a static library.
/// </summary>
public sealed class ArchiveRequest
{
    public required IReadOnlyList<string> ObjectFiles { get; init; }
    public required string OutputFile { get; init; }
}

/// <summary>
/// Result of archiving.
/// </summary>
public sealed class ArchiveResult
{
    public required bool Success { get; init; }
    public required string OutputFile { get; init; }
    public required TimeSpan Duration { get; init; }
    public string Output { get; init; } = "";
    public string ErrorOutput { get; init; } = "";
    public int ExitCode { get; init; }
}
