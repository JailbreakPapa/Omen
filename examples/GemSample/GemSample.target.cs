// GemSample Target - a minimal, real, working sample that demonstrates the
// Omen Gem model: an executable consuming a gem's Runtime (shared library)
// flavor, which itself privately depends on the gem's Static flavor.

using Omen.Core.Configuration;
using Omen.Core.Options;
using Omen.Core.Rules;

public class GemSampleTarget : TargetRules
{
    public static TargetMatrix SupportedTargets => TargetMatrix.Empty
        .For(TargetPlatform.Windows)
        .For(TargetArchitecture.X64)
        .For(BuildConfiguration.Debug, BuildConfiguration.Development);

    public GemSampleTarget(BuildContext context) : base(context)
    {
        Type = TargetType.Executable;
        LaunchModuleName = "AppModule";

        ExtraModules.Add("AppModule");

        UsePCHFiles = false;
        UseUnityBuild = false;

        var verboseGreeting = BuildOptions.Declare(context, "VERBOSE_GREETING", "Print an extended greeting from the Greeter gem", false);
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
