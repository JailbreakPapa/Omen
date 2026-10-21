// Greeter Plugin - sample plugin for the Omen build system.
// Demonstrates a plugin with a Static flavor (core logic) and a Runtime flavor
// (shared library, privately depending on Static, exporting a C API).

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class GreeterPlugin : PluginRules
{
    public GreeterPlugin(BuildContext context) : base(context)
    {
        LoadManifest("Plugins/Greeter");

        var staticFlavor = DefineFlavor(PluginFlavorKind.Static);
        staticFlavor.SourceDirectory = "Plugins/Greeter/Code/Source/Static";

        var runtimeFlavor = DefineFlavor(PluginFlavorKind.Runtime);
        runtimeFlavor.SourceDirectory = "Plugins/Greeter/Code/Source/Runtime";
        runtimeFlavor.BinaryType = TargetType.SharedLibrary;
        runtimeFlavor.PrivateDependencies.Add($"{Name}.Static");
        runtimeFlavor.PrivateDefinitions.Add("GREETER_RUNTIME_EXPORTS");

        CreateAlias("Clients", PluginFlavorKind.Runtime);
    }
}

