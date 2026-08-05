// Greeter Gem - sample gem for the Omen build system.
// Demonstrates a gem with a Static flavor (core logic) and a Runtime flavor
// (shared library, privately depending on Static, exporting a C API).

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class GreeterGem : GemRules
{
    public GreeterGem(BuildContext context) : base(context)
    {
        LoadManifest("Gems/Greeter");

        var staticFlavor = DefineFlavor(GemFlavorKind.Static);
        staticFlavor.SourceDirectory = "Gems/Greeter/Code/Source/Static";

        var runtimeFlavor = DefineFlavor(GemFlavorKind.Runtime);
        runtimeFlavor.SourceDirectory = "Gems/Greeter/Code/Source/Runtime";
        runtimeFlavor.BinaryType = TargetType.SharedLibrary;
        runtimeFlavor.PrivateDependencies.Add($"{Name}.Static");
        runtimeFlavor.PrivateDefinitions.Add("GREETER_RUNTIME_EXPORTS");

        CreateAlias("Clients", GemFlavorKind.Runtime);
    }
}
