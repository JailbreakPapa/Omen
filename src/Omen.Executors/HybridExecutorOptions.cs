namespace Omen.Executors;

/// <summary>
/// Options for the hybrid executor.
/// </summary>
public sealed class HybridExecutorOptions
{
	/// <summary>
	/// Maximum local parallel actions. Defaults to half of processor count.
	/// </summary>
	public int? LocalParallelism { get; init; }

	/// <summary>
	/// Maximum concurrent remote actions.
	/// </summary>
	public int MaxRemoteActions { get; init; } = 100;

	/// <summary>
	/// Whether to race local vs remote execution.
	/// </summary>
	public bool UseDynamicExecution { get; init; }

	/// <summary>
	/// Prefer remote execution for compilation.
	/// </summary>
	public bool PreferRemoteForCompilation { get; init; } = true;
}
