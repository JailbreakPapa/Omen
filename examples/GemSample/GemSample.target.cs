// GemSample Target - a minimal, real, working sample that demonstrates the
// Omen Gem model: an executable consuming a gem's Runtime (shared library)
// flavor, which itself privately depends on the gem's Static flavor.

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class GemSampleTarget : TargetRules
{
    public GemSampleTarget(BuildContext context) : base(context)
    {
        Type = TargetType.Executable;
        LaunchModuleName = "AppModule";

        ExtraModules.Add("AppModule");

        UsePCHFiles = false;
        UseUnityBuild = false;
    }
}
