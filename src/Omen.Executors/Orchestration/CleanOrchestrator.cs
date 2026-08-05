// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Executors.Orchestration;

public sealed class CleanOrchestratorRequest
{
    public required string ProjectRoot { get; init; }
    public string? Platform { get; init; }
    public string? Configuration { get; init; }
    public bool All { get; init; }
}

public sealed class CleanOrchestratorResult
{
    public required int DirectoriesCleaned { get; init; }
    public required int DirectoriesFailed { get; init; }
}

public sealed class CleanOrchestrator
{
    public Task<CleanOrchestratorResult> CleanAsync(CleanOrchestratorRequest request, IProgress<OrchestratorEvent>? events)
    {
        var intermediateDir = Path.Combine(request.ProjectRoot, "Intermediate");
        var binariesDir = Path.Combine(request.ProjectRoot, "Binaries");

        var dirsToClean = new List<string>();

        if (request.All || (string.IsNullOrEmpty(request.Platform) && string.IsNullOrEmpty(request.Configuration)))
        {
            if (Directory.Exists(intermediateDir)) dirsToClean.Add(intermediateDir);
            if (Directory.Exists(binariesDir)) dirsToClean.Add(binariesDir);
        }
        else
        {
            var pattern = $"{request.Platform ?? "*"}_{request.Configuration ?? "*"}";

            if (Directory.Exists(intermediateDir))
                dirsToClean.AddRange(Directory.GetDirectories(intermediateDir, pattern));

            if (Directory.Exists(binariesDir))
                dirsToClean.AddRange(Directory.GetDirectories(binariesDir, pattern));
        }

        if (dirsToClean.Count == 0)
        {
            events?.Report(new OrchestratorEvent("Nothing to clean.", OrchestratorEventLevel.Warning));
            return Task.FromResult(new CleanOrchestratorResult { DirectoriesCleaned = 0, DirectoriesFailed = 0 });
        }

        events?.Report(new OrchestratorEvent($"Cleaning {dirsToClean.Count} directories...", OrchestratorEventLevel.Info));

        var cleaned = 0;
        var failed = 0;
        foreach (var dir in dirsToClean)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                events?.Report(new OrchestratorEvent($"✓ {Path.GetRelativePath(request.ProjectRoot, dir)}", OrchestratorEventLevel.Success));
                cleaned++;
            }
            catch (Exception ex)
            {
                events?.Report(new OrchestratorEvent($"✗ {Path.GetRelativePath(request.ProjectRoot, dir)}: {ex.Message}", OrchestratorEventLevel.Error));
                failed++;
            }
        }

        events?.Report(new OrchestratorEvent("Clean complete.", OrchestratorEventLevel.Success));

        return Task.FromResult(new CleanOrchestratorResult { DirectoriesCleaned = cleaned, DirectoriesFailed = failed });
    }
}
