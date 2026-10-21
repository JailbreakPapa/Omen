// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.IO;
using System.Xml.Linq;
using Omen.Core.Configuration;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Tests;

public sealed class WorkflowViewModelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OmenGuiWorkflowTests", Guid.NewGuid().ToString("N"));
    public WorkflowViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Source"));
        File.WriteAllText(Path.Combine(root, "Sample.omenproject.cs"), "using Omen.Core.Rules; public class SampleProject : OmenProjectRules { public SampleProject() { SourceRoots.Add(\"Source\"); } }");
    }
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task FailedRegisteredTest_ProducesFailureStateAndJunitAndClearsBusyState()
    {
        File.WriteAllText(Path.Combine(root, "Source", "Sample.target.cs"), """
            using Omen.Core.Configuration; using Omen.Core.Rules;
            public class SampleTarget : TargetRules {
                public SampleTarget(BuildContext c) : base(c) {
                    Tests.Add(new TestDefinition { Name = "failure", Command = "cmd.exe", Arguments = ["/d", "/c", "exit /b 4"] });
                }
            }
            """);
        var model = new MainWindowViewModel(restoreLastProject: false)
        {
            ProjectPath = root, SelectedPlatform = TargetPlatform.Windows, SelectedConfiguration = BuildConfiguration.Development
        };
        await model.RunTestsAsync();
        model.CurrentBuildState.Should().Be(MainWindowViewModel.BuildState.Failed);
        model.StatusText.Should().Be("Tests failed");
        model.IsBuilding.Should().BeFalse(); model.IsCancellable.Should().BeFalse();
        XDocument.Load(Path.Combine(root, "Intermediate", "TestResults", "results.xml")).Descendants("failure").Should().ContainSingle();
    }

    [Fact]
    public async Task InstallAndPackage_ExecuteTheSameDeclaredRulesFromTheGui()
    {
        File.WriteAllText(Path.Combine(root, "input.txt"), "GUI install content");
        File.WriteAllText(Path.Combine(root, "Source", "Sample.target.cs"), """
            using Omen.Core.Configuration; using Omen.Core.Rules;
            public class SampleTarget : TargetRules {
                public SampleTarget(BuildContext c) : base(c) {
                    InstallRules.Add(new InstallRule { Source = "input.txt", Destination = "share" });
                }
            }
            """);
        var model = new MainWindowViewModel(restoreLastProject: false)
        {
            ProjectPath = root, SelectedPlatform = TargetPlatform.Windows, SelectedConfiguration = BuildConfiguration.Development
        };
        await model.InstallProjectAsync(Path.Combine(root, "installed"), package: false);
        model.CurrentBuildState.Should().Be(MainWindowViewModel.BuildState.Success);
        File.ReadAllText(Path.Combine(root, "installed", "share", "input.txt")).Should().Be("GUI install content");
        await model.InstallProjectAsync(Path.Combine(root, "package.zip"), package: true);
        model.CurrentBuildState.Should().Be(MainWindowViewModel.BuildState.Success);
        using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(root, "package.zip"));
        archive.Entries.Should().Contain(e => e.FullName == "share/input.txt");
        model.IsBuilding.Should().BeFalse(); model.IsCancellable.Should().BeFalse();
    }
}
