// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Build;

/// <summary>
/// Turns a module name into something usable as a file or directory name.
///
/// Module names are namespaced - "AZ::AzCore", "Plugin::Camera", "3rdParty::Lua" - and a colon is
/// illegal in a Windows path. Using the raw name as an intermediate directory produced
/// "Intermediate/Windows_Debug/AZ::AzCore/...", and every process spawned against such a path
/// failed with "The filename, directory name, or volume label syntax is incorrect".
///
/// That failure mode was invisible for two reasons now fixed alongside this: the executor
/// swallowed the exception and left the action Executing forever, and a stalled graph reported
/// success. The symptom was a build of 877 actions that compiled nothing, produced no artifacts,
/// and exited 0.
/// </summary>
public static class ModulePathName
{
    /// <summary>
    /// The file name a module's own binary is written under: the module name with its namespace
    /// qualifier dropped, so "NF::NightFoxCore" produces NightFoxCore.dll, not NF__NightFoxCore.dll.
    ///
    /// The qualifier identifies the module to other rules, exactly as O3DE's `ly_add_target(NAME
    /// AzCore ... NAMESPACE AZ)` does - AZ:: is how CMake refers to the target, and the artifact on
    /// disk has always been AzCore.dll. Anything that loads a module by name at runtime looks for
    /// that name, so the qualifier does not belong in the artifact, its import library, or its PDB.
    ///
    /// Intermediate directories keep <see cref="Sanitize"/>: nobody reads them, and there the
    /// qualifier is what keeps two same-leaf modules from sharing a directory.
    /// </summary>
    public static string BinaryName(string moduleName)
    {
        if (string.IsNullOrEmpty(moduleName))
        {
            return moduleName;
        }

        var separator = moduleName.LastIndexOf("::", StringComparison.Ordinal);
        return Sanitize(separator < 0 ? moduleName : moduleName[(separator + 2)..]);
    }

    /// <summary>
    /// <paramref name="moduleName"/> with every character illegal in a file name replaced by an
    /// underscore, so "AZ::AzCore" becomes "AZ__AzCore".
    ///
    /// Substitution rather than removal keeps distinct names distinct: dropping the colons would
    /// map "AZ::AzCore" and "AZAzCore" onto one directory.
    /// </summary>
    public static string Sanitize(string moduleName)
    {
        if (string.IsNullOrEmpty(moduleName))
        {
            return moduleName;
        }

        var invalid = Path.GetInvalidFileNameChars();
        if (moduleName.IndexOfAny(invalid) < 0)
        {
            return moduleName;
        }

        return string.Create(moduleName.Length, moduleName, (span, name) =>
        {
            for (var i = 0; i < name.Length; i++)
            {
                span[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
            }
        });
    }
}
