// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Wraps one GemFlavor into the ModuleRules shape ActionGraphBuilder already understands.
/// Not user-authored — CompiledRules.CreateModuleRules synthesizes one of these per
/// flavor a GemRules subclass defines.
/// </summary>
internal sealed class GemFlavorModuleRules : ModuleRules
{
    public GemFlavorModuleRules(BuildContext context, GemRules gem, GemFlavor flavor)
        : base(context, explicitName: $"{gem.Name}.{flavor.Kind}")
    {
        Type = flavor.Kind == GemFlavorKind.Editor ? ModuleType.Editor : ModuleType.Runtime;
        SourceDirectory = flavor.SourceDirectory;
        BinaryType = flavor.BinaryType;

        PrivateDependencies.AddRange(flavor.PrivateDependencies);
        PrivateIncludePaths.AddRange(flavor.PrivateIncludePaths);
        PrivateDefinitions.AddRange(flavor.PrivateDefinitions);

        if (gem.Manifest != null)
        {
            PublicDependencies.AddRange(gem.Manifest.Dependencies.Select(d => $"{d}.Runtime"));
        }

        PrivateDefinitions.Add($"O3DE_GEM_NAME={gem.Name}");
        PrivateDefinitions.Add($"O3DE_GEM_VERSION={gem.Manifest?.Version ?? "0.0.0"}");
    }
}
