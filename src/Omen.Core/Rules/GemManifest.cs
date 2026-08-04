// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;

namespace Omen.Core.Rules;

/// <summary>
/// Reads an O3DE-style gem.json. This file stays authoritative for a gem's identity and
/// dependencies — it's read by tooling outside the build (Project Manager, gem repo) — so
/// this is a reader only, never a writer, and GemRules must not re-declare what's here.
/// </summary>
public sealed class GemManifest
{
    public required string GemName { get; init; }
    public required string Version { get; init; }
    public List<string> Dependencies { get; init; } = [];
    public List<string> Tags { get; init; } = [];

    public static GemManifest Load(string gemJsonPath)
    {
        if (!File.Exists(gemJsonPath))
            throw new FileNotFoundException($"Gem manifest not found: {gemJsonPath}", gemJsonPath);

        using var stream = File.OpenRead(gemJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (!root.TryGetProperty("gem_name", out var nameEl))
            throw new InvalidOperationException($"'{gemJsonPath}' is missing 'gem_name'.");

        if (nameEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"'{gemJsonPath}' field 'gem_name' must be a string, got {nameEl.ValueKind}.");

        var gemName = nameEl.GetString();

        var version = "0.0.0";
        if (root.TryGetProperty("version", out var versionEl))
        {
            if (versionEl.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"'{gemJsonPath}' field 'version' must be a string, got {versionEl.ValueKind}.");
            version = versionEl.GetString() ?? "0.0.0";
        }

        return new GemManifest
        {
            GemName = gemName,
            Version = version,
            Dependencies = ReadStringArray(root, "dependencies", gemJsonPath),
            Tags = ReadStringArray(root, "user_tags", gemJsonPath)
        };
    }

    private static List<string> ReadStringArray(JsonElement root, string propertyName, string gemJsonPath)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(propertyName, out var arrayEl) || arrayEl.ValueKind != JsonValueKind.Array)
            return result;

        var index = 0;
        foreach (var item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException(
                    $"'{gemJsonPath}' array '{propertyName}[{index}]' must be a string, got {item.ValueKind}.");

            var value = item.GetString();
            if (value != null) result.Add(value);
            index++;
        }
        return result;
    }
}
