// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;

namespace Omen.Core.Interfaces;

/// <summary>
/// Interface for detecting and configuring platform SDKs.
/// </summary>
public interface IPlatformSDK
{
    /// <summary>
    /// Platform this SDK supports.
    /// </summary>
    TargetPlatform Platform { get; }
    
    /// <summary>
    /// Display name of the SDK.
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Whether this SDK is available on the current system.
    /// </summary>
    bool IsAvailable { get; }
    
    /// <summary>
    /// Detects and returns SDK information.
    /// </summary>
    SDKInfo? Detect();
    
    /// <summary>
    /// Creates a toolchain for the given architecture.
    /// </summary>
    IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo);
    
    /// <summary>
    /// Gets supported architectures for this SDK.
    /// </summary>
    IReadOnlyList<TargetArchitecture> SupportedArchitectures { get; }
}

/// <summary>
/// Information about a detected SDK.
/// </summary>
public sealed class SDKInfo
{
    public required string Version { get; init; }
    public required string InstallPath { get; init; }
    public Dictionary<string, string> Properties { get; init; } = [];
    public Dictionary<string, string> EnvironmentVariables { get; init; } = [];
    
    /// <summary>
    /// Additional paths (e.g., Windows SDK, VC tools).
    /// </summary>
    public Dictionary<string, string> AdditionalPaths { get; init; } = [];
}
