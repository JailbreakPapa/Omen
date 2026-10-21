using Omen.Core.Configuration;
using Omen.Core.Rules;

public sealed class WorkflowSampleTarget : TargetRules
{
    public WorkflowSampleTarget(BuildContext context) : base(context)
    {
        Type = TargetType.Executable;
        LaunchModuleName = "WorkflowSampleModule";
        UseUnityBuild = false;
        Tests.Add(new TestDefinition { Name = "sample.self", Command = "$(TargetFile)", Labels = ["smoke"] });
        Tests.Add(new TestDefinition { Name = "sample.arguments", Command = "$(TargetFile)", Arguments = ["--argument", "value with spaces"], Labels = ["smoke"] });
        Tests.Add(new TestDefinition
        {
            Name = "sample.environment", Command = "$(TargetFile)", Arguments = ["--environment"],
            Environment = new() { ["OMEN_SAMPLE_MESSAGE"] = "hello from test" }, Labels = ["smoke"]
        });
        Tests.Add(new TestDefinition { Name = "sample.expected-failure", Command = "$(TargetFile)", Arguments = ["--fail"], ExpectedExitCode = 7, Labels = ["negative"] });
        Tests.Add(new TestDefinition { Name = "sample.skipped", Command = "$(TargetFile)", Arguments = ["--skip"], SkipExitCode = 77 });
        Tests.Add(new TestDefinition { Name = "sample.disabled", Command = "unused-disabled-command", Disabled = true });
        InstallRules.Add(new InstallRule { Source = "$(TargetFile)", Destination = "bin" });
    }
}
