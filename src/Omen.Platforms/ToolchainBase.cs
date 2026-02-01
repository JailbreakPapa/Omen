// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Interfaces;

namespace Omen.Platforms;

/// <summary>
/// Base class for all toolchain implementations.
/// </summary>
public abstract class ToolchainBase : IToolchain
{
    public abstract TargetPlatform Platform { get; }
    public abstract TargetArchitecture Architecture { get; }
    public abstract string Name { get; }
    public abstract string Version { get; }
    public abstract string CompilerPath { get; }
    public abstract string LinkerPath { get; }
    public abstract string ArchiverPath { get; }
    public virtual string? SysrootPath => null;
    
    public abstract string ObjectFileExtension { get; }
    public abstract string StaticLibraryExtension { get; }
    public abstract string SharedLibraryExtension { get; }
    public abstract string ExecutableExtension { get; }

    public virtual IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>();
    
    public abstract Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default);
    public abstract Task<LinkResult> LinkAsync(LinkRequest request, CancellationToken ct = default);
    public abstract Task<ArchiveResult> ArchiveAsync(ArchiveRequest request, CancellationToken ct = default);
    
    public abstract IReadOnlyList<string> GetDefaultCompilerFlags(BuildConfiguration configuration);
    public abstract IReadOnlyList<string> GetDefaultLinkerFlags(BuildConfiguration configuration);
    
    /// <summary>
    /// Runs a process and captures output.
    /// </summary>
    protected async Task<ProcessResult> RunProcessAsync(
        string executable, 
        string arguments,
        string workingDirectory,
        Dictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }
        
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        
        var sw = System.Diagnostics.Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync(ct);
        sw.Stop();
        
        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            Duration = sw.Elapsed
        };
    }
    
    /// <summary>
    /// Parses diagnostics from compiler output.
    /// </summary>
    protected abstract IReadOnlyList<CompileDiagnostic> ParseDiagnostics(string output);
    
    protected record ProcessResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = "";
        public string StandardError { get; init; } = "";
        public TimeSpan Duration { get; init; }
    }
}


