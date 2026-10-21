using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Omen.Core.Configuration;
using Omen.Core.Interfaces;
using Omen.Core.Rules;

namespace Omen.Platforms;

/// <summary>
/// Base class for all toolchain implementations.
/// </summary>
public abstract class ToolchainBase : IToolchain
{
	protected record ProcessResult
	{
		public int ExitCode { get; init; }

		public string StandardOutput { get; init; } = "";

		public string StandardError { get; init; } = "";

		public TimeSpan Duration { get; init; }
	}

	public abstract TargetPlatform Platform { get; }

	public abstract TargetArchitecture Architecture { get; }

	public abstract string Name { get; }

	public abstract string Version { get; }

	public abstract string CompilerPath { get; }

	public abstract string LinkerPath { get; }

	public abstract string ArchiverPath { get; }

	/// <summary>
	/// Defaults by platform, which is what every existing toolchain here happens to match
	/// (MSVC on Windows, clang everywhere else). A clang-cl-on-Windows or gcc-on-Linux
	/// toolchain overrides it rather than being mis-detected.
	/// </summary>
	public virtual CompilerFamily CompilerFamily
	{
		get
		{
			if (Platform != TargetPlatform.Windows)
			{
				return CompilerFamily.Clang;
			}
			return CompilerFamily.Msvc;
		}
	}

	public virtual string? SysrootPath => null;

	public virtual string? ResourceCompilerPath => null;

	public abstract string ObjectFileExtension { get; }

	public abstract string StaticLibraryExtension { get; }

	public abstract string SharedLibraryExtension { get; }

	public abstract string ExecutableExtension { get; }

	public virtual IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>();

	public abstract Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default(CancellationToken));

	public abstract Task<LinkResult> LinkAsync(LinkRequest request, CancellationToken ct = default(CancellationToken));

	public abstract Task<ArchiveResult> ArchiveAsync(ArchiveRequest request, CancellationToken ct = default(CancellationToken));

	public abstract IReadOnlyList<string> GetDefaultCompilerFlags(BuildConfiguration configuration);

	public abstract IReadOnlyList<string> GetDefaultLinkerFlags(BuildConfiguration configuration);

	/// <summary>
	/// Runs a process and captures output.
	/// </summary>
	protected async Task<ProcessResult> RunProcessAsync(string executable, string arguments, string workingDirectory, Dictionary<string, string>? environment = null, CancellationToken ct = default(CancellationToken))
	{
		ProcessStartInfo startInfo = new ProcessStartInfo
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
		using Process process = new Process
		{
			StartInfo = startInfo
		};
		StringBuilder stdout = new StringBuilder();
		StringBuilder stderr = new StringBuilder();
		process.OutputDataReceived += (object _, DataReceivedEventArgs e) =>
		{
			if (e.Data != null)
			{
				stdout.AppendLine(e.Data);
			}
		};
		process.ErrorDataReceived += (object _, DataReceivedEventArgs e) =>
		{
			if (e.Data != null)
			{
				stderr.AppendLine(e.Data);
			}
		};
		Stopwatch sw = Stopwatch.StartNew();
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
}
