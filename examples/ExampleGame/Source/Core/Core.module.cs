// Core Module - Foundational types and utilities

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class CoreModule : ModuleRules
{
    public CoreModule(BuildContext context) : base(context)
    {
        Type = ModuleType.Runtime;
        SourceDirectory = "Source/Core";
        PCHUsage = PCHUsage.UseExplicitOrShared;
        SharedPCHHeaderFile = "CorePCH.h";

        // Include paths
        PublicIncludePaths.Add("Public");
        PrivateIncludePaths.Add("Private");

        // Definitions
        PublicDefinitions.Add("CORE_API=__declspec(dllexport)");

        // Platform-specific settings
        ConfigureForPlatform(TargetPlatform.Windows, () =>
        {
            PublicDefinitions.Add("PLATFORM_WINDOWS=1");
            PublicDefinitions.Add("_CRT_SECURE_NO_WARNINGS");
            PublicSystemLibraries.Add("kernel32.lib");
            PublicSystemLibraries.Add("user32.lib");
        });

        ConfigureForPlatform(TargetPlatform.Linux, () =>
        {
            PublicDefinitions.Add("PLATFORM_LINUX=1");
            PublicSystemLibraries.Add("pthread");
        });

        // Compiler settings
        EnableRTTI = false;
        EnableExceptions = true;
        WarningLevel = WarningLevel.Level4;
        TreatWarningsAsErrors = true;

        CppStandard = CppStandard.Cpp20;
    }
}
