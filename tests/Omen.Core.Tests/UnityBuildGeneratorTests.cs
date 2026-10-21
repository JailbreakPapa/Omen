// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Graph;
using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class UnityBuildGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OmenUnityGen_" + Guid.NewGuid().ToString("N"));

    private sealed class TestModule(BuildContext context) : ModuleRules(context);

    public UnityBuildGeneratorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteSource(string dir, string name)
    {
        var full = Path.Combine(_root, dir);
        Directory.CreateDirectory(full);
        var path = Path.Combine(full, name);
        File.WriteAllText(path, "// " + name);
        return path;
    }

    [Fact]
    public void GroupByDirectory_FlushesAtEachDirectoryBoundary()
    {
        // A batch left mid-fill when one directory's files run out must not silently absorb
        // the next directory's files too, or GroupByDirectory stops doing what it says.
        var a1 = WriteSource("A", "A1.cpp");
        var b1 = WriteSource("B", "B1.cpp");

        var context = new BuildContext
        {
            Platform = TargetPlatform.Windows,
            Architecture = TargetArchitecture.X64,
            Configuration = BuildConfiguration.Development,
            ProjectRoot = _root,
            OutputDirectory = Path.Combine(_root, "bin"),
            IntermediateDirectory = Path.Combine(_root, "obj")
        };
        var module = new TestModule(context);
        var generator = new UnityBuildGenerator();

        var unityFiles = generator.GenerateUnityFiles(module, [a1, b1], Path.Combine(_root, "Intermediate"));

        unityFiles.Should().HaveCount(2);
        unityFiles.Should().ContainSingle(u => u.IncludedFiles.Contains(a1) && !u.IncludedFiles.Contains(b1));
        unityFiles.Should().ContainSingle(u => u.IncludedFiles.Contains(b1) && !u.IncludedFiles.Contains(a1));
    }
}
