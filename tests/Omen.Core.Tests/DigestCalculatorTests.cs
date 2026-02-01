// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Implementations;

namespace Omen.Core.Tests;

/// <summary>
/// Tests for Sha256DigestCalculator.
/// </summary>
public class DigestCalculatorTests
{
    [Fact]
    public void ComputeDigest_ReturnsNonEmptyHash()
    {
        // Arrange
        var calculator = new Sha256DigestCalculator();
        var content = "Hello, World!";

        // Act
        var digest = calculator.ComputeDigest(content);

        // Assert
        digest.Should().NotBeNull();
        digest.Hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ComputeDigest_IsDeterministic()
    {
        // Arrange
        var calculator = new Sha256DigestCalculator();
        var content = "Same content";

        // Act
        var digest1 = calculator.ComputeDigest(content);
        var digest2 = calculator.ComputeDigest(content);

        // Assert
        digest1.Hash.Should().Be(digest2.Hash);
    }

    [Fact]
    public void ComputeDigest_DifferentContent_DifferentHash()
    {
        // Arrange
        var calculator = new Sha256DigestCalculator();

        // Act
        var digest1 = calculator.ComputeDigest("Content A");
        var digest2 = calculator.ComputeDigest("Content B");

        // Assert
        digest1.Hash.Should().NotBe(digest2.Hash);
    }

    [Fact]
    public void ComputeDigest_EmptyContent_ReturnsValidHash()
    {
        // Arrange
        var calculator = new Sha256DigestCalculator();

        // Act
        var digest = calculator.ComputeDigest("");

        // Assert
        digest.Should().NotBeNull();
        digest.Hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ComputeDigest_LargeContent_ReturnsValidHash()
    {
        // Arrange
        var calculator = new Sha256DigestCalculator();
        var largeContent = new string('x', 1_000_000);

        // Act
        var digest = calculator.ComputeDigest(largeContent);

        // Assert
        digest.Should().NotBeNull();
        digest.Hash.Should().HaveLength(64); // SHA256 hex is 64 chars
    }

    [Fact]
    public void ComputeDigest_SpecialCharacters_ReturnsValidHash()
    {
        // Arrange
        var calculator = new Sha256DigestCalculator();
        var content = "Special: αβγδ ∀∃∄ 日本語 🎉";

        // Act
        var digest = calculator.ComputeDigest(content);

        // Assert
        digest.Should().NotBeNull();
        digest.Hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ComputeFileDigestAsync_NonexistentFile_ThrowsException()
    {
        // Arrange
        var calculator = new Sha256DigestCalculator();

        // Act & Assert - could throw FileNotFoundException or DirectoryNotFoundException
        await Assert.ThrowsAnyAsync<IOException>(() =>
            calculator.ComputeFileDigestAsync("/nonexistent/file.txt"));
    }

    [Fact]
    public async Task ComputeFileDigestAsync_ExistingFile_ReturnsValidHash()
    {
        // Arrange
        var calculator = new Sha256DigestCalculator();
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "Test file content");

        try
        {
            // Act
            var digest = await calculator.ComputeFileDigestAsync(tempFile);

            // Assert
            digest.Should().NotBeNull();
            digest.Hash.Should().NotBeNullOrEmpty();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ComputeFileDigestAsync_SameContent_SameHash()
    {
        // Arrange
        var calculator = new Sha256DigestCalculator();
        var tempFile1 = Path.GetTempFileName();
        var tempFile2 = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile1, "Identical content");
        await File.WriteAllTextAsync(tempFile2, "Identical content");

        try
        {
            // Act
            var digest1 = await calculator.ComputeFileDigestAsync(tempFile1);
            var digest2 = await calculator.ComputeFileDigestAsync(tempFile2);

            // Assert
            digest1.Hash.Should().Be(digest2.Hash);
        }
        finally
        {
            File.Delete(tempFile1);
            File.Delete(tempFile2);
        }
    }
}
