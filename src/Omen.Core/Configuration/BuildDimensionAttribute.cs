// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Configuration;

/// <summary>
/// Marks an enum as a build-configuration dimension - an axis TargetMatrix.For&lt;T&gt;
/// can constrain and a ResolvedTarget/BuildContext can carry a value for. TargetPlatform,
/// TargetArchitecture, and BuildConfiguration carry this attribute as the three built-in
/// dimensions; a studio adds a custom axis (e.g. a RenderApi enum) by tagging its own enum
/// the same way - no core-repo edit required.
/// </summary>
[AttributeUsage(AttributeTargets.Enum)]
public sealed class BuildDimensionAttribute : Attribute;
