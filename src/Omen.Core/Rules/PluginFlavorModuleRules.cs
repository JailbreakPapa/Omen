// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Wraps one PluginFlavor into the ModuleRules shape ActionGraphBuilder already understands.
/// Not user-authored — CompiledRules.CreateModuleRules synthesizes one of these per
/// flavor a PluginRules subclass defines.
/// </summary>
internal sealed class PluginFlavorModuleRules : ModuleRules
{
    public PluginFlavorModuleRules(BuildContext context, PluginRules plugin, PluginFlavor flavor, IReadOnlyList<PluginRules> allPlugins)
        : base(context, explicitName: PluginFlavorNaming.ModuleName(plugin.Name, flavor))
    {
        PluginName = plugin.Name;
        // The shared flavor type declares nothing; the plugin's *.plugin.cs is the rule file.
        RuleFilePath = context.RuleFileFor(plugin.GetType());

        Type = flavor.Kind switch
        {
            PluginFlavorKind.Editor or PluginFlavorKind.EditorApi or PluginFlavorKind.EditorPrivateObject
                or PluginFlavorKind.EditorStatic
                // RHI-backend-family kinds that are always host-tools-gated in the real trees
                // (every real instance is wrapped in `if(PAL_TRAIT_BUILD_HOST_TOOLS)`) - same
                // Editor tier as the classic/modern-layout host-tools kinds above.
                or PluginFlavorKind.Builders or PluginFlavorKind.BuildersStatic or PluginFlavorKind.Edit
                => ModuleType.Editor,
            // TestsStatic is Tests' static half - same tier as Tests, mirroring how
            // EditorPrivateObject (Editor's static half) already shares Editor's tier above.
            PluginFlavorKind.Tests or PluginFlavorKind.EditorTests or PluginFlavorKind.TestsStatic => ModuleType.Test,
            PluginFlavorKind.Custom => flavor.Role switch
            {
                PluginModuleRole.Editor => ModuleType.Editor,
                PluginModuleRole.Test => ModuleType.Test,
                _ => ModuleType.Runtime
            },
            _ => ModuleType.Runtime
        };
        SourceDirectory = flavor.SourceDirectory;
        BinaryType = flavor.BinaryType;
        IsPrivateTarget = flavor.IsPrivateTarget;

        SourceFiles.AddRange(flavor.SourceFiles);

        PublicDependencies.AddRange(flavor.PublicDependencies);
        PrivateDependencies.AddRange(flavor.PrivateDependencies);
        PublicIncludePaths.AddRange(flavor.PublicIncludePaths);
        PrivateIncludePaths.AddRange(flavor.PrivateIncludePaths);
        PublicDefinitions.AddRange(flavor.PublicDefinitions);
        PrivateDefinitions.AddRange(flavor.PrivateDefinitions);
        RuntimeDependencies.AddRange(flavor.RuntimeDependencies);
        ThirdPartyDependencies.AddRange(flavor.ThirdPartyDependencies);
        RequiredTraits.AddRange(flavor.RequiredTraits);
        PlatformIncludeFiles.AddRange(flavor.PlatformIncludeFiles);
        AdditionalLinkerFlags.AddRange(flavor.AdditionalLinkerFlags);
        UseUnityBuild = flavor.UseUnityBuild;
        UnityBuildExclusions.AddRange(flavor.UnityBuildExclusions);

        QtVersion = flavor.QtVersion;
        QtModules.AddRange(flavor.QtModules);
        EnableMoc = flavor.EnableMoc;
        EnableUic = flavor.EnableUic;
        EnableRcc = flavor.EnableRcc;

        // plugin.json "dependencies" are deliberately NOT forwarded as build dependencies.
        //
        // In O3DE they express plugin ENABLEMENT - "enabling AudioSystem also enables LmbrCentral" -
        // and are consumed by the enablement system (see PluginEnablement, which walks
        // Manifest.Dependencies itself). ly_add_target never sees them; link dependencies come
        // from BUILD_DEPENDENCIES in CMakeLists, which the converter emits into the flavors above.
        //
        // Forwarding them here manufactured link edges O3DE does not have, from a relation that
        // is not acyclic by construction. It produced a real cycle -
        //   LmbrCentral -> LmbrCentral.Static -> AudioSystem.API -> LmbrCentral
        // - because AudioSystem's manifest names LmbrCentral. 83 of 142 converted plugins declare
        // manifest dependencies, and their shape shows the edges were never linkage:
        //   Atom     -> MaterialEditor, MaterialCanvas, ShaderManagementConsole, ...
        //   Atom_RHI -> Atom_RHI_Metal, Atom_RHI_AGC, Atom_RHI_DX12, Atom_RHI_Vulkan
        // Every Atom flavor would have linked MaterialEditor, and Atom_RHI would have linked the
        // Metal and AGC backends into a Windows build.

        PrivateDefinitions.Add($"O3DE_PLUGIN_NAME={plugin.Name}");
        PrivateDefinitions.Add($"O3DE_PLUGIN_VERSION={plugin.Manifest?.Version ?? "0.0.0"}");

        AddAutoGen([.. flavor.AutoGenRules]);
    }

}
