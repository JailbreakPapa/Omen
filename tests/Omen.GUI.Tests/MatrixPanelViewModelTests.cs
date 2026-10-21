using FluentAssertions;
using Omen.Core.Configuration;
using Omen.GUI.ViewModels;
using Xunit;

namespace Omen.GUI.Tests;

public sealed class MatrixPanelViewModelTests
{
    [Fact]
    public async Task ResolveAsync_NestedTargetUsesSharedRulesFromProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmenMatrixRecovery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Source", "Shared"));
        Directory.CreateDirectory(Path.Combine(root, "Source", "App"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Sample.omenproject.cs"), """
                using Omen.Core.Rules;
                public class SampleProject : OmenProjectRules
                {
                    public SampleProject() { SourceRoots.Add("Source"); }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Source", "Shared", "Shared.module.cs"), """
                using Omen.Core.Configuration;
                public static class SharedDimensions
                {
                    public static TargetMatrix Matrix => TargetMatrix.Empty
                        .For(TargetPlatform.Windows).For(TargetArchitecture.X64)
                        .For(BuildConfiguration.Debug, BuildConfiguration.Development);
                }
                """);
            var target = Path.Combine(root, "Source", "App", "App.target.cs");
            File.WriteAllText(target, """
                using Omen.Core.Configuration;
                using Omen.Core.Rules;
                public class AppTarget : TargetRules
                {
                    public static TargetMatrix SupportedTargets => SharedDimensions.Matrix;
                    public AppTarget(BuildContext context) : base(context) { }
                }
                """);
            var panel = new MatrixPanelViewModel();
            var messages = new List<string>();
            panel.Report = (message, _) => messages.Add(message);

            await panel.ResolveAsync(target);

            messages.Should().BeEmpty();
            panel.IsActive.Should().BeTrue();
            panel.DimensionGroups.Should().ContainSingle()
                .Which.Values.Select(v => v.Value).Should().BeEquivalentTo(
                    new Enum[] { BuildConfiguration.Debug, BuildConfiguration.Development });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
