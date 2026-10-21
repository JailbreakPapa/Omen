// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Build;

/// <summary>
/// The include paths and preprocessor definitions one module compiles with, resolved once.
///
/// Everything that needs to know how a translation unit is compiled takes this rather than
/// deriving its own: the action graph, the Visual Studio generator, the emitted CMakeLists, and
/// - the reason this type exists - moc, which preprocesses and therefore needs exactly the set
/// the compiler receives. A drifted include set does not fail loudly; it generates subtly wrong
/// code.
/// </summary>
public sealed record CompileEnvironment(
    IReadOnlyList<string> IncludePaths,
    IReadOnlyList<string> Definitions,
    IReadOnlyList<string> ExternalIncludePaths)
{
    public CompileEnvironment(IReadOnlyList<string> includePaths, IReadOnlyList<string> definitions)
        : this(includePaths, definitions, []) { }
}
