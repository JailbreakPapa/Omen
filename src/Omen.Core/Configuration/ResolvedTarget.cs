// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Configuration;

/// <summary>
/// One concrete point in a resolved TargetMatrix - one value per dimension type present
/// in this combination. Built by TargetMatrix.Resolve(); consumed by BuildOrchestrator/
/// ProjectGenerationOrchestrator to construct one BuildContext per combination.
/// </summary>
public sealed class ResolvedTarget
{
    private readonly IReadOnlyDictionary<Type, Enum> _values;

    internal ResolvedTarget(IReadOnlyDictionary<Type, Enum> values) => _values = values;

    /// <summary>
    /// Constructs a ResolvedTarget directly from a set of dimension values, for the one
    /// legitimate case outside Omen.Core: a caller (e.g. BuildOrchestrator's no-matrix
    /// fallback) synthesizing a single combination itself rather than getting one back from
    /// TargetMatrix.Resolve(). Everywhere else, Resolve() is how a ResolvedTarget is made.
    /// </summary>
    public static ResolvedTarget Create(IReadOnlyDictionary<Type, Enum> values) => new(values);

    /// <summary>Every dimension value this combination carries, keyed by dimension type.</summary>
    public IReadOnlyDictionary<Type, Enum> Values => _values;

    public T Get<T>() where T : struct, Enum
    {
        if (!TryGet<T>(out var value))
            throw new InvalidOperationException($"ResolvedTarget has no value for dimension '{typeof(T).Name}'.");
        return value;
    }

    public bool TryGet<T>(out T value) where T : struct, Enum
    {
        if (_values.TryGetValue(typeof(T), out var boxed))
        {
            value = (T)boxed;
            return true;
        }
        value = default;
        return false;
    }

    public override string ToString() =>
        string.Join(", ", _values.OrderBy(kv => kv.Key.Name).Select(kv => $"{kv.Key.Name}={kv.Value}"));
}
