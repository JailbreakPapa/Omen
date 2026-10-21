using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Omen.Core.Graph;

namespace Omen.Core.Generators;

/// <summary>
/// Writes compile_commands.json for clangd/clang-tidy/editors other than Visual Studio,
/// sourced from the same command lines ActionGraphBuilder produced for the real build
/// (not a second, independent derivation of include paths and definitions).
/// </summary>
public static class CompileCommandsWriter
{
	private sealed class Entry
	{
		public required string Directory { get; init; }

		public required string Command { get; init; }

		public required string File { get; init; }
	}

	public static void Write(ActionGraph graph, string outputPath)
	{
		List<Entry> entries = (from a in graph.Actions
			where a.Type == ActionType.Compile && a.Inputs.Count > 0
			select new Entry
			{
				Directory = a.WorkingDirectory,
				Command = a.CommandLine,
				File = a.Inputs[0].Path
			}).ToList();
		string directory = Path.GetDirectoryName(outputPath);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}
		GeneratedFile.WriteAllText(outputPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		}));
	}
}
