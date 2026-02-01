// Engine Module - Core engine functionality

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class EngineModule : ModuleRules
{
    public EngineModule(BuildContext context) : base(context)
    {
        Type = ModuleType.Runtime;
        SourceDirectory = "Source/Engine";
        PCHUsage = PCHUsage.UseSharedPCHs;
        SharedPCHHeaderFile = "EnginePCH.h";

        // Include paths
        PublicIncludePaths.Add("Public");
        PrivateIncludePaths.Add("Private");

        // Dependencies
        PublicDependencies.Add("CoreModule");

        // Definitions
        PublicDefinitions.Add("ENGINE_API=__declspec(dllexport)");

        // Compiler settings
        EnableRTTI = false;
        EnableExceptions = true;
        CppStandard = CppStandard.Cpp20;
    }
}
