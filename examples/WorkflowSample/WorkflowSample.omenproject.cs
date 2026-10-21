using Omen.Core.Rules;

public sealed class WorkflowSampleProject : OmenProjectRules
{
    public WorkflowSampleProject()
    {
        Version = "1.0.0";
        SourceRoots.Add("Source");
        DeclareOption("ENABLE_GREETING", "Enable the sample greeting", true);
        ConfigureFiles.Add(new ConfiguredFile
        {
            Input = "config.h.in", Output = "$(IntermediateDir)/Generated/config.h", NewlineStyle = ConfiguredNewlineStyle.LF
        });
        InstallRules.Add(new InstallRule { Source = "README.md", Destination = "share/WorkflowSample", Component = "Documentation" });
        InstallRules.Add(new InstallRule
        {
            Source = "$(IntermediateDir)/Generated/config.h", Destination = "include/WorkflowSample", Component = "Development"
        });
    }
}
