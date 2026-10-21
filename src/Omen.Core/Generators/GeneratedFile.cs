// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text;

namespace Omen.Core.Generators;

/// <summary>
/// Writes generator output over whatever is already on disk. A generated file is the
/// generator's to overwrite, but source-control syncs and other tools leave them Read-only:
/// 372 of the engine's generated .vcxproj files were, and every regeneration died on the first
/// with "Access to the path is denied" - leaving in place a stale solution Visual Studio had
/// already damaged. Sharpmake and UBT clear the flag on their generated files the same way.
/// </summary>
public static class GeneratedFile
{
    public static Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        MakeWritable(path);
        return File.WriteAllTextAsync(path, contents, ct);
    }

    public static Task WriteAllTextAsync(string path, string contents, Encoding encoding, CancellationToken ct = default)
    {
        MakeWritable(path);
        return File.WriteAllTextAsync(path, contents, encoding, ct);
    }

    public static void WriteAllText(string path, string contents)
    {
        MakeWritable(path);
        File.WriteAllText(path, contents);
    }

    private static void MakeWritable(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists && file.IsReadOnly)
            file.IsReadOnly = false;
    }
}
