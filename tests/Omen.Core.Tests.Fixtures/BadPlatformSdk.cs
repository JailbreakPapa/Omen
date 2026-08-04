// Omen Build System - Test Fixtures
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Interfaces;

namespace Omen.Core.Tests.Fixtures;

/// <summary>
/// A test fixture implementing IPlatformSDK with no parameterless constructor.
/// This class is designed to test error handling in PlatformFactory.DiscoverExternalSdks:
/// when the factory tries to instantiate this type via Activator.CreateInstance(),
/// it will throw MissingMethodException (no parameterless constructor), which should
/// be caught and the type silently skipped.
/// </summary>
public class BadPlatformSdk : IPlatformSDK
{
    // No parameterless constructor - only this one requiring a parameter
    public BadPlatformSdk(string requiredParameter)
    {
        _ = requiredParameter;
    }

    public TargetPlatform Platform => TargetPlatform.Unknown;
    public string Name => "Bad Platform SDK";
    public bool IsAvailable => false;
    public IReadOnlyList<TargetArchitecture> SupportedArchitectures => [];

    public SDKInfo? Detect() => null;

    public IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo) =>
        throw new NotImplementedException();
}
