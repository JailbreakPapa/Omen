// Example Game Target for Omen Build System
// This demonstrates how to define a target using pure C#

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class ExampleGameTarget : TargetRules
{
    public ExampleGameTarget(BuildContext context) : base(context)
    {
        Type = TargetType.Executable;

        // Supported platforms (all are supported by default, but you can filter)
        SupportedPlatforms.Clear();
        SupportedPlatforms.Add(TargetPlatform.Windows);
        SupportedPlatforms.Add(TargetPlatform.Linux);

        // Build optimizations
        UsePCHFiles = true;
        UseUnityBuild = true;

        // Enable LTO for shipping builds
        ConfigureForConfiguration(BuildConfiguration.Shipping, () =>
        {
            EnableLTO = true;
        });

        // Modules to build
        ExtraModules.Add("Core");
        ExtraModules.Add("Engine");
        ExtraModules.Add("Renderer");
        ExtraModules.Add("Game");

        // Pre-build steps
        PreBuildSteps.Add(new BuildStep
        {
            Description = "Pre-build message",
            Command = "echo Building ExampleGame..."
        });

        // Post-build steps
        PostBuildSteps.Add(new BuildStep
        {
            Description = "Post-build message",
            Command = "echo Build complete!"
        });
    }
}
