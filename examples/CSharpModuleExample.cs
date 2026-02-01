// Example C# Module for Omen Build System
// This demonstrates how to define a C# project module

using Omen.Core.Configuration;
using Omen.Core.Rules;

/// <summary>
/// Example C# module definition.
/// </summary>
public class ToolsModule : ModuleRules
{
    public ToolsModule(BuildContext context) : base(context)
    {
        // Set this as a C# project
        Language = ModuleLanguage.CSharp;
        
        // Configure C# settings
        CSharpVersion = CSharpVersion.CSharp12;
        TargetFramework = DotNetFramework.Net80;
        EnableNullable = true;
        ImplicitUsings = true;
        
        // NuGet packages
        PackageReferences.AddRange([
            "Newtonsoft.Json/13.0.3",
            "Serilog/3.1.1",
            "System.CommandLine/2.0.0-beta4.22272.1"
        ]);
        
        // Module dependencies
        PublicDependencies.Add("CoreLibModule");
        
        // Custom source directory
        SourceDirectory = "Source/Tools";
    }
}
