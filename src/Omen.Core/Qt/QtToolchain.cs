// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Qt;

/// <summary>
/// A located Qt toolchain: the three code generators, resolved to absolute paths.
/// </summary>
/// <param name="RootDirectory">The Qt root. The tools live under its <c>bin/</c>.</param>
/// <param name="MocPath">Absolute path to moc.</param>
/// <param name="UicPath">Absolute path to uic.</param>
/// <param name="RccPath">Absolute path to rcc.</param>
/// <param name="MajorVersion">
/// The major version this toolchain actually is ("5" or "6"), probed from its library
/// directory rather than taken from what the module asked for. A module declaring Qt6 can
/// resolve to a Qt5 toolchain (the resolver prefers a mismatched major over no Qt at all),
/// and library names must follow what is on disk, not the request.
/// </param>
public sealed record QtToolchain(
    string RootDirectory,
    string MocPath,
    string UicPath,
    string RccPath,
    string MajorVersion);

/// <summary>
/// Thrown when a module calls UseQt but no Qt toolchain can be found for it.
///
/// This is deliberately a hard failure. Skipping moc silently produces a link error about an
/// unresolved vtable hundreds of lines later, in a place that says nothing about Qt - so the
/// failure names the module and every path that was tried instead. Same precedent as
/// FindModuleParser, which throws naming the package rather than mistranslating quietly.
/// </summary>
public sealed class QtToolchainNotFoundException : Exception
{
    public QtToolchainNotFoundException(string moduleName, IReadOnlyList<string> pathsTried)
        : base(BuildMessage(moduleName, pathsTried))
    {
        ModuleName = moduleName;
        PathsTried = pathsTried;
    }

    public string ModuleName { get; }

    public IReadOnlyList<string> PathsTried { get; }

    private static string BuildMessage(string moduleName, IReadOnlyList<string> pathsTried)
    {
        var tried = pathsTried.Count == 0
            ? "  (no candidates - the module declares no QtPath and no Qt package or environment variable was found)"
            : string.Join(Environment.NewLine, pathsTried.Select(p => $"  {p}"));

        return $"Module '{moduleName}' uses Qt, but no Qt toolchain containing moc, uic and rcc " +
               $"was found. Paths tried, in order:{Environment.NewLine}{tried}{Environment.NewLine}" +
               "Set ModuleRules.QtPath, install the Qt package into the O3DE 3rdParty store, or set QTDIR.";
    }
}
