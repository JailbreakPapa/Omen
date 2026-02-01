// Renderer Module - Graphics and rendering

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class RendererModule : ModuleRules
{
    public RendererModule(BuildContext context) : base(context)
    {
        Type = ModuleType.Runtime;
        SourceDirectory = "Source/Renderer";
        PCHUsage = PCHUsage.UseSharedPCHs;
        SharedPCHHeaderFile = "RendererPCH.h";

        // Include paths
        PublicIncludePaths.Add("Public");
        PrivateIncludePaths.Add("Private");

        // Dependencies
        PublicDependencies.Add("CoreModule");
        PublicDependencies.Add("EngineModule");

        // Definitions
        PublicDefinitions.Add("RENDERER_API=__declspec(dllexport)");

        // Platform-specific graphics APIs
        ConfigureForPlatform(TargetPlatform.Windows, () =>
        {
            PublicDefinitions.Add("WITH_D3D12=1");
            PublicSystemLibraries.Add("d3d12.lib");
            PublicSystemLibraries.Add("dxgi.lib");
        });

        ConfigureForPlatform(TargetPlatform.Linux, () =>
        {
            PublicDefinitions.Add("WITH_VULKAN=1");
            PublicSystemLibraries.Add("vulkan");
        });

        // Compiler settings
        EnableRTTI = false;
        EnableExceptions = true;
        CppStandard = CppStandard.Cpp20;
    }
}
