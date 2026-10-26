// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text;
using System.Xml.Linq;
using Omen.Core.Configuration;
using Omen.Core.Generators;
using Omen.Core.Rules;

namespace Omen.Core.Tests;

/// <summary>
/// The solution/vcxproj shape rules that made Visual Studio (2026's strict loader in
/// particular) refuse a 379-project engine solution: namespaced module names leaking into
/// solution entries, no source-mirroring folders, dropped external include paths, invisible
/// NMake-project sources, and uncollected .inl headers.
/// </summary>
public class VisualStudioGeneratorSolutionTests : IDisposable
{
    private readonly string _projectRoot;

    public VisualStudioGeneratorSolutionTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "OmenVsGenSln_" + Guid.NewGuid().ToString("N"));
        WriteFile("Code/Framework/AtomCore/Instance/thing.cpp", "int f() { return 0; }\n");
        WriteFile("Code/Framework/AtomCore/Instance/thing.h", "#pragma once\n");
        WriteFile("Code/Framework/AtomCore/std/containers.inl", "// inline impl\n");
        WriteFile("Shaders/Authored.slang", "[shader(\"compute\")] void Main() {}\n");
        WriteFile("Shaders/Generated/Material.slang", "// generated\n");
        WriteFile("Source/App/main.cpp", "int main() { return 0; }\n");
        WriteFile("3rdParty/Pkg/include/pkg.h", "#pragma once\n");
    }

    public void Dispose() => Directory.Delete(_projectRoot, recursive: true);

    private void WriteFile(string relative, string content)
    {
        var path = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private BuildContext CreateContext(BuildConfiguration configuration = BuildConfiguration.Development,
        TargetArchitecture architecture = TargetArchitecture.X64) => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = architecture,
        Configuration = configuration,
        ProjectRoot = _projectRoot,
        OutputDirectory = Path.Combine(_projectRoot, "bin"),
        IntermediateDirectory = Path.Combine(_projectRoot, "obj")
    };

    private Task<(string Solution, Func<string, string> Project)> GenerateAsync(params ModuleRules[] modules) =>
        GenerateAsync(CreateContext(), modules);

    private async Task<(string Solution, Func<string, string> Project)> GenerateAsync(BuildContext context, params ModuleRules[] modules)
    {
        var target = new PilotTarget(context);
        await new VisualStudioGenerator(_projectRoot, VisualStudioGenerator.VisualStudioVersion.VS2026)
            .GenerateAsync(target, modules, context);

        var solution = await File.ReadAllTextAsync(Path.Combine(_projectRoot, "Pilot.sln"));
        return (solution, moduleName =>
        {
            var sanitized = Omen.Core.Build.ModulePathName.BinaryName(moduleName);
            return File.ReadAllText(Path.Combine(
                _projectRoot, "Intermediate", "ProjectFiles", sanitized, $"{sanitized}.vcxproj"));
        });
    }

    [Fact]
    public async Task Vs2026_SlnxPreservesFoldersRulesDependenciesAndBuildSelection()
    {
        var context = CreateContext();
        var framework = new FrameworkModule(context);
        framework.PrivateDependencies.Add("Vendored");
        WriteFile("Pilot.target.cs", "// rules");
        context.RuleFilePaths = new Dictionary<string, string>
        {
            [typeof(PilotTarget).FullName!] = Path.Combine(_projectRoot, "Pilot.target.cs")
        };
        var target = new PilotTarget(context);
        await new VisualStudioGenerator(_projectRoot, VisualStudioGenerator.VisualStudioVersion.VS2026)
            .GenerateAsync(target, [new AppModule(context), framework, new VendoredThirdParty(context)], context);

        var xml = XDocument.Load(Path.Combine(_projectRoot, "Pilot.slnx"));
        var app = xml.Descendants("Project").Single(p => ((string)p.Attribute("Path")!).EndsWith("/App.vcxproj"));
        var core = xml.Descendants("Project").Single(p => ((string)p.Attribute("Path")!).EndsWith("/AtomCore.vcxproj"));
        ((string?)core.Parent!.Attribute("Name")).Should().Be("/Code/Framework/");
        core.Element("BuildDependency")!.Attribute("Project")!.Value.Should().EndWith("/Vendored.vcxproj");
        app.Elements("BuildDependency").Should().BeEmpty("Omen's launch project builds its own closure");
        app.Elements("Build").Should().OnlyContain(b => (string?)b.Attribute("Project") == "true");
        core.Elements("Build").Should().OnlyContain(b => (string?)b.Attribute("Project") == "false");
        xml.Descendants("File").Should().Contain(f => (string?)f.Attribute("Path") == "Pilot.target.cs");
        File.Exists(Path.Combine(_projectRoot, "Pilot.sln")).Should().BeTrue();
    }

    [Fact]
    public async Task Slnx_MapsGatedConfigurationsAndOverwritesReadOnlyOutput()
    {
        var debug = CreateContext(BuildConfiguration.Debug);
        var shipping = CreateContext(BuildConfiguration.Shipping, TargetArchitecture.ARM64);
        var generator = new VisualStudioGenerator(_projectRoot, VisualStudioGenerator.VisualStudioVersion.VS2026);
        (TargetRules Target, IReadOnlyList<ModuleRules> Modules, BuildContext Context)[] combinations =
        [
            (new PilotTarget(debug), [new AppModule(debug), new FrameworkModule(debug)], debug),
            (new PilotTarget(shipping), [new FrameworkModule(shipping)], shipping)
        ];
        await generator.GenerateAsync(combinations, solutionName: "A & B");
        var path = Path.Combine(_projectRoot, "A & B.slnx");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        await generator.GenerateAsync(combinations, solutionName: "A & B");
        new FileInfo(path).IsReadOnly.Should().BeFalse();
        var xml = XDocument.Load(path);
        var app = xml.Descendants("Project").Single(p => ((string)p.Attribute("Path")!).EndsWith("/App.vcxproj"));
        app.Elements("Build").Single(b => (string?)b.Attribute("Solution") == "Debug|x64")
            .Attribute("Project")!.Value.Should().Be("true");
        app.Elements("Build").Where(b => (string?)b.Attribute("Solution") != "Debug|x64")
            .Should().OnlyContain(b => (string?)b.Attribute("Project") == "false");
        app.Elements("BuildType").Single(b => (string?)b.Attribute("Solution") == "Shipping|ARM64")
            .Attribute("Project")!.Value.Should().Be("Debug");
        app.Elements("Platform").Single(b => (string?)b.Attribute("Solution") == "Shipping|ARM64")
            .Attribute("Project")!.Value.Should().Be("x64");
    }

    [Theory]
    [InlineData(VisualStudioGenerator.VisualStudioVersion.VS2019)]
    [InlineData(VisualStudioGenerator.VisualStudioVersion.VS2022)]
    public async Task OlderVisualStudio_KeepsLegacySolutionFormat(VisualStudioGenerator.VisualStudioVersion version)
    {
        var context = CreateContext();
        await new VisualStudioGenerator(_projectRoot, version)
            .GenerateAsync(new PilotTarget(context), [new FrameworkModule(context)], context);
        File.Exists(Path.Combine(_projectRoot, "Pilot.sln")).Should().BeTrue();
        File.Exists(Path.Combine(_projectRoot, "Pilot.slnx")).Should().BeFalse();
    }

    [Fact]
    public async Task NamespacedModuleName_NeverReachesTheSolutionOrProjectXml()
    {
        var (solution, project) = await GenerateAsync(new FrameworkModule(CreateContext()));

        // "AZ::AtomCore" is not a legal Visual Studio project name; the display name is the
        // project file's own base name, and RootNamespace is an identifier.
        solution.Should().NotContain("\"AZ::AtomCore\"");
        solution.Should().Contain("= \"AtomCore\"");
        project("AZ::AtomCore").Should().Contain("<RootNamespace>AtomCore</RootNamespace>");
    }

    [Fact]
    public async Task SolutionGuids_AreUppercaseBraced()
    {
        var (solution, _) = await GenerateAsync(new FrameworkModule(CreateContext()));

        System.Text.RegularExpressions.Regex.IsMatch(solution, @"\{[0-9a-f]{8}-")
            .Should().BeFalse("every GUID in a solution is written the way Visual Studio writes them: uppercase");
    }

    [Fact]
    public async Task SolutionFolders_MirrorTheSourceTree()
    {
        var (solution, _) = await GenerateAsync(new FrameworkModule(CreateContext()));

        // Code/Framework/AtomCore, with the trailing module segment dropped -> Code\Framework.
        solution.Should().Contain("= \"Code\", \"Code\"");
        solution.Should().Contain("= \"Framework\", \"Framework\"");
        // Project nests under Framework, Framework under Code.
        var start = solution.IndexOf("GlobalSection(NestedProjects)", StringComparison.Ordinal);
        var nested = solution[start..solution.IndexOf("EndGlobalSection", start, StringComparison.Ordinal)];
        nested.Split('\n').Count(l => l.Contains(" = {")).Should().Be(2);
    }

    [Fact]
    public async Task ThirdPartyModules_GroupUnderTheThirdPartyFolder()
    {
        var (solution, _) = await GenerateAsync(new FrameworkModule(CreateContext()), new VendoredThirdParty(CreateContext()));

        solution.Should().Contain("= \"ThirdParty\", \"ThirdParty\"");
    }

    [Fact]
    public async Task InlHeaders_AreListedInTheProject()
    {
        var (_, project) = await GenerateAsync(new FrameworkModule(CreateContext()));

        project("AZ::AtomCore").Should().Contain("containers.inl");
    }

    [Fact]
    public async Task ExplicitSourceFilesList_StillShowsUnlistedHeadersForBrowsing()
    {
        var (_, project) = await GenerateAsync(new FilesListModule(CreateContext()));

        // The FILES_CMAKE-style list names only the .cpp; the on-disk header still shows.
        var xml = project("FilesList");
        xml.Should().Contain("thing.cpp");
        xml.Should().Contain("thing.h");
    }

    [Fact]
    public async Task ShaderSources_AreNonCompilingProjectItemsAndHonorExplicitList()
    {
        var (_, project) = await GenerateAsync(new ShaderModule(CreateContext()));

        var xml = project("Shaders");
        xml.Should().Contain("<None Include=\"..\\..\\..\\Shaders\\Authored.slang\" />");
        xml.Should().NotContain("Generated\\Material.slang");
        xml.Should().NotContain("<ClCompile Include=\"..\\..\\..\\Shaders\\Authored.slang");
    }

    [Fact]
    public async Task ExecutableLinkModules_AreHiddenImplementationDetails()
    {
        var (solution, _) = await GenerateAsync(
            new AppModule(CreateContext()),
            new ExecutableLinkModule(CreateContext()));

        solution.Should().NotContain("ExecutableLink");
        Directory.Exists(Path.Combine(_projectRoot, "Intermediate", "ProjectFiles", "PilotExecutableLink"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task MainNMakeModule_StillListsItsSources()
    {
        // The launch module is a Makefile project (omen builds it), but a Makefile project
        // with no ClCompile items shows no sources at all in Solution Explorer.
        var context = CreateContext();
        var (_, project) = await GenerateAsync(new AppModule(context), new FrameworkModule(CreateContext()));

        var xml = project("App");
        xml.Should().Contain("NMakeBuildCommandLine", "App is the launch module");
        xml.Should().Contain("main.cpp");
    }

    [Fact]
    public async Task ThirdPartyIncludeRoots_ReachTheProjectAsExternalIncludePaths()
    {
        var context = CreateContext();
        var consumer = new FrameworkModule(context);
        consumer.PublicDependencies.Add("Pkg");

        var (_, project) = await GenerateAsync(consumer, new PrebuiltThirdParty(CreateContext()));

        var xml = project("AZ::AtomCore");
        xml.Should().Contain("<ExternalIncludePath>");
        // SourceDirectory separators pass through verbatim - compare normalized.
        xml.Replace('/', '\\').Should().Contain("3rdParty\\Pkg\\include");
    }

    [Fact]
    public async Task SolutionName_OverridesTheTargetName()
    {
        var context = CreateContext();
        await new VisualStudioGenerator(_projectRoot, VisualStudioGenerator.VisualStudioVersion.VS2026)
            .GenerateAsync(new PilotTarget(context), [new FrameworkModule(context)], context, solutionName: "NightFox");

        File.Exists(Path.Combine(_projectRoot, "NightFox.sln")).Should().BeTrue();
        File.Exists(Path.Combine(_projectRoot, "Pilot.sln")).Should().BeFalse();
    }

    [Fact]
    public async Task Solution_IsWrittenWithAUtf8Bom()
    {
        await GenerateAsync(new FrameworkModule(CreateContext()));

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_projectRoot, "Pilot.sln"));
        bytes.Take(3).Should().Equal(new UTF8Encoding(true).GetPreamble());
    }

    [Fact]
    public async Task ProjectNamedLikeASiblingFolder_NestsInsideThatFolder()
    {
        // Code/Tools/ModelDoc holds ModelDoc, ModelDoc.Static and ModelDoc.Tests. The siblings
        // create a "ModelDoc" folder under Tools, and the ModelDoc project used to land beside it.
        // Visual Studio 2026 drops a project whose name matches another item under the same
        // parent and saves the solution without it - ModelDoc and AssetBrowser, the engine's
        // two tool executables, disappeared that way.
        var context = CreateContext();
        var (solution, _) = await GenerateAsync(
            new NamedModule(context, "ModelDoc", "Code/Tools/ModelDoc"),
            new NamedModule(context, "ModelDoc.Static", "Code/Tools/ModelDoc"),
            new NamedModule(context, "ModelDoc.Tests", "Code/Tools/ModelDoc"));

        var items = SolutionItems(solution);
        items.GroupBy(i => i.Parent)
            .Should().OnlyContain(
                g => g.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == g.Count(),
                "Visual Studio drops a project whose name matches another item under the same parent");
        var modelDocFolder = items.Single(i => i.IsFolder && i.Name == "ModelDoc");
        items.Single(i => !i.IsFolder && i.Name == "ModelDoc").Parent.Should().Be(modelDocFolder.Guid);
        items.Single(i => !i.IsFolder && i.Name == "ModelDoc.Static").Parent.Should().Be(modelDocFolder.Guid);
    }

    [Fact]
    public async Task ReadOnlyGeneratedFiles_AreOverwritten()
    {
        // Something left 372 of the engine's generated .vcxproj files Read-only, and every
        // regeneration died on the first with "Access to the path is denied" - so the fixed
        // generator never replaced the solution Visual Studio had damaged.
        await GenerateAsync(new FrameworkModule(CreateContext()));
        var projectPath = Path.Combine(_projectRoot, "Intermediate", "ProjectFiles", "AtomCore", "AtomCore.vcxproj");
        var solutionPath = Path.Combine(_projectRoot, "Pilot.sln");
        File.SetAttributes(projectPath, FileAttributes.ReadOnly);
        File.SetAttributes(solutionPath, FileAttributes.ReadOnly);

        var regenerate = () => GenerateAsync(new FrameworkModule(CreateContext()));

        await regenerate.Should().NotThrowAsync();
        new FileInfo(projectPath).IsReadOnly.Should().BeFalse();
        new FileInfo(solutionPath).IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task LaunchProject_HasNoSolutionDependenciesAndNothingDependsOnIt()
    {
        // F5 on a tool must run only that tool's `omen build`, which compiles the closure
        // itself. Any solution edge from a launch project, or into another target's launch
        // project, makes Visual Studio build that other target too - F5 on ModelDoc used to
        // build NightFoxCorePilot and, through a chain linking every launch project,
        // NightFoxCoreTests as well.
        var context = CreateContext();
        var app = new AppModule(context);
        var framework = new FrameworkModule(context);
        framework.PrivateDependencies.Add("App"); // module depending on the launch module
        var (solution, project) = await GenerateAsync(app, framework);

        var items = SolutionItems(solution);
        var appGuid = items.Single(i => i.Name == "App").Guid;
        var frameworkGuid = items.Single(i => i.Name == "AtomCore").Guid;

        DependenciesOf(solution, appGuid).Should().BeEmpty("omen build App compiles the closure itself");
        DependenciesOf(solution, frameworkGuid).Should().NotContain(appGuid, "nothing may pull a launch project into its build");
        project("App").Should().NotContain("<ProjectReference", "MSBuild would build referenced projects before the Makefile project");
    }

    private static List<string> DependenciesOf(string solution, string projectGuid)
    {
        var start = solution.IndexOf($", \"{projectGuid}\"", StringComparison.Ordinal);
        var end = solution.IndexOf("EndProject", start, StringComparison.Ordinal);
        var section = solution[start..end];
        return System.Text.RegularExpressions.Regex.Matches(section, @"^\s+(\{[0-9A-F-]+\}) = \1", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public async Task IntelliSense_SeesTheCompilersForcedIncludesOptionsAndLiteralDefinitions()
    {
        // The compile carried /FI VSCompat.h, /Zc:preprocessor and /permissive- (engine-wide)
        // plus the module's /utf-8; the project files carried none of them, and a definition
        // kept its command-line escaping - so IntelliSense mis-parsed the STL itself and
        // reported thousands of errors on code that compiles clean.
        var context = CreateContext();
        context.GlobalCompilerOptions = [@"/FIC:\engine\Compat\VSCompat.h", "/Zc:preprocessor", "/permissive-", "/nologo"];
        var app = new AppModule(context);
        var framework = new FrameworkModule(context);
        framework.AdditionalCompilerFlags.Add("/utf-8");
        framework.PublicDefinitions.Add("AZ_BUILD_CONFIGURATION_TYPE=\\\"debug\\\"");
        var (_, project) = await GenerateAsync(context, app, framework);

        var library = project("AZ::AtomCore");
        library.Should().Contain(@"<ForcedIncludeFiles>C:\engine\Compat\VSCompat.h;%(ForcedIncludeFiles)</ForcedIncludeFiles>");
        library.Should().Contain("<UseStandardPreprocessor>true</UseStandardPreprocessor>");
        library.Should().MatchRegex(@"<AdditionalOptions>[^<]*/Zc:preprocessor[^<]*/permissive-[^<]*/utf-8[^<]*%\(AdditionalOptions\)</AdditionalOptions>");
        library.Should().NotContain("/nologo").And.NotContain("<AdditionalOptions>/FI");
        library.Should().Contain("AZ_BUILD_CONFIGURATION_TYPE=\"debug\"").And.NotContain("\\\"debug\\\"");

        var launch = project("App");
        launch.Should().Contain(@"<NMakeForcedIncludes>C:\engine\Compat\VSCompat.h</NMakeForcedIncludes>");
        launch.Should().MatchRegex(@"<AdditionalOptions>/std:c\+\+20 [^<]*/Zc:preprocessor[^<]*</AdditionalOptions>");
    }

    /// <summary>Every project and solution folder in a .sln with its NestedProjects parent
    /// (null at the solution root).</summary>
    private static List<(string Guid, string Name, bool IsFolder, string? Parent)> SolutionItems(string solution)
    {
        var nestedStart = solution.IndexOf("GlobalSection(NestedProjects)", StringComparison.Ordinal);
        var nested = solution[nestedStart..solution.IndexOf("EndGlobalSection", nestedStart, StringComparison.Ordinal)];
        var parents = System.Text.RegularExpressions.Regex
            .Matches(nested, @"(\{[0-9A-F-]+\}) = (\{[0-9A-F-]+\})")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        return System.Text.RegularExpressions.Regex
            .Matches(solution, @"^Project\(""\{([0-9A-F-]+)\}""\) = ""([^""]+)"", ""[^""]*"", ""(\{[0-9A-F-]+\})""",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => (
                Guid: m.Groups[3].Value,
                Name: m.Groups[2].Value,
                IsFolder: m.Groups[1].Value == "2150E333-8FDC-42A3-9474-1A3956D46DE8",
                Parent: parents.TryGetValue(m.Groups[3].Value, out var parent) ? parent : null))
            .ToList();
    }

    private sealed class NamedModule : ModuleRules
    {
        public NamedModule(BuildContext context, string name, string sourceDirectory) : base(context, explicitName: name)
        {
            SourceDirectory = sourceDirectory;
        }
    }

    private sealed class PilotTarget : TargetRules
    {
        public PilotTarget(BuildContext context) : base(context)
        {
            // Name derives from the class name: "PilotTarget" -> "Pilot".
            LaunchModuleName = "App";
        }
    }

    private sealed class AppModule : ModuleRules
    {
        public AppModule(BuildContext context) : base(context, explicitName: "App")
        {
            SourceDirectory = "Source/App";
            PrivateDependencies.Add("AZ::AtomCore");
        }
    }

    private sealed class FrameworkModule : ModuleRules
    {
        public FrameworkModule(BuildContext context) : base(context, explicitName: "AZ::AtomCore")
        {
            SourceDirectory = "Code/Framework/AtomCore";
        }
    }

    private sealed class FilesListModule : ModuleRules
    {
        public FilesListModule(BuildContext context) : base(context, explicitName: "FilesList")
        {
            SourceDirectory = "Code/Framework/AtomCore";
            SourceFiles.Add("Instance/thing.cpp");
        }
    }

    private sealed class ShaderModule : ModuleRules
    {
        public ShaderModule(BuildContext context) : base(context, explicitName: "Shaders")
        {
            SourceDirectory = "Shaders";
            SourceFiles.Add("Authored.slang");
        }
    }

    private sealed class ExecutableLinkModule : ModuleRules
    {
        public ExecutableLinkModule(BuildContext context) : base(context, explicitName: "PilotExecutableLink")
        {
            BinaryType = null;
            SourceFiles.Add("Source/App/main.cpp");
        }
    }

    private sealed class VendoredThirdParty : ModuleRules
    {
        public VendoredThirdParty(BuildContext context) : base(context, explicitName: "Vendored")
        {
            Type = ModuleType.ThirdParty;
            SourceDirectory = "3rdParty/Pkg";
        }
    }

    private sealed class PrebuiltThirdParty : ModuleRules
    {
        public PrebuiltThirdParty(BuildContext context) : base(context, explicitName: "Pkg")
        {
            Type = ModuleType.ThirdParty;
            BinaryType = TargetType.HeaderOnly;
            SourceDirectory = "3rdParty/Pkg";
            PublicIncludePaths.Add("include");
        }
    }
}
