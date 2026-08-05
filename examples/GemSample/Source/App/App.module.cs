// App module - the sample executable's entry-point module. Depends on the
// Greeter gem's Runtime flavor (a shared library) to demonstrate an
// executable consuming a gem across a DLL boundary.

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class AppModule : ModuleRules
{
    public AppModule(BuildContext context) : base(context)
    {
        Type = ModuleType.Runtime;
        SourceDirectory = "Source/App";
        PrivateDependencies.Add("Greeter.Runtime");
    }
}
