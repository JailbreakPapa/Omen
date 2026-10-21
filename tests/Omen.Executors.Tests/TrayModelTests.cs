// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Executors.Server;

namespace Omen.Executors.Tests;

public class TrayModelTests
{
    private static ServerStatusDto Server(string config, params BuildStatusDto[] builds) =>
        new("1.0.0+abc", @"F:\engine", config, 14, builds.Length, true, 1500, [.. builds]);

    private static BuildStatusDto Build(string target, int completed, int total, bool background = false) =>
        new("r1", $@"F:\engine\Code\{target}.target.cs", background, DateTime.UtcNow, completed, total, "Compile A.cpp", "10 to run: 10 input-newer");

    [Fact]
    public void NoServers_IsIdle()
    {
        var model = TrayModel.From([], lastFailed: false);
        model.State.Should().Be(TrayState.Idle);
        model.Tooltip.Should().Be("Omen: no build server running");
    }

    [Fact]
    public void ServersWithoutBuilds_AreIdle()
    {
        var model = TrayModel.From([Server("Development")], lastFailed: false);
        model.State.Should().Be(TrayState.Idle);
        model.Tooltip.Should().Be("Omen: 1 server, idle");
    }

    [Fact]
    public void RunningBuilds_AreBuilding_AndCounted()
    {
        var model = TrayModel.From([Server("Development", Build("Editor", 10, 100), Build("Maestro", 1, 4, background: true)), Server("Debug")], lastFailed: false);
        model.State.Should().Be(TrayState.Building);
        model.Tooltip.Should().Be("Omen: 2 servers, 2 builds running");
    }

    [Fact]
    public void LastFailure_ShowsUntilCleared_UnlessBuilding()
    {
        TrayModel.From([Server("Development")], lastFailed: true).State.Should().Be(TrayState.Failed);
        TrayModel.From([Server("Development", Build("Editor", 1, 2))], lastFailed: true).State.Should().Be(TrayState.Building);
    }

    [Fact]
    public void Rows_NameTheServerAndEachBuild()
    {
        var model = TrayModel.From([Server("Development", Build("Editor", 25, 100, background: true))], lastFailed: false);

        model.Rows.Should().HaveCount(2);
        model.Rows[0].Text.Should().Be(@"F:\engine (Development) - 1 building, 14 slots, rules warm, idle exit in 25 min");
        model.Rows[1].Text.Should().Be("  Editor [background] 25/100 - Compile A.cpp");
        model.Rows[1].Progress.Should().Be(25);
        model.Rows[1].BuildId.Should().Be("r1");
    }

    [Fact]
    public void FailedSince_SeesOnlyFailuresAfterTheWindowWasLastOpened()
    {
        var seen = DateTime.UtcNow;
        var old = Server("Development") with { LastFinished = new LastBuildDto("Editor.target.cs", false, seen.AddMinutes(-1)) };
        var fresh = Server("Debug") with { LastFinished = new LastBuildDto("Editor.target.cs", false, seen.AddMinutes(1)) };

        TrayModel.FailedSince([old], seen).Should().BeFalse();
        TrayModel.FailedSince([old, fresh], seen).Should().BeTrue();
    }

    [Fact]
    public void TargetName_DropsTheRuleFileSuffix() =>
        TrayModel.TargetName(@"F:\engine\Code\Tools\AzTestRunner\HoudiniEngineTests.target.cs").Should().Be("HoudiniEngineTests");
}
