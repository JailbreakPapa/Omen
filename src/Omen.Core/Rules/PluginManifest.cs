using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Omen.Core.Rules;

/// <summary>
/// Reads an O3DE-style plugin.json. This file stays authoritative for a plugin's identity and
/// dependencies — it's read by tooling outside the build (Project Manager, plugin repo) — so
/// this is a reader only, never a writer, and PluginRules must not re-declare what's here.
/// </summary>
public sealed class PluginManifest
{
	public required string PluginName { get; init; }

	public required string Version { get; init; }

	public List<string> Dependencies { get; init; } = new List<string>();

	public List<string> Tags { get; init; } = new List<string>();

	public static PluginManifest Load(string pluginJsonPath)
	{
		if (!File.Exists(pluginJsonPath))
		{
			throw new FileNotFoundException("Plugin manifest not found: " + pluginJsonPath, pluginJsonPath);
		}
		using FileStream stream = File.OpenRead(pluginJsonPath);
		using JsonDocument doc = JsonDocument.Parse((Stream)stream, default(JsonDocumentOptions));
		JsonElement root = doc.RootElement;
		if (!root.TryGetProperty("plugin_name", out var nameEl))
		{
			throw new InvalidOperationException("'" + pluginJsonPath + "' is missing 'plugin_name'.");
		}
		if (nameEl.ValueKind != JsonValueKind.String)
		{
			throw new InvalidOperationException($"'{pluginJsonPath}' field 'plugin_name' must be a string, got {nameEl.ValueKind}.");
		}
		string pluginName = nameEl.GetString();
		string version = "0.0.0";
		if (root.TryGetProperty("version", out var versionEl))
		{
			if (versionEl.ValueKind != JsonValueKind.String)
			{
				throw new InvalidOperationException($"'{pluginJsonPath}' field 'version' must be a string, got {versionEl.ValueKind}.");
			}
			version = versionEl.GetString() ?? "0.0.0";
		}
		return new PluginManifest
		{
			PluginName = pluginName,
			Version = version,
			Dependencies = ReadStringArray(root, "dependencies", pluginJsonPath),
			Tags = ReadStringArray(root, "user_tags", pluginJsonPath)
		};
	}

	private static List<string> ReadStringArray(JsonElement root, string propertyName, string pluginJsonPath)
	{
		List<string> result = new List<string>();
		if (!root.TryGetProperty(propertyName, out var arrayEl) || arrayEl.ValueKind != JsonValueKind.Array)
		{
			return result;
		}
		int index = 0;
		foreach (JsonElement item in arrayEl.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.Object)
			{
				JsonElement uuidEl;
				if (item.TryGetProperty("name", out var namedEl) && namedEl.ValueKind == JsonValueKind.String)
				{
					AddIfNotEmpty(result, namedEl.GetString());
				}
				else if (item.TryGetProperty("uuid", out uuidEl) && uuidEl.ValueKind == JsonValueKind.String)
				{
					AddIfNotEmpty(result, uuidEl.GetString());
				}
				index++;
				continue;
			}
			if (item.ValueKind != JsonValueKind.String)
			{
				throw new InvalidOperationException($"'{pluginJsonPath}' array '{propertyName}[{index}]' must be a string or an object carrying 'name' or 'uuid', got {item.ValueKind}.");
			}
			string value = item.GetString();
			if (value != null)
			{
				result.Add(value);
			}
			index++;
		}
		return result;
	}

	private static void AddIfNotEmpty(List<string> values, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			values.Add(value);
		}
	}
}
