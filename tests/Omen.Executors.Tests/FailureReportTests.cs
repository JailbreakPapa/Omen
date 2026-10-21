// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;
using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public class FailureReportTests
{
    private static ActionResult Failed(string stdout, string stderr = "") => new()
    {
        Action = new BuildAction
        {
            Id = "c", Type = ActionType.Compile, Description = "Compile Foo.cpp", WorkingDirectory = "F:/src",
            CommandLine = "cl.exe /c Foo.cpp", Outputs = [new FileItem { Path = "F:/obj/Foo.obj" }],
        },
        Success = false,
        Duration = TimeSpan.Zero,
        ExitCode = 2,
        StandardOutput = stdout,
        StandardError = stderr,
    };

    [Fact]
    public void CompilerDiagnostics_StartTheirLine_SoVisualStudioListsThem()
    {
        // Visual Studio's Error List takes only lines of the form `file(line,col): error CODE: text`; a prefix
        // in front of the first diagnostic hid it.
        const string error = @"F:\src\Foo.cpp(12,5): error C2039: 'Bar': is not a member of 'Baz'";

        var lines = BuildOrchestrator.DescribeFailure(Failed(error + "\r\n", "LINK : fatal error LNK1104: cannot open file 'x.lib'"))
            .SelectMany(e => e.Message.Split('\n'))
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        lines.Should().Contain(error);
        lines.Should().Contain("LINK : fatal error LNK1104: cannot open file 'x.lib'");
    }

    [Fact]
    public void Diagnostics_ComeBeforeTheCommandLine()
    {
        var events = BuildOrchestrator.DescribeFailure(Failed("F:/src/Foo.cpp(1): error C1083: nope")).ToList();

        events.FindIndex(e => e.Message.Contains("C1083")).Should().BeLessThan(events.FindIndex(e => e.Message.Contains("cl.exe")));
    }
}
