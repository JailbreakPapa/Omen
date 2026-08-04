// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;

namespace Omen.Core.Tests;

public class CompileCommandsWriterTests : IDisposable
{
    private readonly string _outputPath;

    public CompileCommandsWriterTests()
    {
        _outputPath = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(CompileCommandsWriterTests), Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_outputPath)) File.Delete(_outputPath);
    }

    [Fact]
    public void Write_EmitsOneEntryPerCompileAction()
    {
        // Arrange
        var graph = new ActionGraph();
        graph.AddAction(new BuildAction
        {
            Id = "compile1",
            Type = ActionType.Compile,
            Description = "Compile Foo.cpp",
            CommandLine = "cl.exe /c Foo.cpp",
            WorkingDirectory = "/project",
            Inputs = [new FileItem { Path = "/project/Foo.cpp" }],
            Outputs = [new FileItem { Path = "/project/obj/Foo.obj" }]
        });
        graph.AddAction(new BuildAction
        {
            Id = "link1",
            Type = ActionType.Link,
            Description = "Link Foo",
            CommandLine = "link.exe Foo.obj",
            WorkingDirectory = "/project",
            Outputs = [new FileItem { Path = "/project/bin/Foo.exe" }]
        });

        // Act
        CompileCommandsWriter.Write(graph, _outputPath);

        // Assert
        var json = File.ReadAllText(_outputPath);
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.EnumerateArray().ToList();

        entries.Should().HaveCount(1);
        entries[0].GetProperty("file").GetString().Should().Be("/project/Foo.cpp");
        entries[0].GetProperty("command").GetString().Should().Be("cl.exe /c Foo.cpp");
        entries[0].GetProperty("directory").GetString().Should().Be("/project");
    }
}
