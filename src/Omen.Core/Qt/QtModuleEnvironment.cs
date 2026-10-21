// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.Core.Qt;

/// <summary>
/// Gives a Qt module the includes, definitions and libraries Qt itself needs, by writing them
/// onto the module's own declared surface.
///
/// Before this, Qt was metadata the Visual Studio generator turned into MSBuild properties and
/// nothing else - the action graph had no Qt include paths, no Qt libraries and no QT_*_LIB
/// defines at all, so `omen build` on a Qt module could not compile QObject.h let alone link.
///
/// Populating PublicIncludePaths/PublicDefinitions/PublicLibraries rather than special-casing
/// Qt inside the graph follows ThirdPartyPackageResolver's existing shape - a package IS a
/// module - so the graph, the generators, the link step and dependency propagation all need no
/// changes and cannot disagree about what Qt contributes.
/// </summary>
public static class QtModuleEnvironment
{
    /// <summary>
    /// Apply Qt's contribution to every Qt module in <paramref name="modules"/>.
    ///
    /// Safe to call once per CreateModuleRules, which constructs fresh ModuleRules instances;
    /// it is not idempotent against the same instance twice.
    /// </summary>
    public static void Apply(BuildContext context, IEnumerable<ModuleRules> modules)
    {
        var resolver = new QtToolchainResolver();

        foreach (var module in modules)
        {
            if (module.QtVersion == QtVersion.None)
            {
                continue;
            }

            var toolchain = resolver.Resolve(module);
            if (toolchain is null)
            {
                continue;
            }

            module.PublicExternalIncludePaths.AddRange(IncludePaths(module, toolchain));
            module.PublicDefinitions.AddRange(Definitions(module));
            module.PublicLibraries.AddRange(Libraries(module, toolchain, context.Configuration));
        }
    }

    /// <summary>
    /// Qt's include roots: the umbrella directory plus one per requested module, which is how
    /// Qt lays out headers (&lt;root&gt;/include and &lt;root&gt;/include/QtWidgets).
    ///
    /// Absolute, so ModuleRules' rooted-path branch carries them through unchanged.
    /// </summary>
    public static IReadOnlyList<string> IncludePaths(ModuleRules module, QtToolchain toolchain)
    {
        var include = Path.Combine(toolchain.RootDirectory, "include");

        var paths = new List<string> { include };
        paths.AddRange(module.QtModules.Select(m => Path.Combine(include, $"Qt{m}")));

        var priv = module.QtModules.SelectMany(m => PrivateIncludePaths(include, m)).ToList();
        paths.AddRange(priv);

        // Private headers reach qplatformdefs.h, which is not in include/ at all - it lives in
        // the mkspecs directory for the platform qmake would have selected. Only needed when a
        // private root was found, since nothing else pulls it in.
        if (priv.Count > 0 && MakeSpecPath(toolchain) is { } spec)
        {
            paths.Add(spec);
        }

        return paths;
    }

    /// <summary>
    /// The mkspec directory for this platform: &lt;root&gt;/mkspecs/win32-msvc and friends. qmake
    /// picks one per platform and toolchain; only the host build matters here, because a Qt
    /// package ships one platform's binaries.
    /// </summary>
    private static string? MakeSpecPath(QtToolchain toolchain)
    {
        var spec = OperatingSystem.IsWindows() ? "win32-msvc"
            : OperatingSystem.IsMacOS() ? "macx-clang"
            : "linux-g++";

        var path = Path.Combine(toolchain.RootDirectory, "mkspecs", spec);
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// The roots for Qt's private headers, which sit one version directory deeper:
    /// &lt;include&gt;/QtWidgets/5.15.1/QtWidgets/private/qstylesheetstyle_p.h, reached as
    /// &lt;QtWidgets/private/qstylesheetstyle_p.h&gt;. NightFoxQtComponents needs them - it
    /// subclasses QStyleSheetStyle - so a Qt build without these fails on C1083 in StyleHelpers.h.
    ///
    /// Discovered, never constructed. The version directory does not have to match the package:
    /// qt-5.15.2-rev7-windows ships its private headers under 5.15.1. Anything that spelled the
    /// version itself would look right and find nothing.
    /// </summary>
    private static IEnumerable<string> PrivateIncludePaths(string include, string qtModule)
    {
        var moduleRoot = Path.Combine(include, $"Qt{qtModule}");
        if (!Directory.Exists(moduleRoot))
        {
            yield break;
        }

        foreach (var versionDirectory in Directory.EnumerateDirectories(moduleRoot))
        {
            // A version directory re-nests the module name inside itself; that is what makes the
            // <QtWidgets/private/...> spelling resolve, and what tells it apart from any other
            // subdirectory Qt happens to ship.
            var nested = Path.Combine(versionDirectory, $"Qt{qtModule}");
            if (Directory.Exists(nested))
            {
                // Both roots, which is what qmake gives a private module. The version directory
                // serves <QtWidgets/private/qstylesheetstyle_p.h> as our own code writes it; the
                // one inside serves "private/qwindowsstyle_p.h", which is how Qt's private headers
                // include each other. Adding only the first gets one file further and then fails
                // inside Qt.
                yield return versionDirectory;
                yield return nested;
            }
        }
    }

    /// <summary>
    /// The QT_&lt;MODULE&gt;_LIB defines Qt headers expect. The Visual Studio generator emitted
    /// these into the .vcxproj while the compiler never received them.
    /// </summary>
    public static IReadOnlyList<string> Definitions(ModuleRules module) =>
        module.QtModules
            .Select(m => $"QT_{m.ToUpperInvariant()}_LIB")
            .Distinct()
            .ToList();

    /// <summary>
    /// Full paths to Qt's import libraries. Full paths rather than bare names plus a search
    /// directory, because ModuleRules.PublicLibraries already means "vendored .lib paths" and
    /// the link step consumes it directly - no library-search-path mechanism needed.
    ///
    /// Debug builds link the 'd'-suffixed variants Qt ships alongside the release ones
    /// (Qt5Cored.lib next to Qt5Core.lib).
    /// </summary>
    public static IReadOnlyList<string> Libraries(
        ModuleRules module, QtToolchain toolchain, BuildConfiguration configuration)
    {
        // The toolchain's own major, not module.QtVersion: a module declaring Qt6 can resolve
        // to a Qt5 toolchain, and asking for Qt6Widgets.lib in a Qt5 lib directory just fails
        // to link. The converted O3DE rules declare Qt6 against a Qt 5.15.2 package store.
        var major = toolchain.MajorVersion;
        var suffix = configuration == BuildConfiguration.Debug ? "d" : string.Empty;
        var libraryDirectory = Path.Combine(toolchain.RootDirectory, "lib");

        return module.QtModules
            .Select(m => Path.Combine(libraryDirectory, $"Qt{major}{m}{suffix}.lib"))
            .ToList();
    }
}
