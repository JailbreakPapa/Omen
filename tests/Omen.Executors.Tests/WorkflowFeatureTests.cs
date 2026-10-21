// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.IO.Compression;
using System.Xml.Linq;
using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public sealed class WorkflowFeatureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OmenWorkflowTests", Guid.NewGuid().ToString("N"));
    public WorkflowFeatureTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    private static TestDefinition Exit(string name, int code = 0, int expected = 0, int? skip = null, List<string>? labels = null) => new()
    {
        Name = name, Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
        Arguments = OperatingSystem.IsWindows() ? ["/d", "/c", $"exit /b {code}"] : ["-c", $"exit {code}"],
        ExpectedExitCode = expected, SkipExitCode = skip, Labels = labels ?? []
    };

    [Fact]
    public async Task ExplicitShellScript_PreservesQuotedPathsWithSpaces()
    {
        string output = Path.Combine(root, "file with spaces.txt");
        var result = await WorkflowProcess.RunAsync(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? ["/d", "/c", $"echo expected>\"{output}\""] : ["-c", $"echo expected > '{output}'"], root);
        result.ExitCode.Should().Be(0, result.Output + result.Error);
        File.ReadAllText(output).Trim().Should().Be("expected");
    }

    [Fact]
    public async Task Runner_ReportsPassFailSkipAndProducesValidJunit()
    {
        var tests = new List<TestDefinition> { Exit("pass"), Exit("expected", 7, 7), Exit("failure", 4), Exit("skip", 99, skip: 99),
            new() { Name = "disabled", Command = "does-not-exist", Disabled = true } };
        string report = Path.Combine(root, "results.xml");
        var result = await new TestOrchestrator().RunAsync(new() { ProjectRoot = root, Tests = tests, Jobs = 2, JunitPath = report });
        result.Success.Should().BeFalse();
        result.Results.Count(r => r.Status == "Passed").Should().Be(2);
        result.Results.Count(r => r.Status == "Failed").Should().Be(1);
        result.Results.Count(r => r.Status == "Skipped").Should().Be(2);
        var xml = XDocument.Load(report);
        xml.Descendants("testcase").Should().HaveCount(5);
        xml.Descendants("failure").Should().ContainSingle();
        xml.Descendants("skipped").Should().HaveCount(2);
    }

    [Fact]
    public async Task Filter_AddsPrerequisitesAndFailureSkipsDependentProcess()
    {
        var tests = new List<TestDefinition> { Exit("setup", 3), new() { Name = "test", Command = "must-not-start", DependsOn = ["setup"], Labels = ["fast"] }, Exit("unrelated") };
        var result = await new TestOrchestrator().RunAsync(new() { ProjectRoot = root, Tests = tests, NamePattern = "^test$", LabelPattern = "fast" });
        result.Results.Select(r => r.Name).Should().Equal("setup", "test");
        result.Results.Single(r => r.Name == "test").Status.Should().Be("Skipped");
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ListOnlyAndNoTests_DoNotStartProcesses()
    {
        var tests = new[] { new TestDefinition { Name = "listed", Command = "does-not-exist" } };
        var listed = await new TestOrchestrator().RunAsync(new() { ProjectRoot = root, Tests = tests, ListOnly = true });
        listed.Success.Should().BeTrue();
        listed.Results.Single().Status.Should().Be("Listed");
        (await new TestOrchestrator().RunAsync(new() { ProjectRoot = root, Tests = tests, NamePattern = "absent" })).Success.Should().BeFalse();
        (await new TestOrchestrator().RunAsync(new() { ProjectRoot = root, Tests = tests, NamePattern = "absent", AllowNoTests = true })).Success.Should().BeTrue();
    }

    [Fact]
    public async Task DependencyCycle_RejectsSuiteBeforeRunningAnything()
    {
        var tests = new[] { new TestDefinition { Name = "a", Command = "unused", DependsOn = ["b"] }, new TestDefinition { Name = "b", Command = "unused", DependsOn = ["a"] } };
        await FluentActions.Awaiting(() => new TestOrchestrator().RunAsync(new() { ProjectRoot = root, Tests = tests }))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*cycle*");
    }

    [Fact]
    public async Task Runner_UsesEnvironmentWorkingDirectoryAndOutputExpectations()
    {
        var test = new TestDefinition
        {
            Name = "environment", Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? ["/d", "/c", "echo %OMEN_TEST_VALUE% & cd"] : ["-c", "echo $OMEN_TEST_VALUE; pwd"],
            WorkingDirectory = root, Environment = new() { ["OMEN_TEST_VALUE"] = "value with spaces" },
            PassRegularExpression = "value with spaces", FailRegularExpression = "fatal error"
        };
        var result = await new TestOrchestrator().RunAsync(new() { ProjectRoot = root, Tests = [test] });
        result.Success.Should().BeTrue();
        result.Results.Single().Output.Should().Contain(root);
        Environment.GetEnvironmentVariable("OMEN_TEST_VALUE").Should().BeNull();
    }

    [Fact(Timeout = 30000)]
    public async Task Timeout_KillsDescendantsBeforeTheyWriteOutput()
    {
        string late = Path.Combine(root, "late.txt");
        string script = OperatingSystem.IsWindows() ? $"ping -n 5 127.0.0.1 >nul & echo late>\"{late}\"" : $"sleep 4; echo late > '{late}'";
        var result = await WorkflowProcess.RunAsync(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? ["/d", "/c", script] : ["-c", script], root, timeoutSeconds: 0.3);
        result.TimedOut.Should().BeTrue();
        await Task.Delay(4500);
        File.Exists(late).Should().BeFalse();
    }

    [Fact]
    public void Junit_FiltersInvalidXmlControlCharacters()
    {
        string path = Path.Combine(root, "escaped.xml");
        TestOrchestrator.WriteJunit(path, [new("special<&\0", "Failed", 0.01, "output\0\u001b[0m", "stderr", "reason<>&")]);
        XDocument.Load(path).Descendants("testcase").Single().Attribute("name")!.Value.Should().Be("special<&");
    }

    [Fact]
    public async Task InstallAndPackage_PreserveLayoutComponentsAndDeterministicContents()
    {
        string include = Path.Combine(root, "headers"); Directory.CreateDirectory(Path.Combine(include, "nested"));
        File.WriteAllText(Path.Combine(include, "api.h"), "header");
        File.WriteAllText(Path.Combine(include, "nested", "detail.hpp"), "nested");
        File.WriteAllText(Path.Combine(include, "ignore.cpp"), "not a header");
        File.WriteAllText(Path.Combine(root, "app.bin"), "program");
        var rules = new[]
        {
            new InstallRule { Source = "app.bin", Destination = "bin", Rename = "application", Component = "Runtime" },
            new InstallRule { Source = "headers", Destination = "include", Directory = true, Patterns = ["*.h", "*.hpp"], Component = "Development" }
        };
        var installer = new InstallOrchestrator();
        var plan = installer.Plan(root, rules);
        plan.Select(p => p.Destination).Should().Equal("bin/application", "include/api.h", "include/nested/detail.hpp");
        installer.Plan(root, rules, "Runtime").Should().ContainSingle();
        string prefix = Path.Combine(root, "installed");
        var manifest = await installer.InstallAsync(plan, prefix, "Example", "1.2.3");
        manifest.Files.Should().HaveCount(3);
        File.ReadAllText(Path.Combine(prefix, "include", "nested", "detail.hpp")).Should().Be("nested");
        manifest.Files.Should().OnlyContain(f => f.Sha256.Length == 64);
        string one = Path.Combine(root, "one.zip"), two = Path.Combine(root, "two.zip");
        await installer.PackageAsync(plan, one, "Example", "1.2.3");
        await installer.PackageAsync(plan, two, "Example", "1.2.3");
        File.ReadAllBytes(one).Should().Equal(File.ReadAllBytes(two));
        using var archive = ZipFile.OpenRead(one);
        archive.Entries.Select(e => e.FullName).Should().Contain("omen-install-manifest.json");
        archive.Entries.Should().OnlyContain(e => !e.FullName.Contains('\\'));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    public void Install_RejectsPrefixEscapeBeforeWriting(string destination)
    {
        File.WriteAllText(Path.Combine(root, "input.txt"), "input");
        FluentActions.Invoking(() => new InstallOrchestrator().Plan(root, [new() { Source = "input.txt", Destination = destination }]))
            .Should().Throw<InvalidOperationException>();
        Directory.GetDirectories(root).Should().BeEmpty();
    }

    [Fact]
    public void Install_RejectsMissingInputsConflictsAndUnknownComponents()
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        var installer = new InstallOrchestrator();
        FluentActions.Invoking(() => installer.Plan(root, [new() { Source = "missing", Destination = "bin" }])).Should().Throw<FileNotFoundException>();
        installer.Plan(root, [new() { Source = "missing", Destination = "bin", Optional = true }]).Should().BeEmpty();
        FluentActions.Invoking(() => installer.Plan(root, [new() { Source = "a.txt", Destination = "bin", Rename = "same" },
            new() { Source = "b.txt", Destination = "bin", Rename = "same" }])).Should().Throw<InvalidOperationException>().WithMessage("*Conflicting*");
        FluentActions.Invoking(() => installer.Plan(root, [new() { Source = "a.txt", Destination = "bin" }], "Unknown"))
            .Should().Throw<InvalidOperationException>().WithMessage("*no installable*");
    }

    [Fact]
    public async Task ProjectWorkflow_ExpandsArtifactPathsAndGeneratesConfiguredHeaders()
    {
        File.WriteAllText(Path.Combine(root, "Sample.omenproject.cs"), """
            using Omen.Core.Rules;
            public class SampleProject : OmenProjectRules {
                public SampleProject() { Version = "2.5"; SourceRoots.Add("Source"); DeclareOption("FEATURE", "Feature", true); }
            }
            """);
        Directory.CreateDirectory(Path.Combine(root, "Source"));
        File.WriteAllText(Path.Combine(root, "Source", "AnyFileName.target.cs"), """
            using Omen.Core.Configuration; using Omen.Core.Rules;
            public class AppTarget : TargetRules {
                public AppTarget(BuildContext c) : base(c) {
                    OutputName = "renamed";
                    Tests.Add(new TestDefinition { Name = "self", Command = "$(TargetFile)", Arguments = ["space in argument"] });
                    InstallRules.Add(new InstallRule { Source = "$(TargetFile)", Destination = "bin" });
                    ConfigureFiles.Add(new ConfiguredFile { Input = "config.h.in", Output = "$(IntermediateDir)/config.h" });
                }
            }
            """);
        File.WriteAllText(Path.Combine(root, "config.h.in"), "@ProjectVersion@ @Configuration@\n#omendefine01 FEATURE\n");
        var workflow = await ProjectWorkflow.LoadAsync(new() { ProjectRoot = root, Target = Path.Combine(root, "Source", "AnyFileName.target.cs"), Platform = TargetPlatform.Windows });
        workflow.Tests().Single().Command.Should().EndWith("renamed.exe");
        workflow.Tests().Single().Arguments.Should().Equal("space in argument");
        workflow.InstallRules().Single().Source.Should().Be(workflow.Tests().Single().Command);
        workflow.ConfigureFiles().Should().Be(1);
        File.ReadAllText(Path.Combine(root, "Intermediate", "Windows_Development", "config.h")).Should().Be("2.5 Development\n#define FEATURE 1\n");
        workflow.ConfigureFiles().Should().Be(0);
    }

    [Fact]
    public async Task Runner_RespectsParallelLimitAndSerialTests()
    {
        var tests = Enumerable.Range(0, 6).Select(i => Exit("parallel" + i)).ToList();
        tests.Add(new TestDefinition { Name = "serial", Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? ["/c", "exit /b 0"] : ["-c", "exit 0"], RunSerial = true });
        int running = 0, maximum = 0;
        bool serialOverlap = false;
        var gate = new object();
        var events = new SynchronousProgress<OrchestratorEvent>(e =>
        {
            lock (gate)
            {
                if (e.Message.StartsWith("Running "))
                {
                    if (e.Message == "Running serial" && running != 0) serialOverlap = true;
                    maximum = Math.Max(maximum, ++running);
                }
                else if (e.Message.StartsWith("Passed:") || e.Message.StartsWith("Failed:")) running--;
            }
        });
        var result = await new TestOrchestrator().RunAsync(new() { ProjectRoot = root, Tests = tests, Jobs = 2 }, events);
        result.Success.Should().BeTrue();
        maximum.Should().BeInRange(1, 2); running.Should().Be(0); serialOverlap.Should().BeFalse();
    }

    [Fact]
    public async Task Install_RejectsDestinationJunctionBeforeWritingOutsidePrefix()
    {
        string prefix = Path.Combine(root, "prefix"), outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(prefix); Directory.CreateDirectory(outside);
        string link = Path.Combine(prefix, "bin");
        if (OperatingSystem.IsWindows())
        {
            var junction = await WorkflowProcess.RunAsync("cmd.exe", ["/d", "/c", $"mklink /J \"{link}\" \"{outside}\""], root);
            junction.ExitCode.Should().Be(0, junction.Output + junction.Error);
        }
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            File.WriteAllText(Path.Combine(root, "app.txt"), "input");
            var installer = new InstallOrchestrator();
            var plan = installer.Plan(root, [new() { Source = "app.txt", Destination = "bin" }]);
            await FluentActions.Awaiting(() => installer.InstallAsync(plan, prefix, "Example", "1.0"))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*link*");
            Directory.GetFiles(outside).Should().BeEmpty();
            File.Exists(Path.Combine(prefix, InstallOrchestrator.ManifestName)).Should().BeFalse();
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task CancelledPackage_PreservesExistingArchive()
    {
        File.WriteAllText(Path.Combine(root, "input.txt"), "input");
        string output = Path.Combine(root, "package.zip");
        File.WriteAllText(output, "original archive");
        var installer = new InstallOrchestrator();
        var plan = installer.Plan(root, [new() { Source = "input.txt", Destination = "share" }]);
        await FluentActions.Awaiting(() => installer.PackageAsync(plan, output, "Example", "1.0", new CancellationToken(canceled: true)))
            .Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(output).Should().Be("original archive");
        Directory.GetFiles(root, "*.omen-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_TimeoutIsReportedAndExternalCancellationIsPropagated()
    {
        var toolchain = new Mock<IToolchain>();
        toolchain.SetupGet(t => t.ObjectFileExtension).Returns(".obj");
        toolchain.Setup(t => t.CompileAsync(It.IsAny<CompileRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (CompileRequest _, CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        var context = new BuildContext { ProjectRoot = root, Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Development, OutputDirectory = root, IntermediateDirectory = root };
        var result = await new ToolchainProbe().RunAsync(new() { Context = context, Source = "int value;", TimeoutSeconds = 0.02 }, toolchain.Object);
        result.TimedOut.Should().BeTrue(); result.Success.Should().BeFalse();
        using var cancel = new CancellationTokenSource(20);
        await FluentActions.Awaiting(() => new ToolchainProbe().RunAsync(new() { Context = context, Source = "int value;" }, toolchain.Object, cancel.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Probe_StopsAtCompileFailureAndPropagatesToolchainDiagnostics()
    {
        var toolchain = new Mock<IToolchain>();
        toolchain.SetupGet(t => t.ObjectFileExtension).Returns(".obj");
        toolchain.Setup(t => t.CompileAsync(It.IsAny<CompileRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CompileResult
            { Success = false, OutputFile = "unused", Duration = TimeSpan.Zero, ErrorOutput = "missing feature" });
        var context = new BuildContext { ProjectRoot = root, Platform = TargetPlatform.Windows, Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Development, OutputDirectory = root, IntermediateDirectory = root };
        var result = await new ToolchainProbe().RunAsync(new() { Context = context, Source = "invalid source", Link = true }, toolchain.Object);
        result.Success.Should().BeFalse(); result.Output.Should().Contain("missing feature");
        toolchain.Verify(t => t.LinkAsync(It.IsAny<LinkRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
