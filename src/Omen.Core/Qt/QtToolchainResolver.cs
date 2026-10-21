// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.Core.Qt;

/// <summary>
/// Locates moc, uic and rcc for a Qt module.
///
/// Before this, Qt paths were hardcoded to C:\Qt\... in VisualStudioGenerator, which is not where
/// O3DE keeps Qt - it lives in the 3rdParty package store, and is deliberately excluded from
/// ThirdPartyPackageResolver because UseQt is its own path. So the store is checked explicitly.
/// </summary>
public sealed class QtToolchainResolver
{
    private readonly string _packageStoreRoot;

    /// <param name="packageStoreRoot">
    /// The O3DE 3rdParty package store to search. Defaults to <see cref="PackageStore.Root"/>;
    /// injectable so the not-found path is testable on a machine that does have Qt installed.
    /// </param>
    public QtToolchainResolver(string? packageStoreRoot = null)
    {
        _packageStoreRoot = packageStoreRoot ?? PackageStore.Root;
    }

    /// <summary>Platform suffix for the tool executables.</summary>
    public static string ExecutableSuffix => OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    /// <summary>
    /// Resolve the toolchain <paramref name="module"/> needs, or null when it is not a Qt module.
    /// </summary>
    /// <exception cref="QtToolchainNotFoundException">
    /// The module uses Qt but no candidate contained all three tools.
    /// </exception>
    public QtToolchain? Resolve(ModuleRules module)
    {
        if (module.QtVersion == QtVersion.None)
        {
            return null;
        }

        var tried = new List<string>();

        foreach (var candidate in Candidates(module))
        {
            tried.Add(candidate);

            if (TryFromRoot(candidate) is { } toolchain)
            {
                return toolchain;
            }
        }

        throw new QtToolchainNotFoundException(module.Name, tried);
    }

    /// <summary>
    /// Candidate Qt roots, in priority order. An explicit QtPath wins; then the O3DE package
    /// store, which is where this engine actually keeps Qt; then the environment conventions;
    /// then the paths that used to be hardcoded, kept last so no existing setup regresses.
    /// </summary>
    private IEnumerable<string> Candidates(ModuleRules module)
    {
        if (!string.IsNullOrWhiteSpace(module.QtPath))
        {
            yield return module.QtPath!;
        }

        foreach (var candidate in ImplicitCandidates(module.QtVersion, _packageStoreRoot))
        {
            yield return candidate;
        }
    }

    /// <summary>
    /// The candidates that do not come from an explicit QtPath: the O3DE package store, the
    /// environment conventions, then the paths that used to be hardcoded - kept last so no
    /// existing setup regresses.
    /// </summary>
    private static IEnumerable<string> ImplicitCandidates(QtVersion version, string packageStoreRoot)
    {
        if (FindInPackageStore(packageStoreRoot, version) is { } fromStore)
        {
            yield return fromStore;
        }

        foreach (var variable in EnvironmentVariablesFor(version))
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                yield return value;
            }
        }

        yield return HardcodedRootFor(version);
    }

    private static string HardcodedRootFor(QtVersion version) => version switch
    {
        QtVersion.Qt5 => @"C:\Qt\5.15.2\msvc2019_64",
        _ => @"C:\Qt\6.8.3\msvc2022_64"
    };

    /// <summary>
    /// The best Qt root for <paramref name="version"/> without an explicit QtPath, never throwing.
    ///
    /// Project generation should still emit a file on a machine with no Qt, as it always has;
    /// the build path (<see cref="Resolve"/>) is the one that fails loudly. Both walk the same
    /// candidates, so the .vcxproj and the build now agree on which Qt is in play - which they
    /// did not when this was hardcoded.
    /// </summary>
    public static string DefaultRootFor(QtVersion version)
    {
        foreach (var candidate in ImplicitCandidates(version, PackageStore.Root))
        {
            if (TryFromRoot(candidate) is not null)
            {
                return candidate;
            }
        }

        return HardcodedRootFor(version);
    }

    private static IEnumerable<string> EnvironmentVariablesFor(QtVersion version)
    {
        yield return "QTDIR";
        yield return version == QtVersion.Qt5 ? "Qt5_DIR" : "Qt6_DIR";
    }

    /// <summary>
    /// Find a Qt root inside an O3DE 3rdParty package store.
    ///
    /// The store holds versioned package directories with the Qt root nested one level down -
    /// <c>&lt;store&gt;/qt-5.15.2-rev7-windows/qt/bin/moc.exe</c> - so both the package
    /// directory and its <c>qt</c> subdirectory are considered.
    ///
    /// Pure and root-parameterized rather than reading LY_3RDPARTY_PATH itself, so it is
    /// testable without mutating the environment.
    /// </summary>
    public static string? FindInPackageStore(string storeRoot, QtVersion version)
    {
        if (!Directory.Exists(storeRoot))
        {
            return null;
        }

        var majorPrefix = version == QtVersion.Qt5 ? "qt-5." : "qt-6.";

        var packages = Directory.EnumerateDirectories(storeRoot, "qt-*")
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Prefer the requested major; fall back to any Qt package rather than failing outright,
        // since a mismatched major still beats the hardcoded C:\Qt guess.
        var ordered = packages
            .Where(d => Path.GetFileName(d).StartsWith(majorPrefix, StringComparison.OrdinalIgnoreCase))
            .Concat(packages.Where(d => !Path.GetFileName(d).StartsWith(majorPrefix, StringComparison.OrdinalIgnoreCase)));

        foreach (var package in ordered)
        {
            var nested = Path.Combine(package, "qt");
            if (TryFromRoot(nested) is not null)
            {
                return nested;
            }

            if (TryFromRoot(package) is not null)
            {
                return package;
            }
        }

        return null;
    }

    /// <summary>
    /// Which Qt major this root is, decided by which core import library it ships. Probing the
    /// filesystem rather than parsing the path keeps this right for an explicit QtPath or a
    /// QTDIR that carries no version in its name. Defaults to 5, matching the package store.
    /// </summary>
    private static string DetectMajorVersion(string qtRoot)
    {
        var lib = Path.Combine(qtRoot, "lib");

        foreach (var major in new[] { "6", "5" })
        {
            if (File.Exists(Path.Combine(lib, $"Qt{major}Core.lib")) ||
                File.Exists(Path.Combine(lib, $"libQt{major}Core.a")) ||
                File.Exists(Path.Combine(lib, $"libQt{major}Core.so")))
            {
                return major;
            }
        }

        return "5";
    }

    /// <summary>
    /// Accept <paramref name="qtRoot"/> only if all three tools are present under its bin/.
    /// A root with moc but no rcc is not a usable toolchain, and finding that out at rcc time
    /// is worse than not accepting it here.
    /// </summary>
    public static QtToolchain? TryFromRoot(string qtRoot)
    {
        if (string.IsNullOrWhiteSpace(qtRoot))
        {
            return null;
        }

        var bin = Path.Combine(qtRoot, "bin");
        var moc = Path.Combine(bin, "moc" + ExecutableSuffix);
        var uic = Path.Combine(bin, "uic" + ExecutableSuffix);
        var rcc = Path.Combine(bin, "rcc" + ExecutableSuffix);

        if (!File.Exists(moc) || !File.Exists(uic) || !File.Exists(rcc))
        {
            return null;
        }

        return new QtToolchain(qtRoot, moc, uic, rcc, DetectMajorVersion(qtRoot));
    }
}
