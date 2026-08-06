// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

/// <summary>
/// Tests for ActionGraph dependency management and topological sorting.
/// </summary>
public class ActionGraphTests
{
    [Fact]
    public void AddAction_IncreasesCount()
    {
        // Arrange
        var graph = new ActionGraph();
        var action = CreateAction("action1");

        // Act
        graph.AddAction(action);

        // Assert
        graph.Count.Should().Be(1);
    }

    [Fact]
    public void AddAction_WithDuplicateId_ThrowsException()
    {
        // Arrange
        var graph = new ActionGraph();
        var action1 = CreateAction("action1");
        var action2 = CreateAction("action1");
        graph.AddAction(action1);

        // Act & Assert
        var act = () => graph.AddAction(action2);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public void GetAction_ReturnsCorrectAction()
    {
        // Arrange
        var graph = new ActionGraph();
        var action = CreateAction("test-action");
        graph.AddAction(action);

        // Act
        var result = graph.GetAction("test-action");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("test-action");
    }

    [Fact]
    public void GetAction_WithNonexistentId_ReturnsNull()
    {
        // Arrange
        var graph = new ActionGraph();

        // Act
        var result = graph.GetAction("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void AddDependency_CreatesDependencyRelationship()
    {
        // Arrange
        var graph = new ActionGraph();
        var action1 = CreateAction("action1");
        var action2 = CreateAction("action2");
        graph.AddAction(action1);
        graph.AddAction(action2);

        // Act
        graph.AddDependency("action2", "action1");

        // Assert
        action2.Dependencies.Should().Contain(action1);
        action1.Dependents.Should().Contain(action2);
    }

    [Fact]
    public void AddDependency_WithNonexistentDependent_ThrowsException()
    {
        // Arrange
        var graph = new ActionGraph();
        var action = CreateAction("action1");
        graph.AddAction(action);

        // Act & Assert
        var act = () => graph.AddDependency("nonexistent", "action1");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public void GetTopologicalOrder_ReturnsDependenciesFirst()
    {
        // Arrange
        var graph = new ActionGraph();
        var action1 = CreateAction("compile1");
        var action2 = CreateAction("compile2");
        var action3 = CreateAction("link");
        
        graph.AddAction(action1);
        graph.AddAction(action2);
        graph.AddAction(action3);
        graph.AddDependency("link", "compile1");
        graph.AddDependency("link", "compile2");

        // Act
        var order = graph.GetTopologicalOrder();

        // Assert
        order.Should().HaveCount(3);
        var linkIndex = order.ToList().IndexOf(action3);
        var compile1Index = order.ToList().IndexOf(action1);
        var compile2Index = order.ToList().IndexOf(action2);
        
        linkIndex.Should().BeGreaterThan(compile1Index);
        linkIndex.Should().BeGreaterThan(compile2Index);
    }

    [Fact]
    public void GetTopologicalOrder_DetectsCircularDependency()
    {
        // Arrange
        var graph = new ActionGraph();
        var action1 = CreateAction("action1");
        var action2 = CreateAction("action2");
        
        graph.AddAction(action1);
        graph.AddAction(action2);
        
        // Create circular dependency manually
        action1.Dependencies.Add(action2);
        action2.Dependencies.Add(action1);

        // Act & Assert
        var act = () => graph.GetTopologicalOrder();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Circular dependency*");
    }

    [Fact]
    public void GetReadyActions_ReturnsActionsWithCompletedDependencies()
    {
        // Arrange
        var graph = new ActionGraph();
        var action1 = CreateAction("action1");
        var action2 = CreateAction("action2");
        
        graph.AddAction(action1);
        graph.AddAction(action2);
        graph.AddDependency("action2", "action1");

        // Act - before completing action1
        var readyBefore = graph.GetReadyActions();

        // Assert
        readyBefore.Should().HaveCount(1);
        readyBefore.Should().Contain(action1);

        // Complete action1
        action1.Status = ActionStatus.Completed;

        // Act - after completing action1
        var readyAfter = graph.GetReadyActions();

        // Assert
        readyAfter.Should().HaveCount(1);
        readyAfter.Should().Contain(action2);
    }

    [Fact]
    public void IsComplete_ReturnsTrueWhenAllActionsComplete()
    {
        // Arrange
        var graph = new ActionGraph();
        var action1 = CreateAction("action1");
        var action2 = CreateAction("action2");
        
        graph.AddAction(action1);
        graph.AddAction(action2);

        // Act & Assert - initially not complete
        graph.IsComplete.Should().BeFalse();

        // Complete all actions
        action1.Status = ActionStatus.Completed;
        action2.Status = ActionStatus.Completed;

        // Now should be complete
        graph.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void HasFailures_ReturnsTrueWhenAnyActionFailed()
    {
        // Arrange
        var graph = new ActionGraph();
        var action1 = CreateAction("action1");
        var action2 = CreateAction("action2");
        
        graph.AddAction(action1);
        graph.AddAction(action2);

        // Act & Assert - initially no failures
        graph.HasFailures.Should().BeFalse();

        // Mark one as failed
        action1.Status = ActionStatus.Failed;

        // Now should have failures
        graph.HasFailures.Should().BeTrue();
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        var graph = new ActionGraph();
        graph.AddAction(CreateAction("compile1", ActionType.Compile));
        graph.AddAction(CreateAction("compile2", ActionType.Compile));
        graph.AddAction(CreateAction("compile3", ActionType.Compile));
        graph.AddAction(CreateAction("link1", ActionType.Link));
        graph.AddAction(CreateAction("archive1", ActionType.Archive));

        // Act
        var stats = graph.GetStatistics();

        // Assert
        stats.TotalActions.Should().Be(5);
        stats.CompileActions.Should().Be(3);
        stats.LinkActions.Should().Be(1);
        stats.ArchiveActions.Should().Be(1);
    }

    [Fact]
    public void Reset_SetsAllStatusesToPending()
    {
        // Arrange
        var graph = new ActionGraph();
        var action1 = CreateAction("action1");
        var action2 = CreateAction("action2");
        
        graph.AddAction(action1);
        graph.AddAction(action2);

        action1.Status = ActionStatus.Completed;
        action2.Status = ActionStatus.Failed;

        // Act
        graph.Reset();

        // Assert
        action1.Status.Should().Be(ActionStatus.Pending);
        action2.Status.Should().Be(ActionStatus.Pending);
    }

    [Fact]
    public void GetCriticalPath_ReturnsLongestChain()
    {
        // Arrange
        var graph = new ActionGraph();
        
        // Path 1: A -> B -> C (3 actions)
        var actionA = CreateAction("A", estimatedDuration: TimeSpan.FromSeconds(10));
        var actionB = CreateAction("B", estimatedDuration: TimeSpan.FromSeconds(10));
        var actionC = CreateAction("C", estimatedDuration: TimeSpan.FromSeconds(10));
        
        // Path 2: X -> Y (2 actions)
        var actionX = CreateAction("X", estimatedDuration: TimeSpan.FromSeconds(5));
        var actionY = CreateAction("Y", estimatedDuration: TimeSpan.FromSeconds(5));

        graph.AddAction(actionA);
        graph.AddAction(actionB);
        graph.AddAction(actionC);
        graph.AddAction(actionX);
        graph.AddAction(actionY);

        graph.AddDependency("B", "A");
        graph.AddDependency("C", "B");
        graph.AddDependency("Y", "X");

        // Act
        var criticalPath = graph.GetCriticalPath();

        // Assert
        criticalPath.Should().HaveCountGreaterOrEqualTo(3);
    }

    [Fact]
    public void GetCriticalPath_WithEmptyGraph_ReturnsEmpty()
    {
        // Regression test: OrderByDescending().First() on an empty graph used to throw
        // InvalidOperationException, which crashed ComputePriorities() and GetStatistics()
        // for a target with zero actions (e.g. zero real modules).
        var graph = new ActionGraph();

        graph.GetCriticalPath().Should().BeEmpty();

        var computePriorities = () => graph.ComputePriorities();
        computePriorities.Should().NotThrow();

        var getStatistics = () => graph.GetStatistics();
        getStatistics.Should().NotThrow();
    }

    [Fact]
    public void ComputePriorities_AssignsLowerPriorityToCriticalPath()
    {
        // Arrange
        var graph = new ActionGraph();
        var actionA = CreateAction("A", estimatedDuration: TimeSpan.FromSeconds(100));
        var actionB = CreateAction("B", estimatedDuration: TimeSpan.FromSeconds(1));
        
        graph.AddAction(actionA);
        graph.AddAction(actionB);

        // Act
        graph.ComputePriorities();

        // Assert - all actions should have priorities assigned
        actionA.Priority.Should().BeLessThanOrEqualTo(1000);
        actionB.Priority.Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public void IsUpToDate_WithDigest_ReturnsFalseWhenCommandLineChanged()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionGraphTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var storePath = Path.Combine(tempDir, "digests.json");
        var outputPath = Path.Combine(tempDir, "out.obj");
        File.WriteAllText(outputPath, "stale object file");

        var calculator = new Sha256DigestCalculator();
        var store = new ActionDigestStore(storePath);

        var originalAction = CreateAction("compile1");
        var originalDigest = originalAction.ComputeDigest(calculator);
        store.Set(outputPath, originalDigest);

        var changedAction = new BuildAction
        {
            Id = "compile1",
            Type = ActionType.Compile,
            Description = "Test action compile1",
            CommandLine = "test.exe /DIFFERENT_FLAG",
            WorkingDirectory = "/test",
            Outputs = [new FileItem { Path = outputPath }]
        };
        var graph = new ActionGraph();
        graph.AddAction(changedAction);

        // Act
        var upToDate = graph.IsUpToDate(changedAction, calculator, store);

        // Assert
        upToDate.Should().BeFalse();

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void IsUpToDate_WithDigest_ReturnsTrueWhenCommandLineUnchanged()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionGraphTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var storePath = Path.Combine(tempDir, "digests.json");
        var outputPath = Path.Combine(tempDir, "out.obj");
        File.WriteAllText(outputPath, "up to date object file");

        var calculator = new Sha256DigestCalculator();
        var store = new ActionDigestStore(storePath);

        var action = new BuildAction
        {
            Id = "compile1",
            Type = ActionType.Compile,
            Description = "Test action compile1",
            CommandLine = "test.exe /SAME_FLAG",
            WorkingDirectory = "/test",
            Outputs = [new FileItem { Path = outputPath }]
        };
        var digest = action.ComputeDigest(calculator);
        store.Set(outputPath, digest);

        var graph = new ActionGraph();
        graph.AddAction(action);

        // Act
        var upToDate = graph.IsUpToDate(action, calculator, store);

        // Assert
        upToDate.Should().BeTrue();

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void IsUpToDate_WithDigest_ReturnsFalseWhenSourceEditedButCommandLineUnchanged()
    {
        // Regression test: a digest match alone must NOT be enough to skip an action.
        // BuildAction.ComputeDigest hashes Type|CommandLine|Inputs(Path:Digest)|Environment,
        // and FileItem.Digest is never populated anywhere in the codebase, so the digest is
        // blind to a source file's content - only its path and the command line affect it.
        // Editing a .cpp's body without touching any compiler flag must still trigger a
        // rebuild, which only the timestamp check (source newer than output) can catch.

        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionGraphTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var storePath = Path.Combine(tempDir, "digests.json");
        var sourcePath = Path.Combine(tempDir, "source.cpp");
        var outputPath = Path.Combine(tempDir, "out.obj");

        File.WriteAllText(outputPath, "object file from the first build");
        File.SetLastWriteTimeUtc(outputPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        File.WriteAllText(sourcePath, "edited source body");
        File.SetLastWriteTimeUtc(sourcePath, new DateTime(2020, 1, 1, 0, 1, 0, DateTimeKind.Utc)); // newer than output

        var calculator = new Sha256DigestCalculator();
        var store = new ActionDigestStore(storePath);

        var action = new BuildAction
        {
            Id = "compile1",
            Type = ActionType.Compile,
            Description = "Test action compile1",
            CommandLine = "test.exe /SAME_FLAG", // unchanged since the recorded digest
            WorkingDirectory = "/test",
            Inputs = [new FileItem { Path = sourcePath }],
            Outputs = [new FileItem { Path = outputPath }]
        };

        // Record the digest as if this action had already been built successfully once.
        // Digest depends only on Type/CommandLine/Input paths, so it is identical
        // before and after the source edit above.
        var digest = action.ComputeDigest(calculator);
        store.Set(outputPath, digest);

        var graph = new ActionGraph();
        graph.AddAction(action);

        // Act
        var upToDate = graph.IsUpToDate(action, calculator, store);

        // Assert - the digest matches, but the source is newer than the output, so this
        // must still be rebuilt.
        upToDate.Should().BeFalse();

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void MarkUpToDateActionsAsSkipped_DoesNotSkipActionWhoseDependencyWillRebuild()
    {
        // Regression test: a link action's own on-disk timestamps can look up to date
        // (its .obj input predates the previously-built .exe) even though its compile
        // dependency is about to produce a fresher .obj later in this same build. The
        // upfront skip pass must not trust stale timestamps for an action sitting behind
        // a Pending (not-yet-skipped) dependency.

        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ActionGraphTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "Game.cpp");
        var objPath = Path.Combine(tempDir, "Game.obj");
        var exePath = Path.Combine(tempDir, "Game.exe");

        File.WriteAllText(objPath, "object file from the first build");
        File.SetLastWriteTimeUtc(objPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        File.WriteAllText(exePath, "exe file from the first build");
        File.SetLastWriteTimeUtc(exePath, new DateTime(2020, 1, 1, 0, 1, 0, DateTimeKind.Utc)); // newer than obj

        File.WriteAllText(sourcePath, "edited source body");
        File.SetLastWriteTimeUtc(sourcePath, new DateTime(2020, 1, 1, 0, 2, 0, DateTimeKind.Utc)); // newer than obj, triggers recompile

        var compileAction = new BuildAction
        {
            Id = "compile-game",
            Type = ActionType.Compile,
            Description = "Compile Game.cpp",
            CommandLine = "cl.exe Game.cpp",
            WorkingDirectory = tempDir,
            Inputs = [new FileItem { Path = sourcePath }],
            Outputs = [new FileItem { Path = objPath }]
        };

        var linkAction = new BuildAction
        {
            Id = "link-game",
            Type = ActionType.Link,
            Description = "Link Game.exe",
            CommandLine = "link.exe Game.obj",
            WorkingDirectory = tempDir,
            Inputs = [new FileItem { Path = objPath }], // same FileItem list a link action's aggregate inputs would reference
            Outputs = [new FileItem { Path = exePath }]
        };

        var graph = new ActionGraph();
        graph.AddAction(compileAction);
        graph.AddAction(linkAction);
        graph.AddDependency("link-game", "compile-game");

        // Act
        graph.MarkUpToDateActionsAsSkipped();

        // Assert - the compile is correctly not skipped (source is newer than its obj),
        // and the link must not be skipped either, since it depends on a fresh obj that
        // doesn't exist yet.
        compileAction.Status.Should().Be(ActionStatus.Pending);
        linkAction.Status.Should().Be(ActionStatus.Pending);

        Directory.Delete(tempDir, recursive: true);
    }

    private static BuildAction CreateAction(
        string id, 
        ActionType type = ActionType.Compile,
        TimeSpan? estimatedDuration = null) => new()
    {
        Id = id,
        Type = type,
        Description = $"Test action {id}",
        CommandLine = "test.exe",
        WorkingDirectory = "/test",
        EstimatedDuration = estimatedDuration ?? TimeSpan.FromSeconds(5)
    };
}
