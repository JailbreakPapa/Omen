// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Options;
using Omen.Core.Rules;

namespace Omen.Executors.Orchestration;

public sealed class OptionsOrchestratorRequest
{
    public required string TargetFile { get; init; }
}

/// <summary>
/// Discovers a project's declared build options without building anything - the Omen
/// equivalent of a CMake Configure pass. Reuses BuildOrchestrator's resolve-and-instantiate
/// sequence (target file -> rule compilation -> BuildContext -> CreateTargetRules/
/// CreateModuleRules) and stops there: instantiating rules is what runs BuildOptions.Declare
/// calls and populates BuildContext.DeclaredOptions, which is the entire discovery mechanism.
/// </summary>
public sealed class OptionsOrchestrator
{
    public async Task<IReadOnlyList<BuildOptionDeclaration>?> DiscoverAsync(
        OptionsOrchestratorRequest request,
        IProgress<OrchestratorEvent>? events,
        CancellationToken ct = default)
    {
        var workingDir = ResolveWorkingDir(request.TargetFile);
        var cacheStore = new OptionCacheStore(CachePath(workingDir));

        var context = new BuildContext
        {
            Platform = TargetPlatform.Windows,
            Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Development,
            ProjectRoot = workingDir,
            IntermediateDirectory = Path.Combine(workingDir, "Intermediate"),
            OutputDirectory = Path.Combine(workingDir, "Binaries"),
            CachedOptionValues = cacheStore.Load()
        };

        var ruleCompiler = new RuleCompiler(Path.Combine(workingDir, "Intermediate", "RuleCache"));

        CompiledRules compiledRules;
        try
        {
            events?.Report(new OrchestratorEvent("Compiling build rules...", OrchestratorEventLevel.Info));
            compiledRules = await ruleCompiler.CompileRulesAsync(workingDir, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            events?.Report(new OrchestratorEvent($"Error compiling rules: {ex.Message}", OrchestratorEventLevel.Error));
            return null;
        }

        compiledRules.CreateTargetRules(context);
        compiledRules.CreateModuleRules(context);

        events?.Report(new OrchestratorEvent($"Found {context.DeclaredOptions.Count} option(s)", OrchestratorEventLevel.Info));

        return context.DeclaredOptions;
    }

    public void SaveOptions(string targetFile, IReadOnlyDictionary<string, string> values)
    {
        var workingDir = ResolveWorkingDir(targetFile);
        new OptionCacheStore(CachePath(workingDir)).Save(values);
    }

    private static string ResolveWorkingDir(string targetFile) =>
        Path.GetDirectoryName(targetFile) ?? Path.GetPathRoot(targetFile) ?? targetFile;

    private static string CachePath(string workingDir) =>
        Path.Combine(workingDir, "Intermediate", "omen-cache.json");
}
