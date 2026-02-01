// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Implementations;

namespace Omen.Core.Tests;

/// <summary>
/// Tests for BuildAction and related types.
/// </summary>
public class BuildActionTests
{
    [Fact]
    public void BuildAction_HasCorrectProperties()
    {
        // Arrange & Act
        var action = new BuildAction
        {
            Id = "compile-main",
            Type = ActionType.Compile,
            Description = "Compile main.cpp",
            CommandLine = "cl.exe /c main.cpp",
            WorkingDirectory = "/project/src"
        };

        // Assert
        action.Id.Should().Be("compile-main");
        action.Type.Should().Be(ActionType.Compile);
        action.Description.Should().Be("Compile main.cpp");
        action.CommandLine.Should().Contain("cl.exe");
    }

    [Fact]
    public void BuildAction_DefaultStatus_IsPending()
    {
        // Arrange & Act
        var action = CreateTestAction();

        // Assert
        action.Status.Should().Be(ActionStatus.Pending);
    }

    [Fact]
    public void BuildAction_CanExecuteRemotely_DefaultsToTrue()
    {
        // Arrange & Act
        var action = CreateTestAction();

        // Assert
        action.CanExecuteRemotely.Should().BeTrue();
    }

    [Fact]
    public void BuildAction_Dependencies_StartsEmpty()
    {
        // Arrange & Act
        var action = CreateTestAction();

        // Assert
        action.Dependencies.Should().BeEmpty();
        action.Dependents.Should().BeEmpty();
    }

    [Fact]
    public void BuildAction_Environment_StartsEmpty()
    {
        // Arrange & Act
        var action = CreateTestAction();

        // Assert
        action.Environment.Should().BeEmpty();
    }

    [Fact]
    public void BuildAction_ComputeDigest_ReturnsDigest()
    {
        // Arrange
        var action = new BuildAction
        {
            Id = "test",
            Type = ActionType.Compile,
            Description = "Test",
            CommandLine = "test.exe arg1 arg2",
            WorkingDirectory = "/test",
            Inputs = new List<FileItem>
            {
                new() { Path = "/test/input.cpp", Digest = new ContentDigest("abc123", 100) }
            }
        };
        var calculator = new Sha256DigestCalculator();

        // Act
        var digest = action.ComputeDigest(calculator);

        // Assert
        digest.Should().NotBeNull();
        digest.Hash.Should().NotBeNullOrEmpty();
        action.ActionDigest.Should().Be(digest);
    }

    [Fact]
    public void BuildAction_ComputeDigest_IsDeterministic()
    {
        // Arrange
        var action1 = CreateActionWithInputs();
        var action2 = CreateActionWithInputs();
        var calculator = new Sha256DigestCalculator();

        // Act
        var digest1 = action1.ComputeDigest(calculator);
        var digest2 = action2.ComputeDigest(calculator);

        // Assert
        digest1.Hash.Should().Be(digest2.Hash);
    }

    [Fact]
    public void BuildAction_ComputeDigest_ChangesWithCommandLine()
    {
        // Arrange
        var action1 = CreateActionWithInputs("test.exe arg1");
        var action2 = CreateActionWithInputs("test.exe arg2");
        var calculator = new Sha256DigestCalculator();

        // Act
        var digest1 = action1.ComputeDigest(calculator);
        var digest2 = action2.ComputeDigest(calculator);

        // Assert
        digest1.Hash.Should().NotBe(digest2.Hash);
    }

    [Fact]
    public void BuildAction_ToString_ReturnsFormattedString()
    {
        // Arrange
        var action = new BuildAction
        {
            Id = "test",
            Type = ActionType.Link,
            Description = "Link executable",
            CommandLine = "link.exe",
            WorkingDirectory = "/test"
        };

        // Act
        var result = action.ToString();

        // Assert
        result.Should().Contain("[Link]");
        result.Should().Contain("Link executable");
    }

    [Fact]
    public void FileItem_Exists_ReturnsFalseForNonexistentFile()
    {
        // Arrange
        var item = new FileItem { Path = "/nonexistent/file.cpp" };

        // Act & Assert
        item.Exists.Should().BeFalse();
    }

    [Fact]
    public void ActionResult_HasCorrectProperties()
    {
        // Arrange & Act
        var action = CreateTestAction();
        var result = new ActionResult
        {
            Action = action,
            Success = true,
            Duration = TimeSpan.FromSeconds(5),
            StandardOutput = "Build succeeded",
            StandardError = "",
            ExitCode = 0,
            WasCached = false,
            WasRemote = true,
            RemoteAgentId = "agent-001"
        };

        // Assert
        result.Success.Should().BeTrue();
        result.Duration.TotalSeconds.Should().Be(5);
        result.ExitCode.Should().Be(0);
        result.WasRemote.Should().BeTrue();
        result.RemoteAgentId.Should().Be("agent-001");
    }

    private static BuildAction CreateTestAction() => new()
    {
        Id = "test",
        Type = ActionType.Compile,
        Description = "Test action",
        CommandLine = "test.exe",
        WorkingDirectory = "/test"
    };

    private static BuildAction CreateActionWithInputs(string commandLine = "test.exe arg1") => new()
    {
        Id = "test",
        Type = ActionType.Compile,
        Description = "Test action",
        CommandLine = commandLine,
        WorkingDirectory = "/test",
        Inputs = new List<FileItem>
        {
            new() { Path = "/test/input.cpp", Digest = new ContentDigest("abc123", 100) }
        }
    };
}
