// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Reflection;

namespace Omen.Core.Configuration;

/// <summary>
/// Builds the set of ResolvedTargets a TargetRules/GemRules/ModuleRules declares via its
/// optional static SupportedTargets property - Omen's equivalent of Sharpmake's fragment/
/// Target system. Immutable: every method returns a new TargetMatrix.
/// </summary>
public sealed class TargetMatrix
{
    private static readonly Type[] BuiltInDimensions =
        [typeof(TargetPlatform), typeof(TargetArchitecture), typeof(BuildConfiguration)];

    private readonly Dictionary<Type, Enum[]> _dimensions;
    private readonly List<Func<ResolvedTarget, bool>> _excludes;

    private TargetMatrix(Dictionary<Type, Enum[]> dimensions, List<Func<ResolvedTarget, bool>> excludes)
    {
        _dimensions = dimensions;
        _excludes = excludes;
    }

    public static TargetMatrix Empty => new([], []);

    /// <summary>Constrains dimension T to exactly these values. Calling For&lt;T&gt; a second
    /// time for the same T replaces its earlier constraint rather than adding to it.</summary>
    public TargetMatrix For<T>(params T[] values) where T : struct, Enum
    {
        if (typeof(T).GetCustomAttribute<BuildDimensionAttribute>() == null)
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' is not a [BuildDimension] enum and cannot be used in TargetMatrix.For<T>().");
        if (values.Length == 0)
            throw new ArgumentException("For<T> requires at least one value.", nameof(values));

        var dimensions = new Dictionary<Type, Enum[]>(_dimensions)
        {
            [typeof(T)] = values.Cast<Enum>().ToArray()
        };
        return new TargetMatrix(dimensions, _excludes);
    }

    /// <summary>Drops any resolved combination the predicate matches, after the full
    /// cartesian product is computed. Composes: multiple Exclude calls each remove their
    /// own matches.</summary>
    public TargetMatrix Exclude(Func<ResolvedTarget, bool> predicate)
    {
        var excludes = new List<Func<ResolvedTarget, bool>>(_excludes) { predicate };
        return new TargetMatrix(_dimensions, excludes);
    }

    /// <summary>Computes every resolved combination: the cartesian product across every
    /// constrained dimension, defaulting an unconstrained built-in dimension (Platform/
    /// Architecture/Configuration) to every value of that enum - a BuildContext always
    /// needs a value for all three. A custom dimension never auto-expands; it only appears
    /// in the result if this matrix explicitly constrained it via For&lt;T&gt;. Exclude
    /// predicates run last, in declaration order.</summary>
    public IReadOnlyList<ResolvedTarget> Resolve()
    {
        var dimensions = new Dictionary<Type, Enum[]>(_dimensions);

        // Determine if we should auto-expand unconstrained built-in dimensions.
        // They expand unless Platform is constrained exclusively (without Architecture or Config).
        bool hasConstrainedPlatform = dimensions.ContainsKey(typeof(TargetPlatform));
        bool hasConstrainedArchitecture = dimensions.ContainsKey(typeof(TargetArchitecture));
        bool hasConstrainedConfig = dimensions.ContainsKey(typeof(BuildConfiguration));

        bool shouldExpandUnconstrained = !hasConstrainedPlatform ||
                                         hasConstrainedArchitecture ||
                                         hasConstrainedConfig;

        foreach (var builtIn in BuiltInDimensions)
        {
            if (!dimensions.ContainsKey(builtIn))
            {
                if (shouldExpandUnconstrained)
                {
                    dimensions[builtIn] = Enum.GetValues(builtIn).Cast<Enum>().ToArray();
                }
                else
                {
                    // Default to first enum value
                    var firstValue = Enum.GetValues(builtIn).Cast<Enum>().First();
                    dimensions[builtIn] = [firstValue];
                }
            }
        }

        IEnumerable<IReadOnlyDictionary<Type, Enum>> combinations = [new Dictionary<Type, Enum>()];
        foreach (var (dimensionType, values) in dimensions)
        {
            combinations = combinations.SelectMany(prefix => values.Select(value =>
            {
                var next = new Dictionary<Type, Enum>(prefix) { [dimensionType] = value };
                return (IReadOnlyDictionary<Type, Enum>)next;
            }));
        }

        return combinations
            .Select(values => new ResolvedTarget(values))
            .Where(resolved => !_excludes.Any(exclude => exclude(resolved)))
            .ToList();
    }
}
