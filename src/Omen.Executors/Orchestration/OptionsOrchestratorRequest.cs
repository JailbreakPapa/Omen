using Omen.Core.Configuration;

namespace Omen.Executors.Orchestration;

public sealed class OptionsOrchestratorRequest
{
	public required string TargetFile { get; init; }
    public TargetPlatform? Platform { get; init; }
    public TargetArchitecture? Architecture { get; init; }
    public BuildConfiguration Configuration { get; init; } = BuildConfiguration.Development;
}
