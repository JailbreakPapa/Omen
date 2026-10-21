// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Options;
using Omen.Core.Rules;

namespace Omen.Core.Tests;

/// <summary>
/// Project-wide build options - CMake's option()/set(... CACHE ...). They are declared by the
/// project definition, which has no BuildContext, so the saved values reach it another way and
/// the declarations have to reach every context afterwards.
/// </summary>
public class ProjectOptionsTests : IDisposable
{
    private readonly string _projectRoot;

    public ProjectOptionsTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenProjectOptions_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Source", "App"));
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Intermediate"));
        File.WriteAllText(Path.Combine(_projectRoot, "Source", "App", "main.cpp"), "int main() { return 0; }\n");
        File.WriteAllText(Path.Combine(_projectRoot, "Source", "App", "App.module.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppModule : ModuleRules
            {
                public AppModule(BuildContext context) : base(context) { SourceDirectory = "Source/App"; }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "App.target.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class AppTarget : TargetRules
            {
                public AppTarget(BuildContext context) : base(context)
                {
                    Type = TargetType.Executable;
                    LaunchModuleName = "AppModule";
                }
            }
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "Sample.omenproject.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Options;
            using Omen.Core.Rules;

            public class SampleProject : OmenProjectRules
            {
                public SampleProject()
                {
                    SourceRoots.Add(".");
                    UnityBuild = DeclareOption("LY_UNITY_BUILD", "Unity builds", true);
                    if (DeclareOption("LY_MONOLITHIC_GAME", "Monolithic", false))
                        LinkTypeOverride = LinkType.Monolithic;
                    var jobs = DeclareOption("LY_PARALLEL_COMPILE_JOBS", "Compile jobs", 0);
                    if (jobs > 0)
                        MaxParallelActions = jobs;
                    DeclareOption("LY_INSTALLER_LICENSE_URL", "License URL", "", advanced: true);
                    if (DeclareOption(WellKnownOptions.ThirdPartyOptions, "Third-party options", false))
                        DeclareOption("MINIAUDIO_USE_VORBIS", "Vorbis support", true, advanced: true);
                }
            }
            """);
    }

    public void Dispose() => Directory.Delete(_projectRoot, recursive: true);

    private void SaveOptionValues(string json) =>
        File.WriteAllText(Path.Combine(_projectRoot, "Intermediate", "omen-cache.json"), json);

    private async Task<CompiledRules> CompileAsync()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Intermediate"));
        return await new RuleCompiler(RuleCompiler.CacheDirectoryFor(_projectRoot)).CompileRulesAsync(_projectRoot);
    }

    private BuildContext CreateContext() => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = BuildConfiguration.Development,
        ProjectRoot = _projectRoot,
        OutputDirectory = Path.Combine(_projectRoot, "bin"),
        IntermediateDirectory = Path.Combine(_projectRoot, "obj")
    };

    [Fact]
    public async Task DeclaredOptions_ReachEveryBuildContext_WithTheirDefaults()
    {
        // The Options panel and `omen configure` both read BuildContext.DeclaredOptions; the
        // project definition is evaluated long before any context exists.
        SaveOptionValues("{}");
        var rules = await CompileAsync();
        var context = CreateContext();

        rules.CreateModuleRules(context);

        context.Option("LY_UNITY_BUILD").Should().Be("true");
        context.OptionBool("LY_MONOLITHIC_GAME").Should().BeFalse();
        context.DeclaredOptions.Should().Contain(o => o.Name == "LY_INSTALLER_LICENSE_URL" && o.IsAdvanced);
        rules.Project!.UnityBuild.Should().BeTrue();
        rules.Project.LinkTypeOverride.Should().BeNull();
    }

    [Fact]
    public async Task SavedValues_OverrideDefaults_AndDriveTheProjectsOwnKnobs()
    {
        SaveOptionValues("""{"LY_UNITY_BUILD":"false","LY_MONOLITHIC_GAME":"true","LY_PARALLEL_COMPILE_JOBS":"6"}""");
        var rules = await CompileAsync();
        var context = CreateContext();
        rules.CreateTargetRules(context);

        context.Option("LY_UNITY_BUILD").Should().Be("false");
        rules.Project!.UnityBuild.Should().BeFalse();
        rules.Project.LinkTypeOverride.Should().Be(LinkType.Monolithic);
        rules.Project.MaxParallelActions.Should().Be(6);
        context.OptionBool("LY_MONOLITHIC_GAME").Should().BeTrue();
    }

    [Fact]
    public async Task ThirdPartyOptions_AreDeclaredOnlyWhenTheToggleIsOn()
    {
        // Hundreds of vendored-library options are not worth evaluating - or scrolling past -
        // unless asked for, which is what the GUI checkbox and `omen configure --third-party` set.
        SaveOptionValues("{}");
        var off = await CompileAsync();
        var offContext = CreateContext();
        off.CreateModuleRules(offContext);
        offContext.DeclaredOptions.Should().NotContain(o => o.Name == "MINIAUDIO_USE_VORBIS");

        SaveOptionValues($$"""{"{{WellKnownOptions.ThirdPartyOptions}}":"true"}""");
        var on = await CompileAsync();
        var onContext = CreateContext();
        on.CreateModuleRules(onContext);
        onContext.DeclaredOptions.Should().Contain(o => o.Name == "MINIAUDIO_USE_VORBIS");
    }

    [Fact]
    public async Task ChangingASavedValue_ReEvaluatesTheProjectDefinition()
    {
        // The definition is memoized per process; a changed option has to invalidate it, or the
        // GUI would show the old value right after saving a new one.
        SaveOptionValues("{}");
        (await CompileAsync()).Project!.UnityBuild.Should().BeTrue();

        SaveOptionValues("""{"LY_UNITY_BUILD":"false"}""");

        (await CompileAsync()).Project!.UnityBuild.Should().BeFalse();
    }
}
