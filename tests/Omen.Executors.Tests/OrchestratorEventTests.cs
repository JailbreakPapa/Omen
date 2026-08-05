// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

public class OrchestratorEventTests
{
    [Fact]
    public void Constructor_SetsMessageAndLevel()
    {
        var evt = new OrchestratorEvent("Build started", OrchestratorEventLevel.Info);

        evt.Message.Should().Be("Build started");
        evt.Level.Should().Be(OrchestratorEventLevel.Info);
    }

    [Theory]
    [InlineData(OrchestratorEventLevel.Info)]
    [InlineData(OrchestratorEventLevel.Warning)]
    [InlineData(OrchestratorEventLevel.Error)]
    [InlineData(OrchestratorEventLevel.Success)]
    public void AllLevels_AreConstructible(OrchestratorEventLevel level)
    {
        var evt = new OrchestratorEvent("message", level);
        evt.Level.Should().Be(level);
    }
}
