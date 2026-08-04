// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Interfaces;

namespace Omen.Platforms.Console;

/// <summary>
/// Registers the Xbox platform slot. Toolchain implementation is
/// deliberately deferred: it needs the console SDK, which is a separate follow-up once
/// someone sits down with it.
/// </summary>
public sealed class XboxSDK : IPlatformSDK
{
    public TargetPlatform Platform => TargetPlatform.Xbox;
    public string Name => "Xbox SDK";
    public bool IsAvailable => false;
    public IReadOnlyList<TargetArchitecture> SupportedArchitectures => [TargetArchitecture.X64];

    public SDKInfo? Detect() => null;

    public IToolchain CreateToolchain(TargetArchitecture architecture, SDKInfo sdkInfo) =>
        throw new NotImplementedException(
            "Xbox toolchain requires the console SDK. Implement IToolchain here once the SDK is wired up.");
}
