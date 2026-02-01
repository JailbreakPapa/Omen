// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Configuration;

/// <summary>
/// Target platform for compilation.
/// </summary>
public enum TargetPlatform
{
    Unknown,
    Windows,
    Linux,
    FreeBSD,
    Android,
    iOS
}
/// <summary>
/// NDA Restricted target platforms for compilation.
/// </summary>

public enum NDAPlatforms
{
    PS4,
    PS5,
    XB1,
    XBX,
    NS1,
    NS2,
}

/// <summary>
/// Target architecture for compilation.
/// </summary>
public enum TargetArchitecture
{
    Unknown,
    X64,
    X86,
    ARM64,
    ARMv7
}

/// <summary>
/// Build configuration type.
/// </summary>
public enum BuildConfiguration
{
    Debug,
    Development,
    Release,
    Shipping
}

/// <summary>
/// Type of build target.
/// </summary>
public enum TargetType
{
    Executable,
    StaticLibrary,
    SharedLibrary,
    HeaderOnly
}

/// <summary>
/// Type of module.
/// </summary>
public enum ModuleType
{
    Runtime,
    Editor,
    ThirdParty,
    Plugin,
    Test
}

/// <summary>
/// Programming language for the module.
/// </summary>
public enum ModuleLanguage
{
    Cpp,
    CSharp
}

/// <summary>
/// Precompiled header usage mode.
/// </summary>
public enum PCHUsage
{
    None,
    UseExplicitOrShared,
    UseSharedPCHs,
    NoPCHs
}

/// <summary>
/// Optimization level for compilation.
/// </summary>
public enum OptimizationLevel
{
    Disabled,
    Debug,
    Development,
    Shipping,
    Size,
    SizeAndSpeed
}

/// <summary>
/// Warning level for compilation.
/// </summary>
public enum WarningLevel
{
    Off,
    Level1,
    Level2,
    Level3,
    Level4,
    EnableAll
}

/// <summary>
/// Link type for targets.
/// </summary>
public enum LinkType
{
    Default,
    Monolithic,
    Modular
}

/// <summary>
/// C++ language standard.
/// </summary>
public enum CppStandard
{
    Cpp14,
    Cpp17,
    Cpp20,
    Cpp23,
    Latest
}

/// <summary>
/// C language standard.
/// </summary>
public enum CStandard
{
    C11,
    C17,
    C23,
    Latest
}

/// <summary>
/// C# language version.
/// </summary>
public enum CSharpVersion
{
    CSharp10,
    CSharp11,
    CSharp12,
    CSharp13,
    Latest
}

/// <summary>
/// .NET target framework.
/// </summary>
public enum DotNetFramework
{
    Net60,
    Net70,
    Net80,
    Net90,
    NetStandard20,
    NetStandard21
}

/// <summary>
/// Qt major version.
/// </summary>
public enum QtVersion
{
    None,
    Qt5,
    Qt6
}
