using System.IO;
using Omen.Core.Configuration;
using Omen.Core.Rules;

public sealed class WorkflowSampleModule : ModuleRules
{
    public WorkflowSampleModule(BuildContext context) : base(context)
    {
        SourceDirectory = "Source/Program";
        CppStandard = CppStandard.Cpp20;
        EnableReflectCodeGen = false;
        PrivateIncludePaths.Add(Path.Combine(context.IntermediateDirectory, "Generated"));
    }
}
