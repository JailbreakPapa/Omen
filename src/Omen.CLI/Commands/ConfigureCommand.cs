using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Omen.Core.Options;
using Omen.Core.Configuration;
using Omen.Executors.Orchestration;
using Spectre.Console;

namespace Omen.CLI.Commands;

/// <summary>
/// `omen configure` - the command-line half of the GUI's Options panel, and Omen's answer to
/// `cmake -D`/`cmake -L`. Discovering options is a Configure pass in its own right: it compiles
/// the rules and instantiates them, which is what runs the declarations.
/// </summary>
public static class ConfigureCommand
{
	public static Command Create()
	{
		Argument<string?> targetArgument = new Argument<string>("target", () => (string?)null, "Target name or .target.cs path. Omitted, the project's single target is used.");
		Option<string[]> setOption = new Option<string[]>(new string[2] { "--set", "-D" }, () => Array.Empty<string>(), "Set an option: -D NAME=VALUE. Repeatable. Values persist in Intermediate/omen-cache.json.")
		{
			AllowMultipleArgumentsPerToken = false
		};
		Option<string[]> unsetOption = new Option<string[]>(new string[2] { "--unset", "-U" }, () => Array.Empty<string>(), "Reset an option to its declared default. Repeatable.");
		Option<bool?> thirdPartyOption = new Option<bool?>("--third-party", "Include options declared by vendored third-party libraries (sets OMEN_THIRDPARTY_OPTIONS).");
		Option<bool> advancedOption = new Option<bool>("--advanced", "List advanced options too.");
		Option<string?> searchOption = new Option<string>(new string[2] { "--search", "-s" }, () => (string?)null, "List only options whose name or description contains this text.");
		Command command = new Command("configure", "List and set build options (Omen's cmake -L / cmake -D)") { targetArgument, setOption, unsetOption, thirdPartyOption, advancedOption, searchOption };
		var configuration = new Option<BuildConfiguration>(new[] { "--configuration", "-c" }, () => BuildConfiguration.Development, "Configuration used to discover options and render configuration files");
		var platform = new Option<TargetPlatform?>(new[] { "--platform", "-p" }, "Target platform");
		var architecture = new Option<TargetArchitecture?>(new[] { "--arch", "-a" }, "Target architecture");
		command.AddOption(configuration); command.AddOption(platform); command.AddOption(architecture);
		command.SetHandler(async (InvocationContext context) =>
		{
			string? valueForArgument = context.ParseResult.GetValueForArgument(targetArgument);
			string[] sets = context.ParseResult.GetValueForOption(setOption) ?? Array.Empty<string>();
			string[] unsets = context.ParseResult.GetValueForOption(unsetOption) ?? Array.Empty<string>();
			bool? thirdParty = context.ParseResult.GetValueForOption(thirdPartyOption);
			bool advanced = context.ParseResult.GetValueForOption(advancedOption);
			string search = context.ParseResult.GetValueForOption(searchOption);
			context.ExitCode = await RunAsync(valueForArgument, sets, unsets, thirdParty, advanced, search, context.ParseResult.GetValueForOption(configuration), context.ParseResult.GetValueForOption(platform), context.ParseResult.GetValueForOption(architecture));
		});
		return command;
	}

	private static async Task<int> RunAsync(string? target, string[] sets, string[] unsets, bool? thirdParty, bool advanced, string? search, BuildConfiguration configuration, TargetPlatform? platform, TargetArchitecture? architecture)
	{
		var (targetFile, allTargetFiles) = await BuildCommand.ResolveTargetFileAsync(target);
		if (targetFile == null)
		{
			AnsiConsole.MarkupLine((allTargetFiles.Count > 1) ? ("[red]Several targets here - name one:[/] " + string.Join(", ", allTargetFiles.Select((string f) => Path.GetFileName(f).Replace(".target.cs", "")).Order(StringComparer.OrdinalIgnoreCase)).EscapeMarkup()) : "[red]No target file found.[/] Run this from a project directory, or name a .target.cs path.");
			return 1;
		}
		OptionsOrchestrator orchestrator = new OptionsOrchestrator();
		Dictionary<string, string> changes = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (string assignment in sets)
		{
			int separator = assignment.IndexOf('=');
			if (separator <= 0)
			{
				AnsiConsole.MarkupLine("[red]Not a NAME=VALUE assignment:[/] " + assignment.EscapeMarkup());
				return 1;
			}
			changes[assignment.Substring(0, separator)] = assignment.Substring(separator + 1);
		}
		if (thirdParty.HasValue)
		{
			bool includeThirdParty = thirdParty == true;
			changes["OMEN_THIRDPARTY_OPTIONS"] = (includeThirdParty ? "true" : "false");
		}
		if (changes.Count > 0)
		{
			orchestrator.SaveOptions(targetFile, changes);
		}
		if (unsets.Length != 0)
		{
			orchestrator.ResetOptions(targetFile, unsets);
		}
		Progress<OrchestratorEvent> events = new Progress<OrchestratorEvent>((OrchestratorEvent e) =>
		{
			OrchestratorEventLevel level = e.Level;
			if ((uint)(level - 1) <= 1u)
			{
				AnsiConsole.MarkupLine($"[{((e.Level == OrchestratorEventLevel.Error) ? "red" : "yellow")}]{e.Message.EscapeMarkup()}[/]");
			}
		});
		IReadOnlyList<BuildOptionDeclaration> declarations = await AnsiConsole.Status().StartAsync("Configuring...", (StatusContext _) => orchestrator.DiscoverAsync(new OptionsOrchestratorRequest
		{
			TargetFile = targetFile, Configuration = configuration,
			Platform = platform, Architecture = architecture
		}, events));
		if (declarations == null)
		{
			return 1;
		}
		List<BuildOptionDeclaration> shown = (from d in declarations
			where advanced || !d.IsAdvanced
			where string.IsNullOrWhiteSpace(search) || d.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || d.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
			select d).OrderBy((BuildOptionDeclaration d) => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
		Table table = new Table().Border(TableBorder.Rounded);
		table.AddColumn("Option");
		table.AddColumn("Type");
		table.AddColumn("Value");
		table.AddColumn("Description");
		foreach (BuildOptionDeclaration declaration in shown)
		{
			bool modified = !string.Equals(declaration.EffectiveValue, declaration.DefaultValue, StringComparison.Ordinal);
			table.AddRow(modified ? ("[yellow]" + declaration.Name.EscapeMarkup() + "[/]") : declaration.Name.EscapeMarkup(), declaration.Type.ToString(), declaration.EffectiveValue.EscapeMarkup(), declaration.Description.EscapeMarkup());
		}
		AnsiConsole.Write(table);
		int n = declarations.Count((BuildOptionDeclaration d) => d.IsAdvanced);
		int hiddenAdvanced = ((!advanced) ? n : 0);
		AnsiConsole.MarkupLine($"[dim]{shown.Count} of {declarations.Count} option(s)" + ((hiddenAdvanced > 0) ? $"; {hiddenAdvanced} advanced hidden (--advanced)" : "") + (declarations.Any((BuildOptionDeclaration d) => d.Name == "OMEN_THIRDPARTY_OPTIONS") ? "; --third-party adds vendored third-party options" : "") + "[/]");
		return 0;
	}
}
