// PluginSample Target - a minimal, real, working sample that demonstrates the
// Omen Plugin model: an executable consuming a plugin's Runtime (shared library)
// flavor, which itself privately depends on the plugin's Static flavor.

using Omen.Core.Configuration;
using Omen.Core.Options;
using Omen.Core.Rules;

public class PluginSampleTarget : TargetRules
{
    public static TargetMatrix SupportedTargets => TargetMatrix.Empty
        .For(TargetPlatform.Windows)
        .For(TargetArchitecture.X64)
        .For(BuildConfiguration.Debug, BuildConfiguration.Development);

    public PluginSampleTarget(BuildContext context) : base(context)
    {
        Type = TargetType.Executable;
        LaunchModuleName = "AppModule";

        ExtraModules.Add("AppModule");

        UsePCHFiles = false;
        UseUnityBuild = false;

        var verboseGreeting = BuildOptions.Declare(context, "VERBOSE_GREETING", "Print an extended greeting from the Greeter plugin", false);
        var loadWindow = BuildOptions.Declare(context, "LOAD_WINDOW", "Load the window module", true);
        if (verboseGreeting)
        {
            GlobalDefinitions.Add("VERBOSE_GREETING=1");
        }
        if(loadWindow)
        {
            GlobalDefinitions.Add("LOAD_WINDOW=1");
        }
    }
}

