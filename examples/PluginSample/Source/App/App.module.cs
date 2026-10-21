// App module - the sample executable's entry-point module. Depends on the
// Greeter plugin's Runtime flavor (a shared library) to demonstrate an
// executable consuming a plugin across a DLL boundary.

using Omen.Core.Configuration;
using Omen.Core.Rules;

public class AppModule : ModuleRules
{
    public AppModule(BuildContext context) : base(context)
    {
        Type = ModuleType.Runtime;
        SourceDirectory = "Source/App";
        // A plugin's Runtime flavor is the module named after the plugin itself (matching O3DE's
        // ly_add_target NAME ${plugin_name}) - "Greeter.Runtime" stopped resolving when that
        // naming landed.
        PrivateDependencies.Add("Greeter");

        // Resolved through the O3DE package store: the association lives in
        // cmake/3rdParty/BuiltInPackages.cmake and the package's own FindLua.cmake supplies
        // the include path and lualib.lib.
        ThirdPartyDependencies.Add("3rdParty::Lua");

        // Resolved from a vcpkg installed tree (VCPKG_ROOT, or the project's manifest-mode
        // vcpkg_installed/): includes, per-config libs, and the port's DLLs staged next to
        // the exe - all read from vcpkg's own install database.
        ThirdPartyDependencies.Add("Vcpkg::zlib");

        // Code generation - Omen's add_custom_command equivalent. The header lands on the
        // module's include path automatically; the generated .cpp compiles into the module.
        CodeGenSteps.Add(new CodeGenStep
        {
            Description = "Generate BuildInfo.h",
            Command = "echo #define GENERATED_BUILD_NAME \"PluginSample (generated at build time)\" > \"$(IntermediateDir)\\BuildInfo.h\"",
            Outputs = { "BuildInfo.h" }
        });
        CodeGenSteps.Add(new CodeGenStep
        {
            Description = "Generate BuildInfo.cpp",
            Command = "echo extern const int kGeneratedAnswer = 123; > \"$(IntermediateDir)\\BuildInfo.cpp\"",
            Outputs = { "BuildInfo.cpp" }
        });
    }
}
