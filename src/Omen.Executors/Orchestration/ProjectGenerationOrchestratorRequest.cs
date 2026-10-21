namespace Omen.Executors.Orchestration;

public sealed class ProjectGenerationOrchestratorRequest
{
	public required string ProjectRoot { get; init; }

	public required IdeKind Ide { get; init; }
}
