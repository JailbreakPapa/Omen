// Example Qt Module for Omen Build System
// This demonstrates how to define a Qt C++ project module

using Omen.Core.Configuration;
using Omen.Core.Rules;

/// <summary>
/// Example Qt module definition for a Qt Widgets application.
/// </summary>
public class EditorUIModule : ModuleRules
{
    public EditorUIModule(BuildContext context) : base(context)
    {
        // Standard C++ settings
        Type = ModuleType.Editor;
        CppStandard = CppStandard.Cpp20;
        
        // Enable Qt support using the helper method
        UseQt(QtVersion.Qt6, "Core", "Widgets", "Gui", "OpenGL");
        
        // Or configure Qt manually:
        // QtVersion = QtVersion.Qt6;
        // QtModules.AddRange(["Core", "Widgets", "Gui", "OpenGL"]);
        
        // Optional: Custom Qt path (if not using QTDIR environment variable)
        // QtPath = "C:\\Qt\\6.8.3\\msvc2022_64";
        
        // Qt tools
        EnableMoc = true;   // Meta-Object Compiler
        EnableUic = true;   // User Interface Compiler  
        EnableRcc = true;   // Resource Compiler
        
        // Module dependencies
        PublicDependencies.AddRange([
            "CoreModule",
            "RenderModule"
        ]);
        
        // Platform-specific configuration
        ConfigureForPlatform(TargetPlatform.Windows, () =>
        {
            PublicDefinitions.Add("WIN32_LEAN_AND_MEAN");
            PublicSystemLibraries.Add("opengl32.lib");
        });
        
        SourceDirectory = "Source/EditorUI";
    }
}
