// Game Module - Game-specific code

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class GameModule : ModuleRules
{
    public GameModule(BuildContext context) : base(context)
    {
        Type = ModuleType.Runtime;
        SourceDirectory = "Source/Game";
        PCHUsage = PCHUsage.UseSharedPCHs;
        SharedPCHHeaderFile = "GamePCH.h";

        // Include paths
        PublicIncludePaths.Add("Public");
        PrivateIncludePaths.Add("Private");

        // Dependencies - Game depends on all other modules
        PublicDependencies.Add("CoreModule");
        PublicDependencies.Add("EngineModule");
        PublicDependencies.Add("RendererModule");

        // Definitions
        PublicDefinitions.Add("GAME_API=__declspec(dllexport)");

        // Compiler settings
        EnableRTTI = false;
        EnableExceptions = true;
        CppStandard = CppStandard.Cpp20;
    }
}
